namespace DeveloperInTheFlow.FactoryGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Common.Logging;

    using CSScriptLib;

    using DeveloperInTheFlow.FactoryGenerator.Models;
    using DeveloperInTheFlow.FactoryGenerator.Services;

    using Humanizer;

    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    using Attribute = DeveloperInTheFlow.FactoryGenerator.Models.Attribute;
    using Project = Microsoft.CodeAnalysis.Project;

    public class FactoryGenerator
    {
        #region Static Fields

        private static readonly ILog Logger = LogManager.GetLogger<FactoryGenerator>();

        #endregion

        #region Fields

        private readonly string[] attributeImportList;

        private readonly ConstructorBuilderService constructorBuilderService;

        private readonly FieldsBuilderService fieldsBuilderService;

        private readonly bool forceGeneration;

        private readonly GenericTypeBuilderService genericTypeBuilderService;

        private readonly MethodsBuilderService methodsBuilderService;

        private readonly string templatePath;

        private readonly string version = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;

        private readonly Workspace workspace;

        private Lazy<IEvaluator> csscriptEvaluator = new(() =>
                                                         {
                                                             CSScript.EvaluatorConfig.ReferenceDomainAssemblies = true;
                                                             return CSScript.Evaluator.ReferenceAssembly(typeof(JObject).Assembly)
                                                                            .ReferenceAssembly(typeof(System.Linq.Enumerable).Assembly)
                                                                            .ReferenceAssembly(typeof(System.Text.RegularExpressions.Regex).Assembly);
                                                         });

        private Solution solution;

        #endregion

        #region Constructors and Destructors

        [DebuggerStepThrough]
        public FactoryGenerator(
            Workspace workspace,
            Solution solution,
            IEnumerable<string> attributeImportList,
            bool writeXmlDoc,
            string templatePath,
            bool forceGeneration)
        {
            this.workspace = workspace;
            this.solution = solution;

            var importList = attributeImportList as string[] ?? attributeImportList.ToArray();
            this.attributeImportList = importList;
            this.templatePath = templatePath;
            this.forceGeneration = forceGeneration;

            var parameterSymbolService = new ParameterSymbolBuilderService();
            var argumentBuilderService = new ArgumentsBuilderService(parameterSymbolService);

            this.fieldsBuilderService = new FieldsBuilderService(parameterSymbolService);
            this.genericTypeBuilderService = new GenericTypeBuilderService();
            this.constructorBuilderService = new ConstructorBuilderService(importList, argumentBuilderService);
            this.methodsBuilderService = new MethodsBuilderService(this.genericTypeBuilderService, argumentBuilderService, parameterSymbolService, writeXmlDoc);
        }

        #endregion

        #region Public Methods and Operators

        public async Task ExecuteAsync()
        {
            var chrono = Stopwatch.StartNew();

            var projectDependencyGraph = this.solution.GetProjectDependencyGraph();
            var projectCount = this.solution.Projects.Count();
            var existingFactoriesTasksList = new List<Task<ICollection<string>>>(projectCount);
            var generatedFactoriesTasksList = new List<Task<ICollection<string>>>(projectCount);
            var hashesFile = Path.Combine(Path.GetDirectoryName(this.solution.FilePath), "generatedFactoryHashes.json");

            Dictionary<string, Dictionary<string, string>> fileHashesPerProject = new Dictionary<string, Dictionary<string, string>>();
            if (File.Exists(hashesFile) && !this.forceGeneration)
            {
                fileHashesPerProject = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(hashesFile));
            }

            Dictionary<string, bool> isSdkStyleProjectDict = new Dictionary<string, bool>();

            foreach (var projectId in projectDependencyGraph.GetTopologicallySortedProjects())
            {
                var project = this.solution.GetProject(projectId);
                var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
                var isSdkStyleProject = await IsSdkStyleProjectAsync(project.FilePath).ConfigureAwait(false);
                isSdkStyleProjectDict[project.FilePath] = isSdkStyleProject;

                Logger.InfoFormat("Processing {0}...", project.Name);

                var catalogTask = CatalogGeneratedFactoriesInProjectAsync(compilation);
                var generateFactoriesTask = this.GenerateFactoriesInProjectAsync(compilation, isSdkStyleProject, fileHashesPerProject);

                existingFactoriesTasksList.Add(catalogTask);
                generatedFactoriesTasksList.Add(generateFactoriesTask);
            }

            await Task.WhenAll(existingFactoriesTasksList.Cast<Task>().Concat(generatedFactoriesTasksList));

            var existingGeneratedFactoryFilePaths = existingFactoriesTasksList.SelectMany(task => task.Result).ToArray();
            var generatedFactoryFilePaths = generatedFactoriesTasksList.SelectMany(task => task.Result).ToArray();

            var newFactoryFilePaths = generatedFactoryFilePaths.Except(existingGeneratedFactoryFilePaths, StringComparer.InvariantCultureIgnoreCase).ToArray();
            var obsoleteFactoryFilePaths = existingGeneratedFactoryFilePaths.Except(generatedFactoryFilePaths, StringComparer.InvariantCultureIgnoreCase).ToArray();
            this.RemoveObsoleteFactoriesFromSolution(obsoleteFactoryFilePaths, isSdkStyleProjectDict);

            this.workspace.TryApplyChanges(this.solution);

            this.RemoveObsoleteHashes(fileHashesPerProject);

            File.WriteAllText(hashesFile, JsonConvert.SerializeObject(fileHashesPerProject, Formatting.Indented));

            chrono.Stop();

            LogCodeGenerationStatistics(chrono, generatedFactoryFilePaths, obsoleteFactoryFilePaths, newFactoryFilePaths);
        }

        #endregion

        #region Methods

        private static async Task<ICollection<string>> CatalogGeneratedFactoriesInProjectAsync(
            Compilation compilation)
        {
            var generatedFactoriesCatalog = new List<string>(16);

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var syntaxRootNode = await syntaxTree.GetRootAsync();
                var generatedFactoryClassDeclarations =
                    syntaxRootNode.DescendantNodesAndSelf(syntaxNode => !(syntaxNode is ClassDeclarationSyntax))
                                  .OfType<ClassDeclarationSyntax>()
                                  .Where(
                                         classDeclarationSyntax => classDeclarationSyntax.AttributeLists.Count > 0 && classDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes).Any(
                                                                                                                                                                                              x =>
                                                                                                                                                                                              {
                                                                                                                                                                                                  var attributeClassName = x.Name.ToString();
                                                                                                                                                                                                  return attributeClassName == "global::System.CodeDom.Compiler.GeneratedCode";
                                                                                                                                                                                              }))
                                  .ToArray();

                if (generatedFactoryClassDeclarations.Any())
                {
                    var generatedFactoryInterfaces = generatedFactoryClassDeclarations.Select(generatedFactoryClassDeclaration => generatedFactoryClassDeclaration.SyntaxTree.FilePath);

                    generatedFactoriesCatalog.AddRange(generatedFactoryInterfaces);
                }
            }

            return generatedFactoriesCatalog;
        }

        private static bool CompareParameters(
            IParameterSymbol parameter1,
            IParameterSymbol parameter2)
        {
            return (parameter1.Name == parameter2.Name);
        }

        private static SyntaxList<UsingDirectiveSyntax> FilterOutUsings(
            SyntaxList<UsingDirectiveSyntax> usings,
            string[] usingsToFilterOut)
        {
            for (;;)
            {
                var index = usings.IndexOf(usingDirectiveSyntax => usingDirectiveSyntax.Alias == null && Array.IndexOf(usingsToFilterOut, usingDirectiveSyntax.Name.ToString()) >= 0);
                if (index < 0)
                {
                    return usings;
                }

                usings = usings.RemoveAt(index);
            }
        }

        private static Document FindDocumentFromPath(
            Solution solution,
            string filePath)
        {
            return solution.Projects
                           .Select(project => project.Documents.FirstOrDefault(d => d.FilePath == filePath))
                           .FirstOrDefault(document => document != null);
        }

        private static string GetDeclarationFullName(
            TypeDeclarationSyntax typeDeclarationSyntax)
        {
            var typeParameterCount = typeDeclarationSyntax.TypeParameterList == null
                                         ? 0
                                         : typeDeclarationSyntax.TypeParameterList.Parameters.Count;
            var fullyQualifiedName = "{0}.{1}{2}".FormatWith(
                                                             GetDeclarationNamespaceFullName(typeDeclarationSyntax),
                                                             typeDeclarationSyntax.Identifier.ValueText,
                                                             typeParameterCount == 0
                                                                 ? string.Empty
                                                                 : "`{0}".FormatWith(typeParameterCount));

            return fullyQualifiedName;
        }

        private static string GetDeclarationNamespaceFullName(
            CSharpSyntaxNode typeDeclarationSyntax)
        {
            var namespaceDeclarationSyntax = typeDeclarationSyntax.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
            return namespaceDeclarationSyntax.Name.ToString();
        }

        private static AttributeSyntax GetFactoryAttributeFromClassDeclaration(
            ClassDeclarationSyntax concreteClassDeclarationSyntax)
        {
            AttributeSyntax factoryAttribute;
            try
            {
                factoryAttribute = concreteClassDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes)
                                                                 .SingleOrDefault(
                                                                                  a =>
                                                                                  {
                                                                                      var attributeClassFullName = a.Name.NormalizeWhitespace().ToFullString();
                                                                                      return attributeClassFullName.EndsWith("GenerateFactoryAttribute") || attributeClassFullName.EndsWith("GenerateFactory");
                                                                                  });
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException("Could not read GenerateFactoryAttribute from {0} type.".FormatWith(concreteClassDeclarationSyntax), e);
            }

            return factoryAttribute;
        }

        private static string GetFactoryClassGenericName(
            ClassDeclarationSyntax concreteTypeDeclarationSyntax,
            INamedTypeSymbol factoryInterfaceTypeSymbol)
        {
            var factoryClassName = "{0}Factory{1}".FormatWith(concreteTypeDeclarationSyntax.Identifier.ValueText, GetTypeParametersDeclaration(factoryInterfaceTypeSymbol.TypeParameters));

            return factoryClassName;
        }

        private static string GetSafeFileName(
            string identifier)
        {
            return identifier.Contains("<")
                       ? identifier.Substring(0, identifier.IndexOf("<", StringComparison.Ordinal))
                       : identifier;
        }

        private static (ITypeSymbol returnType, IMethodSymbol symbol)[] GetSuitableFactoryInterfaceMethods(
            INamedTypeSymbol concreteClassTypeSymbol,
            INamedTypeSymbol factoryInterfaceTypeSymbol,
            INamedTypeSymbol[] genericArgumentTypeSymbols)
        {
            return factoryInterfaceTypeSymbol
                   .AllInterfaces
                   .Add(factoryInterfaceTypeSymbol)
                   .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
                   .Select(methodSymbol => (returnType: ResolveGenericArgument(factoryInterfaceTypeSymbol, methodSymbol.ReturnType, genericArgumentTypeSymbols), symbol: methodSymbol))
                   .Where(methodSymbol => concreteClassTypeSymbol.AllInterfaces.Select(i => i.OriginalDefinition).Contains(methodSymbol.returnType.OriginalDefinition, SymbolEqualityComparer.Default))
                   .ToArray();
        }

        private static string GetTypeParametersDeclaration(
            ImmutableArray<ITypeParameterSymbol> typeParameterSymbols)
        {
            var typeParameters = string.Empty;

            if (typeParameterSymbols.Length > 0)
            {
                typeParameters = "<{0}>".FormatWith(string.Join(" ,", typeParameterSymbols.Select(t => t.Name)));
            }

            return typeParameters;
        }

        private static string GetXmlDocSafeTypeName(
            string typeName)
        {
            return typeName.Replace("<", "{").Replace(">", "}");
        }

        private static async Task<bool> IsSdkStyleProjectAsync(
            string path)
        {
            using var streamReader = new StreamReader(File.OpenRead(path));
            while (true)
            {
                var line = await streamReader.ReadLineAsync().ConfigureAwait(false);
                if (line.Contains("<Project"))
                {
                    return line.Contains("Sdk=\"Microsoft.NET.Sdk");
                }
            }

            throw new NotSupportedException($"Not able to detect project style for {path}!");
        }

        private static bool IsTypeDeclarationSyntaxFactoryTarget(
            ClassDeclarationSyntax classDeclarationSyntax)
        {
            var attributeListSyntaxes = classDeclarationSyntax.AttributeLists;

            return attributeListSyntaxes.Count > 0 &&
                   attributeListSyntaxes.SelectMany(a => a.Attributes).Any(
                                                                           x =>
                                                                           {
                                                                               var qualifiedNameSyntax = x.Name as Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax;
                                                                               var attributeClassName = qualifiedNameSyntax != null
                                                                                                            ? qualifiedNameSyntax.Right.Identifier.ToString()
                                                                                                            : x.Name.ToString();
                                                                               return attributeClassName == "GenerateFactory" || attributeClassName == "GenerateFactoryAttribute";
                                                                           });
        }

        private static void LogCodeGenerationStatistics(
            Stopwatch chrono,
            string[] generatedFactoryFilePaths,
            string[] obsoleteFactoryFilePaths,
            string[] newFactoryFilePaths)
        {
            var ellapsedTime = TimeSpan.FromMilliseconds(chrono.ElapsedMilliseconds);
            var statisticsFormat = obsoleteFactoryFilePaths.Any() || newFactoryFilePaths.Any()
                                       ? "{0} (+{1}/-{2})"
                                       : "{0}";
            var factoryStatistics = statisticsFormat.FormatWith("factory".ToQuantity(generatedFactoryFilePaths.Length), newFactoryFilePaths.Length, obsoleteFactoryFilePaths.Length);

            Logger.InfoFormat("Generated {0} in {1}.", factoryStatistics, ellapsedTime.Humanize(2));
        }

        private static ITypeSymbol ResolveGenericArgument(
            INamedTypeSymbol factoryInterfaceTypeSymbol,
            ITypeSymbol methodReturnType,
            INamedTypeSymbol[] genericArgumentTypeSymbols)
        {
            if (methodReturnType is INamedTypeSymbol)
            {
                return methodReturnType;
            }

            var typeArgumentIndex = factoryInterfaceTypeSymbol.TypeArguments.IndexOf(methodReturnType);

            return typeArgumentIndex >= 0
                       ? genericArgumentTypeSymbols[typeArgumentIndex]
                       : methodReturnType;
        }

        private static IMethodSymbol SelectConstructorFromFactoryMethod(
            IMethodSymbol factoryMethod,
            INamedTypeSymbol concreteClassSymbol)
        {
            var factoryMethodParameters = factoryMethod.Parameters;
            var instanceConstructors = concreteClassSymbol.InstanceConstructors
                                                          .Where(c => c.DeclaredAccessibility == Accessibility.Public || c.DeclaredAccessibility == Accessibility.Internal)
                                                          .ToArray();

            try
            {
                var matchedInstanceConstructors = instanceConstructors.GroupBy(
                                                                               c => factoryMethodParameters.Select(fmp => fmp.Name)
                                                                                                           .Count(c.Parameters.Select(cp => cp.Name).Contains))
                                                                      .ToArray();
                var selectedGrouping = matchedInstanceConstructors.SingleOrDefault(g => g.Key == factoryMethodParameters.Length);
                if (selectedGrouping == null)
                {
                    selectedGrouping = matchedInstanceConstructors.OrderBy(g => g.Key - factoryMethodParameters.Length).First();
                }

                var selectedConstructor = selectedGrouping.First();

                return selectedConstructor;
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException("Could not select a constructor from {0} type with the following parameters: ({1}).".FormatWith(concreteClassSymbol.Name, string.Join(", ", factoryMethodParameters.Select(x => x.Name))), e);
            }
        }

        private JObject Transform(
            JObject factoryFile,
            string transformationScriptPath)
        {
            dynamic script = this.csscriptEvaluator.Value.LoadFile(transformationScriptPath);
            var @out = script.Transform(factoryFile);

            return @out;
        }

        private static bool VerifyAndAddHash(
            Dictionary<string, Dictionary<string, string>> fileHashesPerProject,
            string fileHash,
            Project project,
            string relativeFilePath)
        {
            var identifier = project.FilePath.Replace(Path.GetDirectoryName(project.Solution.FilePath), string.Empty).TrimStart('\\');
            if (!fileHashesPerProject.ContainsKey(identifier))
            {
                fileHashesPerProject.Add(identifier, new Dictionary<string, string>());
            }

            if (fileHashesPerProject[identifier].ContainsKey(relativeFilePath))
            {
                if (fileHashesPerProject[identifier][relativeFilePath] == fileHash)
                {
                    return true;
                }

                fileHashesPerProject[identifier][relativeFilePath] = fileHash;
            }
            else
            {
                fileHashesPerProject[identifier].Add(relativeFilePath, fileHash);
            }

            return false;
        }

        private async Task<ICollection<string>> GenerateFactoriesInProjectAsync(
            Compilation compilation,
            bool isSdkStyleProject,
            Dictionary<string, Dictionary<string, string>> fileHashesPerProject)
        {
            var generatedFactoriesList = new List<string>();

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var syntaxRootNode = await syntaxTree.GetRootAsync();
                var classDeclarations =
                    syntaxRootNode.DescendantNodesAndSelf(syntaxNode => !(syntaxNode is ClassDeclarationSyntax))
                                  .OfType<ClassDeclarationSyntax>()
                                  .Where(IsTypeDeclarationSyntaxFactoryTarget)
                                  .ToArray();

                if (classDeclarations.Any())
                {
                    foreach (var classDeclarationSyntax in classDeclarations)
                    {
                        var fullyQualifiedMetadataName = GetDeclarationFullName(classDeclarationSyntax);
                        var resolvedConcreteClassType = compilation.GetTypeByMetadataName(fullyQualifiedMetadataName);

                        var (generatedFilePath, generated) = this.GenerateFactoryForClass(classDeclarationSyntax, resolvedConcreteClassType, compilation, isSdkStyleProject, fileHashesPerProject);
                        generatedFactoriesList.Add(generatedFilePath);

                        if (generated)
                        {
                            Logger.Debug(new string('-', 80));
                        }
                    }
                }
            }

            return generatedFactoriesList;
        }

        private (string, bool) GenerateFactoryForClass(
            ClassDeclarationSyntax concreteClassDeclarationSyntax,
            INamedTypeSymbol concreteClassTypeSymbol,
            Compilation compilation,
            bool isSdkStyleProject,
            Dictionary<string, Dictionary<string, string>> fileHashesPerProject)
        {
            var factoryAttribute = GetFactoryAttributeFromClassDeclaration(concreteClassDeclarationSyntax);

            INamedTypeSymbol factoryInterfaceTypeSymbol;

            var genericArgumentTypeSymbols = new INamedTypeSymbol[0];

            if (factoryAttribute.ArgumentList != null && factoryAttribute.ArgumentList.Arguments.Any())
            {
                var typeOfArgument = factoryAttribute.ArgumentList.Arguments.Single();

                var usings = concreteClassDeclarationSyntax.Parent.DescendantNodes()
                                                           .OfType<UsingDirectiveSyntax>()
                                                           .Select(syntax => syntax.Name.NormalizeWhitespace().ToFullString())
                                                           .ToArray();

                var namespaces = usings.Concat(new[] { GetDeclarationNamespaceFullName(concreteClassDeclarationSyntax) });

                var typeName = ((TypeOfExpressionSyntax)typeOfArgument.Expression).Type.ToFullString();

                if (typeName.EndsWith(">"))
                {
                    var genericsPostfix = typeName.Substring(typeName.IndexOf("<", StringComparison.InvariantCulture));
                    var genericArguments = genericsPostfix.Trim('<', '>').Split(',');
                    var genericArgumentCount = genericArguments.Length;

                    genericArgumentTypeSymbols = genericArguments.SelectMany(
                                                                             arg => namespaces.Select(ns => $"{ns}.{arg}")
                                                                                              .Concat(new[] { arg })
                                                                                              .Select(compilation.GetTypeByMetadataName)
                                                                                              .Where(o => o != null)
                                                                                              .DefaultIfEmpty(null))
                                                                 .ToArray();

                    if (genericArgumentTypeSymbols.Length != genericArgumentCount)
                    {
                        throw new InvalidOperationException("Not all generic types for {0} have been found".FormatWith(typeName));
                    }

                    typeName = typeName.Replace(genericsPostfix, $"`{genericArgumentCount}");
                }

                var fulltypeNames = namespaces.Select(ns => $"{ns}.{typeName}").Concat(new[] { typeName });

                factoryInterfaceTypeSymbol = fulltypeNames.Select(compilation.GetTypeByMetadataName).FirstOrDefault(o => o != null);

                if (factoryInterfaceTypeSymbol == null)
                {
                    throw new InvalidOperationException("Factory {0} type couldn't be resolved in {1}.".FormatWith(typeName, GetDeclarationFullName(concreteClassDeclarationSyntax)));
                }

                if (factoryInterfaceTypeSymbol.IsUnboundGenericType)
                {
                    factoryInterfaceTypeSymbol = factoryInterfaceTypeSymbol.ConstructedFrom;
                }
            }
            else
            {
                throw new InvalidOperationException("Factory type must be specified in {0} on {1}.".FormatWith(factoryAttribute.Name.ToFullString(), GetDeclarationFullName(concreteClassDeclarationSyntax)));
            }

            var factoryClassGenericName = GetFactoryClassGenericName(concreteClassDeclarationSyntax, factoryInterfaceTypeSymbol);
            var factoryInterfaceFullName = factoryInterfaceTypeSymbol.ToString();

            var (project, typeDeclarationDocument) = this.solution.Projects.Select(proj => (proj, doc: proj.GetDocument(concreteClassDeclarationSyntax.SyntaxTree))).First(t => t.doc != null);

            var folders = string.Join(@"\", typeDeclarationDocument.Folders);
            var safeFileName = GetSafeFileName(factoryClassGenericName);
            var fileName = "{0}.Generated.cs".FormatWith(safeFileName);
            var generatedFilePath = Path.Combine(Path.GetDirectoryName(project.FilePath), folders, fileName);
            var relativeFilePath = Path.Combine(folders, typeDeclarationDocument.Name);

            using SHA1Managed sha1 = new SHA1Managed();
            var hash = sha1.ComputeHash(File.ReadAllBytes(typeDeclarationDocument.FilePath));
            var fileHashBuilder = new StringBuilder(2 * hash.Length);
            foreach (byte b in hash)
            {
                fileHashBuilder.AppendFormat("{0:X2}", b);
            }

            var fileHash = fileHashBuilder.ToString();
            if (VerifyAndAddHash(fileHashesPerProject, fileHash, project, relativeFilePath) && File.Exists(generatedFilePath))
            {
                return (generatedFilePath, false);
            }

            Logger.DebugFormat("[{0}]", string.Join(", ", concreteClassDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes).Select(x => x.Name)));
            Logger.Debug(concreteClassDeclarationSyntax.Identifier.Value);

            Logger.InfoFormat("Rendering factory implementation {0}\r\n\tfor factory interface {1}\r\n\ttargeting {2}...", factoryClassGenericName, factoryInterfaceFullName, GetDeclarationFullName(concreteClassDeclarationSyntax));
            this.RenderFactoryImplementation(generatedFilePath, concreteClassDeclarationSyntax, concreteClassTypeSymbol, factoryInterfaceTypeSymbol, GetSafeFileName(factoryClassGenericName), genericArgumentTypeSymbols, isSdkStyleProject);

            return (generatedFilePath, true);
        }

        private void RemoveObsoleteFactoriesFromSolution(
            string[] obsoleteFactoryFilePaths,
            Dictionary<string, bool> isSdkStyleProjectDict)
        {
            if (obsoleteFactoryFilePaths.Length == 0)
            {
                return;
            }

            var newSolution = this.solution;

            Logger.InfoFormat("Removing {0} from solution...", "obsolete generated factory file".ToQuantity(obsoleteFactoryFilePaths.Length));

            foreach (var obsoleteFactoryFilePath in obsoleteFactoryFilePaths)
            {
                var obsoleteFactoryDocument = FindDocumentFromPath(newSolution, obsoleteFactoryFilePath);
                if (!isSdkStyleProjectDict[obsoleteFactoryDocument.Project.FilePath])
                {
                    var newProject = obsoleteFactoryDocument.Project.RemoveDocument(obsoleteFactoryDocument.Id);
                    newSolution = newProject.Solution;
                }

                Logger.InfoFormat("{0} from {1}.", Path.GetFileName(obsoleteFactoryFilePath), obsoleteFactoryDocument.Project.Name);
                File.Delete(obsoleteFactoryFilePath);
            }

            Interlocked.Exchange(ref this.solution, newSolution);
        }

        private void RemoveObsoleteHashes(
            Dictionary<string, Dictionary<string, string>> fileHashesPerProject)
        {
            foreach (var projectPath in fileHashesPerProject.Keys.ToArray())
            {
                var project = this.solution.Projects.FirstOrDefault(p => p.FilePath.Equals(Path.Combine(Path.GetDirectoryName(this.solution.FilePath), projectPath)));

                if (project == null)
                {
                    fileHashesPerProject.Remove(projectPath);
                    continue;
                }

                foreach (var file in fileHashesPerProject[projectPath].Keys.ToArray())
                {
                    var filePath = Path.Combine(Path.GetDirectoryName(project.FilePath), file);
                    if (!File.Exists(filePath))
                    {
                        fileHashesPerProject[projectPath].Remove(file);
                    }
                }
            }
        }

        private void RenderFactoryImplementation(
            string generatedFilePath,
            ClassDeclarationSyntax concreteClassDeclarationSyntax,
            INamedTypeSymbol concreteClassTypeSymbol,
            INamedTypeSymbol factoryInterfaceTypeSymbol,
            string factoryName,
            INamedTypeSymbol[] genericArgumentTypeSymbols,
            bool isSdkStyleProject)
        {
            var fileName = Path.GetFileName(generatedFilePath);
            var factoryInterfaceName = factoryInterfaceTypeSymbol.Name;

            var (project, typeDeclarationDocument) = this.solution.Projects.Select(proj => (proj, doc: proj.GetDocument(concreteClassDeclarationSyntax.SyntaxTree))).First(t => t.doc != null);
            var factoryGeneratorEngine = new FactoryGeneratorEngine(project, this.templatePath);

            var usingsToFilterOut = new[] { concreteClassTypeSymbol.ContainingNamespace.ToString() };
            var outerUsingDeclarations = FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<CompilationUnitSyntax>().Usings, usingsToFilterOut);
            var innerUsingDeclarations = FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>().Usings, usingsToFilterOut);

            var factoryInterfaceMethods = GetSuitableFactoryInterfaceMethods(concreteClassTypeSymbol, factoryInterfaceTypeSymbol, genericArgumentTypeSymbols);

            var allContractMethodParameters = factoryInterfaceMethods.SelectMany(contractTypeMethod => contractTypeMethod.symbol.Parameters).ToArray();
            if (!factoryInterfaceMethods.Any())
            {
                throw new InvalidOperationException("The interface {0} has no suitable method returning any interface implemented by {1}. Please check if their is any.".FormatWith(factoryInterfaceTypeSymbol, concreteClassTypeSymbol));
            }

            var allConstructorParameters = factoryInterfaceMethods.Select(factoryMethod => SelectConstructorFromFactoryMethod(factoryMethod.symbol, concreteClassTypeSymbol))
                                                                  .SelectMany(selectedConstructor => selectedConstructor.Parameters).ToArray();
            var constructorParametersUsingSelfType = allConstructorParameters.Where(p => p.Type.Name == factoryInterfaceTypeSymbol.Name).ToArray();
            var constructorParametersWithoutSelfType = allConstructorParameters.Except(constructorParametersUsingSelfType)
                                                                               .ToArray();
#pragma warning disable RS1024 // Symbols should be compared for equality
            var injectedParameters = (from parameter in (IEnumerable<IParameterSymbol>)constructorParametersWithoutSelfType
                                      where !allContractMethodParameters.Any(contractMethodParameter => CompareParameters(contractMethodParameter, parameter))
                                      select parameter).Distinct(new ParameterEqualityComparer()).ToArray();
#pragma warning restore RS1024 // Symbols should be compared for equality

            var concreteClassName = GetXmlDocSafeTypeName(concreteClassTypeSymbol.ToString());
            var @namespace = GetDeclarationNamespaceFullName(concreteClassDeclarationSyntax);

            var generateCodeArguments = new[] { new Value("\"DeveloperInTheFlow.FactoryGenerator\"", false), new Value(string.Format("\"{0}\"", this.version), true) };

            // Class attributes
            var classAttributes = concreteClassDeclarationSyntax.AttributeLists
                                                                .SelectMany(al => al.Attributes).Where(
                                                                                                       attributeSyntax =>
                                                                                                       {
                                                                                                           var attributeName = attributeSyntax.Name.ToString();
                                                                                                           return this.attributeImportList.Any(attributeName.Contains);
                                                                                                       })
                                                                .Select(
                                                                        a =>
                                                                        {
                                                                            var lastIndex = a.ArgumentList.Arguments.Count - 1;

                                                                            var arguments = a.ArgumentList.Arguments.Select(
                                                                                                                            (
                                                                                                                                arg,
                                                                                                                                i) => new Value(arg.ToString(), i == lastIndex));
                                                                            return Attribute.Create(a.Name.ToString(), arguments);
                                                                        })
                                                                .Concat(
                                                                        new[]
                                                                        {
                                                                            Attribute.Create("global::System.CodeDom.Compiler.GeneratedCode", generateCodeArguments),
                                                                            Attribute.Create("global::System.Diagnostics.DebuggerNonUserCodeAttribute")
                                                                        }).ToArray();

            // Generic types of the factory
            var classGenericTypes = this.genericTypeBuilderService.Build(factoryInterfaceTypeSymbol.TypeParameters, genericArgumentTypeSymbols);

            // Constructor of the factory
            var constructor = this.constructorBuilderService.Build(concreteClassDeclarationSyntax, injectedParameters);

            // Fields of the factory
            var fields = this.fieldsBuilderService.Build(injectedParameters).ToArray();

            // Methods of the factory
            var methods = this.methodsBuilderService.Build(concreteClassTypeSymbol, fields, injectedParameters, factoryInterfaceMethods, factoryInterfaceName);

            // Interface of the factory
            var inherit = factoryInterfaceTypeSymbol.ToString();

            for (var i = 0; i < genericArgumentTypeSymbols.Length; i++)
            {
                if (genericArgumentTypeSymbols[i] != null)
                {
                    inherit = inherit.Replace(factoryInterfaceTypeSymbol.TypeArguments[i].Name, genericArgumentTypeSymbols[i].ToString());
                }
            }

            // The factory
            var factoryClass = new Class(classAttributes, concreteClassName, constructor, methods, fields, classGenericTypes, inherit, factoryName);

            // The file containing the factory
            var factoryFile = FactoryFile.Create(
                                                 @namespace,
                                                 factoryClass,
                                                 innerUsingDeclarations.ToFullString(),
                                                 outerUsingDeclarations.ToFullString());

            object model = factoryFile;
            var transformationScript = string.Format(@"{0}\{1}.tcs", Path.GetDirectoryName(this.templatePath), factoryFile.FactoryFor);

            // Execute the script associated to the template in order to adapt the model for the template whether it exists.
            if (File.Exists(transformationScript))
            {
                var json = JObject.FromObject(factoryFile);
                model = this.Transform(json, transformationScript);
            }

            var projectFolder = Path.GetDirectoryName(project.FilePath);
            if (projectFolder == null)
            {
                throw new InvalidOperationException("Cannot determines the folder path of the project.");
            }

            // The result of the generator
            var factoryResult = factoryGeneratorEngine.Generate(model, factoryFile.FactoryFor);

            if (!isSdkStyleProject)
            {
                if (!project.Documents.Any(doc => doc.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && doc.Folders.SequenceEqual(typeDeclarationDocument.Folders)))
                {
                    var document = project.AddDocument(fileName, factoryResult.Code, typeDeclarationDocument.Folders);
                    this.solution = document.Project.Solution;
                }
            }

            File.WriteAllText(generatedFilePath, factoryResult.Code, Encoding.UTF8);
        }

        #endregion

        private class ParameterEqualityComparer : IEqualityComparer<IParameterSymbol>
        {
            #region Public Methods and Operators

            /// <summary>
            /// Determines whether the specified objects are equal.
            /// </summary>
            /// <returns>
            /// true if the specified objects are equal; otherwise, false.
            /// </returns>
            /// <param name="x">The first object of type <paramref name="T"/> to compare.</param><param name="y">The second object of type <paramref name="T"/> to compare.</param>
            public bool Equals(
                IParameterSymbol x,
                IParameterSymbol y)
            {
                return x.Name.Equals(y.Name) && x.Type.Name.Equals(y.Type.Name);
            }

            /// <summary>
            /// Returns a hash code for the specified object.
            /// </summary>
            /// <returns>
            /// A hash code for the specified object.
            /// </returns>
            /// <param name="obj">The <see cref="T:System.Object"/> for which a hash code is to be returned.</param><exception cref="T:System.ArgumentNullException">The type of <paramref name="obj"/> is a reference type and <paramref name="obj"/> is null.</exception>
            public int GetHashCode(
                IParameterSymbol obj)
            {
                unchecked
                {
                    return ((obj.Name != null
                                 ? obj.Name.GetHashCode()
                                 : 0) * 397) ^ (obj.Type.Name != null
                                                    ? obj.Type.Name.GetHashCode()
                                                    : 0);
                }
            }

            #endregion
        }
    }
}