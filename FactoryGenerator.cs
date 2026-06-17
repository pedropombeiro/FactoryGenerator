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

        private readonly Lazy<IEvaluator> csscriptEvaluator = new(() =>
                                                                  {
                                                                      CSScript.EvaluatorConfig.ReferenceDomainAssemblies = true;
                                                                      return CSScript.Evaluator.ReferenceAssembly(typeof(JObject).Assembly)
                                                                                     .ReferenceAssembly(typeof(System.Linq.Enumerable).Assembly)
                                                                                     .ReferenceAssembly(typeof(System.Text.RegularExpressions.Regex).Assembly);
                                                                  });

        private readonly FieldsBuilderService fieldsBuilderService;

        private readonly bool forceGeneration;

        private readonly GenericTypeBuilderService genericTypeBuilderService;

        private readonly MethodsBuilderService methodsBuilderService;

        private readonly string templatePath;

        private readonly string templateHash;

        private readonly string version = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;

        private readonly Workspace workspace;

        private Solution solution;

        #endregion

        #region Constructors and Destructors

        [DebuggerStepThrough]
        public FactoryGenerator(Workspace workspace,
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

            using (var sha1 = new SHA1Managed())
            {
                var templateBytes = File.ReadAllBytes(templatePath);
                var hashBytes = sha1.ComputeHash(templateBytes);
                this.templateHash = BitConverter.ToString(hashBytes).Replace("-", string.Empty);
            }

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
                var generateFactoriesTask = this.GenerateFactoriesInProjectAsync(compilation, isSdkStyleProject, fileHashesPerProject, project);

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

        private static async Task<ICollection<string>> CatalogGeneratedFactoriesInProjectAsync(Compilation compilation)
        {
            var generatedFactoriesCatalog = new List<string>(16);

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var syntaxRootNode = await syntaxTree.GetRootAsync();
                var generatedFactoryClassDeclarations =
                    syntaxRootNode.DescendantNodesAndSelf(syntaxNode => !(syntaxNode is ClassDeclarationSyntax))
                                  .OfType<ClassDeclarationSyntax>()
                                  .Where(classDeclarationSyntax => classDeclarationSyntax.AttributeLists.Count > 0 && classDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes).Any(x =>
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

        private static bool CompareParameters(IParameterSymbol parameter1,
                                              IParameterSymbol parameter2)
        {
            return (parameter1.Name == parameter2.Name);
        }

        private static SyntaxList<UsingDirectiveSyntax> FilterOutUsings(SyntaxList<UsingDirectiveSyntax> usings,
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

        private static Document FindDocumentFromPath(Solution solution,
                                                     string filePath)
        {
            return solution.Projects
                           .Select(project => project.Documents.FirstOrDefault(d => d.FilePath == filePath))
                           .FirstOrDefault(document => document != null);
        }

        /// <summary>
        ///     Recursively collects all distinct containing namespaces from a set of type symbols,
        ///     including generic type arguments. Used to ensure every referenced namespace has a
        ///     corresponding using directive in the generated file.
        /// </summary>
        private static IEnumerable<INamespaceSymbol> CollectNamespacesFromTypes(IEnumerable<ITypeSymbol> types)
        {
            var visited = new HashSet<string>();
            var queue = new Queue<ITypeSymbol>(types);
            while (queue.Count > 0)
            {
                var type = queue.Dequeue();
                if (type == null) continue;

                // Unwrap arrays
                if (type is IArrayTypeSymbol arrayType)
                {
                    queue.Enqueue(arrayType.ElementType);
                    continue;
                }

                var ns = type.ContainingNamespace;
                if (ns != null && !ns.IsGlobalNamespace)
                {
                    var nsName = ns.ToString();
                    if (visited.Add(nsName))
                        yield return ns;
                }

                // Recurse into generic type arguments
                if (type is INamedTypeSymbol named)
                {
                    foreach (var arg in named.TypeArguments)
                        queue.Enqueue(arg);
                }
            }
        }

        /// <summary>
        ///     Strips namespace prefixes from a fully-qualified type name (including inside generic
        ///     angle brackets) when that namespace appears in <paramref name="knownNamespaces"/>,
        ///     producing the shortest name that is unambiguous given the file's using directives.
        /// </summary>
        internal static string ShortenTypeName(string typeName, ICollection<string> knownNamespaces)
        {
            if (string.IsNullOrEmpty(typeName) || knownNamespaces == null || knownNamespaces.Count == 0)
                return typeName;

            // Process generics recursively by splitting on top-level ',' inside '<...>'
            var ltIdx = typeName.IndexOf('<');
            if (ltIdx >= 0)
            {
                // Find the matching '>' by tracking nesting depth. This correctly handles
                // types with a suffix after the closing '>', e.g. 'Option<T>[]' (array of generic).
                var scanDepth = 0;
                var gtIdx = -1;
                for (var i = ltIdx; i < typeName.Length; i++)
                {
                    if (typeName[i] == '<') scanDepth++;
                    else if (typeName[i] == '>')
                    {
                        scanDepth--;
                        if (scanDepth == 0) { gtIdx = i; break; }
                    }
                }

                if (gtIdx < 0)
                    return typeName; // malformed — return as-is

                var outerPart = typeName.Substring(0, ltIdx);
                var innerContent = typeName.Substring(ltIdx + 1, gtIdx - ltIdx - 1);
                var suffix = typeName.Substring(gtIdx + 1); // e.g. "[]" for array types
                var shortenedOuter = ShortenTypeName(outerPart, knownNamespaces);
                // Split inner args at top-level commas only
                var args = new List<string>();
                var depth = 0;
                var start = 0;
                for (var i = 0; i < innerContent.Length; i++)
                {
                    var c = innerContent[i];
                    if (c == '<') depth++;
                    else if (c == '>') depth--;
                    else if (c == ',' && depth == 0)
                    {
                        args.Add(ShortenTypeName(innerContent.Substring(start, i - start).Trim(), knownNamespaces));
                        start = i + 1;
                    }
                }
                args.Add(ShortenTypeName(innerContent.Substring(start).Trim(), knownNamespaces));
                return $"{shortenedOuter}<{string.Join(", ", args)}>{suffix}";
            }

            // No generics — strip the longest matching namespace prefix
            var best = typeName;
            foreach (var ns in knownNamespaces)
            {
                var prefix = ns + ".";
                if (typeName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var candidate = typeName.Substring(prefix.Length);
                    // Prefer the shortest result (longest namespace prefix wins)
                    if (candidate.Length < best.Length)
                        best = candidate;
                }
            }

            // DEBUG: Log when we fail to shorten a type that looks like it should be shortened
            if (typeName.Contains("IEcuGroupUreaTestingFunctionRoutineControlFeatureService"))
            {
                System.Diagnostics.Debug.WriteLine($"FAILED TO SHORTEN: '{typeName}'");
                System.Diagnostics.Debug.WriteLine($"Looking for prefixes in {knownNamespaces?.Count ?? 0} namespaces");
                if (knownNamespaces != null)
                {
                    foreach (var ns in knownNamespaces.Where(n => n.Contains("ScrFunctions") || n.Contains("Lidec2") || n.Contains("Edc17")))
                    {
                        System.Diagnostics.Debug.WriteLine($"  Checking: '{ns}.'  StartsWith={typeName.StartsWith(ns + ".")}");
                        
                    }
                }
            }

            return best;
        }

        private static string GetDeclarationFullName(TypeDeclarationSyntax typeDeclarationSyntax)
        {
            var typeParameterCount = typeDeclarationSyntax.TypeParameterList == null
                                         ? 0
                                         : typeDeclarationSyntax.TypeParameterList.Parameters.Count;
            var fullyQualifiedName = $"{GetDeclarationNamespaceFullName(typeDeclarationSyntax)}.{typeDeclarationSyntax.Identifier.ValueText}{(typeParameterCount == 0
                                                                                                                                                  ? string.Empty
                                                                                                                                                  : $"`{typeParameterCount}")}";

            return fullyQualifiedName;
        }

        private static string GetDeclarationNamespaceFullName(CSharpSyntaxNode typeDeclarationSyntax)
        {
            var namespaceDeclarationSyntax = typeDeclarationSyntax.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
            return namespaceDeclarationSyntax.Name.ToString();
        }

        private static AttributeSyntax GetFactoryAttributeFromClassDeclaration(ClassDeclarationSyntax concreteClassDeclarationSyntax)
        {
            AttributeSyntax factoryAttribute;
            try
            {
                factoryAttribute = concreteClassDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes)
                                                                 .SingleOrDefault(a =>
                                                                                  {
                                                                                      var attributeClassFullName = a.Name.NormalizeWhitespace().ToFullString();
                                                                                      return attributeClassFullName.EndsWith("GenerateFactoryAttribute") || attributeClassFullName.EndsWith("GenerateFactory");
                                                                                  });
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"Could not read GenerateFactoryAttribute from {concreteClassDeclarationSyntax} type.", e);
            }

            return factoryAttribute;
        }

        private static string GetFactoryClassGenericName(ClassDeclarationSyntax concreteTypeDeclarationSyntax,
                                                         INamedTypeSymbol factoryInterfaceTypeSymbol)
        {
            var factoryClassName = $"{concreteTypeDeclarationSyntax.Identifier.ValueText}Factory{GetTypeParametersDeclaration(factoryInterfaceTypeSymbol.TypeParameters)}";

            return factoryClassName;
        }

        private static string GetSafeFileName(string identifier)
        {
            return identifier.Contains("<")
                       ? identifier.Substring(0, identifier.IndexOf("<", StringComparison.Ordinal))
                       : identifier;
        }

        private static (ITypeSymbol returnType, IMethodSymbol symbol)[] GetSuitableFactoryInterfaceMethods(INamedTypeSymbol concreteClassTypeSymbol,
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

        private static string GetTypeParametersDeclaration(ImmutableArray<ITypeParameterSymbol> typeParameterSymbols)
        {
            var typeParameters = string.Empty;

            if (typeParameterSymbols.Length > 0)
            {
                typeParameters = $"<{string.Join(" ,", typeParameterSymbols.Select(t => t.Name))}>";
            }

            return typeParameters;
        }

        private static string GetXmlDocSafeTypeName(string typeName)
        {
            return typeName.Replace("<", "{").Replace(">", "}");
        }

        private static async Task<bool> IsSdkStyleProjectAsync(string path)
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
        }

        private static bool IsTypeDeclarationSyntaxFactoryTarget(ClassDeclarationSyntax classDeclarationSyntax)
        {
            var attributeListSyntaxes = classDeclarationSyntax.AttributeLists;

            return attributeListSyntaxes.Count > 0 &&
                   attributeListSyntaxes.SelectMany(a => a.Attributes).Any(x =>
                                                                           {
                                                                               var attributeClassName = x.Name is QualifiedNameSyntax qualifiedNameSyntax
                                                                                                            ? qualifiedNameSyntax.Right.Identifier.ToString()
                                                                                                            : x.Name.ToString();
                                                                               return attributeClassName == "GenerateFactory" || attributeClassName == "GenerateFactoryAttribute";
                                                                           });
        }

        private static void LogCodeGenerationStatistics(Stopwatch chrono,
                                                        string[] generatedFactoryFilePaths,
                                                        string[] obsoleteFactoryFilePaths,
                                                        string[] newFactoryFilePaths)
        {
            var elapsedTime = TimeSpan.FromMilliseconds(chrono.ElapsedMilliseconds);
            var statisticsFormat = obsoleteFactoryFilePaths.Any() || newFactoryFilePaths.Any()
                                       ? "{0} (+{1}/-{2})"
                                       : "{0}";
            var factoryStatistics = string.Format(statisticsFormat, "factory".ToQuantity(generatedFactoryFilePaths.Length), newFactoryFilePaths.Length, obsoleteFactoryFilePaths.Length);

            Logger.InfoFormat("Generated {0} in {1}.", factoryStatistics, elapsedTime.Humanize(2));
        }

        private static ITypeSymbol ResolveGenericArgument(INamedTypeSymbol factoryInterfaceTypeSymbol,
                                                          ITypeSymbol methodReturnType,
                                                          INamedTypeSymbol[] genericArgumentTypeSymbols)
        {
            if (methodReturnType is INamedTypeSymbol)
            {
                return methodReturnType;
            }

            if (genericArgumentTypeSymbols.Length == 0)
            {
                return methodReturnType;
            }

            var typeArgumentIndex = factoryInterfaceTypeSymbol.TypeArguments.IndexOf(methodReturnType);

            return typeArgumentIndex >= 0
                       ? genericArgumentTypeSymbols[typeArgumentIndex]
                       : methodReturnType;
        }

        private static IMethodSymbol SelectConstructorFromFactoryMethod(IMethodSymbol factoryMethod,
                                                                        INamedTypeSymbol concreteClassSymbol)
        {
            var factoryMethodParameters = factoryMethod.Parameters;
            var instanceConstructors = concreteClassSymbol.InstanceConstructors
                                                          .Where(c => c.DeclaredAccessibility == Accessibility.Public || c.DeclaredAccessibility == Accessibility.Internal)
                                                          .ToArray();

            try
            {
                var matchedInstanceConstructors = instanceConstructors.GroupBy(c => factoryMethodParameters.Select(fmp => fmp.Name)
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
                throw new InvalidOperationException($"Could not select a constructor from {concreteClassSymbol.Name} type with the following parameters: ({string.Join(", ", factoryMethodParameters.Select(x => x.Name))}).", e);
            }
        }

        private static bool VerifyAndAddHash(Dictionary<string, Dictionary<string, string>> fileHashesPerProject,
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

        private async Task<ICollection<string>> GenerateFactoriesInProjectAsync(Compilation compilation,
                                                                                bool isSdkStyleProject,
                                                                                Dictionary<string, Dictionary<string, string>> fileHashesPerProject,
                                                                                Project project)
        {
            var generatedFactoriesList = new List<string>();

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var syntaxRootNode = await syntaxTree.GetRootAsync();
                var classDeclarations =
                    syntaxRootNode.DescendantNodesAndSelf(syntaxNode => syntaxNode is not ClassDeclarationSyntax)
                                  .OfType<ClassDeclarationSyntax>()
                                  .Where(IsTypeDeclarationSyntaxFactoryTarget)
                                  .ToArray();

                if (classDeclarations.Any())
                {
                    foreach (var classDeclarationSyntax in classDeclarations)
                    {
                        var fullyQualifiedMetadataName = GetDeclarationFullName(classDeclarationSyntax);
                        var resolvedConcreteClassType = compilation.GetTypeByMetadataName(fullyQualifiedMetadataName);

                        var result = this.GenerateFactoryForClass(classDeclarationSyntax, resolvedConcreteClassType, compilation, isSdkStyleProject, fileHashesPerProject, project);

                        if (result is null)
                            continue;

                        var (generatedFilePath, generated) = result.Value;
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

        private (string, bool)? GenerateFactoryForClass(ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                                        INamedTypeSymbol concreteClassTypeSymbol,
                                                        Compilation compilation,
                                                        bool isSdkStyleProject,
                                                        Dictionary<string, Dictionary<string, string>> fileHashesPerProject,
                                                        Project project)
        {
            var factoryAttribute = GetFactoryAttributeFromClassDeclaration(concreteClassDeclarationSyntax);

            INamedTypeSymbol factoryInterfaceTypeSymbol;

            var genericArgumentTypeSymbols = Array.Empty<INamedTypeSymbol>();

            if (factoryAttribute.ArgumentList != null && factoryAttribute.ArgumentList.Arguments.Any())
            {
                var typeOfArgument = factoryAttribute.ArgumentList.Arguments.Single();

                var usings = concreteClassDeclarationSyntax.Parent.DescendantNodes()
                                                           .OfType<UsingDirectiveSyntax>()
                                                           .Select(syntax => syntax.Name.NormalizeWhitespace().ToFullString())
                                                           .ToArray();

                var namespaces = usings.Concat([GetDeclarationNamespaceFullName(concreteClassDeclarationSyntax)]);

                var typeOfSyntax = (TypeOfExpressionSyntax)typeOfArgument.Expression;
                var typeName = typeOfSyntax.Type.ToFullString();

                if (typeOfSyntax.Type is GenericNameSyntax genericNameSyntax)
                {
                    var typeArgList = genericNameSyntax.TypeArgumentList.Arguments;
                    var genericArgumentCount = typeArgList.Count;

                    // Determine whether all type arguments are open placeholders, meaning either:
                    //   typeof(IFoo<,,>)          — OmittedTypeArgumentSyntax nodes (commas with no names)
                    //   typeof(IFoo<TState, ...>) — simple identifiers that are the class's own type parameters
                    // Both represent an unbound generic whose parameters are forwarded from the concrete
                    // class; don't try to resolve the placeholder names as concrete types.
                    var classTypeParameterNames = new HashSet<string>(
                        concreteClassTypeSymbol?.TypeParameters.Select(tp => tp.Name) ?? Enumerable.Empty<string>(),
                        StringComparer.Ordinal);

                    bool IsOpenGenericPlaceholder(TypeSyntax arg)
                        => arg is OmittedTypeArgumentSyntax                                         // typeof(IFoo<,,>)
                           || (arg is IdentifierNameSyntax id                                       // typeof(IFoo<TState>)
                               && classTypeParameterNames.Contains(id.Identifier.ValueText));

                    var isUnboundForwarded = typeArgList.All(IsOpenGenericPlaceholder);

                    if (!isUnboundForwarded)
                    {
                        var genericArgumentNames = typeArgList.Select(a => a.ToFullString().Trim()).ToArray();
                        genericArgumentTypeSymbols = genericArgumentNames
                                                     .Select(arg => ResolveTypeByName(arg, namespaces, compilation))
                                                     .ToArray();

                        if (genericArgumentTypeSymbols.Any(s => s == null))
                        {
                            throw new InvalidOperationException($"Not all generic types for {typeName} have been found");
                        }
                    }

                    // Build the metadata name directly from the identifier and arity, e.g. IFoo`3
                    // Avoids any ToFullString() trivia/whitespace issues with the generic suffix.
                    typeName = $"{genericNameSyntax.Identifier.ValueText}`{genericArgumentCount}";
                }

                var fullTypeNames = namespaces.Select(ns => $"{ns}.{typeName}").Concat([typeName]);

                factoryInterfaceTypeSymbol = fullTypeNames.Select(compilation.GetTypeByMetadataName).FirstOrDefault(o => o != null);

                if (factoryInterfaceTypeSymbol == null)
                {
                    throw new InvalidOperationException($"Factory {typeName} type couldn't be resolved in {GetDeclarationFullName(concreteClassDeclarationSyntax)}.");
                }

                if (factoryInterfaceTypeSymbol.IsUnboundGenericType)
                {
                    factoryInterfaceTypeSymbol = factoryInterfaceTypeSymbol.ConstructedFrom;
                }
            }
            else
            {
                throw new InvalidOperationException($"Factory type must be specified in {factoryAttribute.Name.ToFullString()} on {GetDeclarationFullName(concreteClassDeclarationSyntax)}.");
            }

            var factoryClassGenericName = GetFactoryClassGenericName(concreteClassDeclarationSyntax, factoryInterfaceTypeSymbol);
            var factoryInterfaceFullName = factoryInterfaceTypeSymbol.ToString();

            var typeDeclarationDocument = project.GetDocument(concreteClassDeclarationSyntax.SyntaxTree);

            var folders = string.Join(@"\", typeDeclarationDocument.Folders);
            var safeFileName = GetSafeFileName(factoryClassGenericName);
            var fileName = $"{safeFileName}.Generated.cs";
            var generatedFilePath = Path.Combine(Path.GetDirectoryName(project.FilePath), folders, fileName);
            var relativeFilePath = Path.Combine(folders, typeDeclarationDocument.Name);

            bool IsLinkedFile()
                => !typeDeclarationDocument.FilePath.StartsWith(Path.GetDirectoryName(project.FilePath), StringComparison.OrdinalIgnoreCase);

            if (IsLinkedFile())
                return null;

            using SHA1Managed sha1 = new SHA1Managed();
            var hash = sha1.ComputeHash(File.ReadAllBytes(typeDeclarationDocument.FilePath));
            var fileHashBuilder = new StringBuilder(2 * hash.Length);
            foreach (var b in hash)
            {
                fileHashBuilder.AppendFormat("{0:X2}", b);
            }

            var fileHash = fileHashBuilder.ToString() + "+" + this.templateHash;
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

        private static string[] SplitTopLevelGenericArguments(string content)
        {
            var args = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < content.Length; i++)
            {
                var c = content[i];
                if (c == '<') depth++;
                else if (c == '>') depth--;
                else if (c == ',' && depth == 0)
                {
                    args.Add(content.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }

            if (start < content.Length)
                args.Add(content.Substring(start).Trim());

            return args.ToArray();
        }

        private static INamedTypeSymbol ResolveTypeByName(string typeName, IEnumerable<string> namespaces, Compilation compilation)
        {
            typeName = typeName.Trim();
            var namespacesArray = namespaces as string[] ?? namespaces.ToArray();

            if (typeName.Contains("<"))
            {
                // Nested generic, e.g. EcuGroupFunctionStep<MaintenanceModeCrsStep>
                var openBracketIdx = typeName.IndexOf('<');
                var outerName = typeName.Substring(0, openBracketIdx);
                var innerContent = typeName.Substring(openBracketIdx + 1, typeName.Length - openBracketIdx - 2);
                var innerArgNames = SplitTopLevelGenericArguments(innerContent);

                var resolvedInnerArgs = innerArgNames
                    .Select(arg => ResolveTypeByName(arg, namespacesArray, compilation))
                    .ToArray();

                if (resolvedInnerArgs.Any(a => a == null))
                    return null;

                var openGenericMetadataName = $"{outerName}`{innerArgNames.Length}";
                var openGeneric = namespacesArray
                                      .Select(ns => compilation.GetTypeByMetadataName($"{ns}.{openGenericMetadataName}"))
                                      .FirstOrDefault(t => t != null)
                                  ?? compilation.GetTypeByMetadataName(openGenericMetadataName);

                return openGeneric?.Construct(resolvedInnerArgs);
            }

            return namespacesArray
                       .Select(ns => compilation.GetTypeByMetadataName($"{ns}.{typeName}"))
                       .FirstOrDefault(t => t != null)
                   ?? compilation.GetTypeByMetadataName(typeName);
        }

        private void RemoveObsoleteFactoriesFromSolution(string[] obsoleteFactoryFilePaths,
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

        private void RemoveObsoleteHashes(Dictionary<string, Dictionary<string, string>> fileHashesPerProject)
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

        private void RenderFactoryImplementation(string generatedFilePath,
                                                 ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                                 INamedTypeSymbol concreteClassTypeSymbol,
                                                 INamedTypeSymbol factoryInterfaceTypeSymbol,
                                                 string factoryName,
                                                 INamedTypeSymbol[] genericArgumentTypeSymbols,
                                                 bool isSdkStyleProject)
        {
            var fileName = Path.GetFileName(generatedFilePath);
            var factoryInterfaceName = factoryInterfaceTypeSymbol.Name;

            // When concrete type arguments are provided (e.g. typeof(IFoo<ConcreteA, ConcreteB>)),
            // construct the closed generic interface so that all method return types and parameter
            // types already carry the concrete types. This ensures the generated method signatures
            // use the real types rather than the open type-parameter names.
            // For non-generic factories and open-generic-forwarding factories (typeof(IFoo<,,>)),
            // the symbol is used as-is — the factory class itself stays generic.
            var closedFactoryInterfaceTypeSymbol = genericArgumentTypeSymbols.Length > 0
                                                   && genericArgumentTypeSymbols.Length == factoryInterfaceTypeSymbol.TypeParameters.Length
                                                       ? factoryInterfaceTypeSymbol.Construct(genericArgumentTypeSymbols)
                                                       : factoryInterfaceTypeSymbol;

            var (project, typeDeclarationDocument) = this.solution.Projects.Select(proj => (proj, doc: proj.GetDocument(concreteClassDeclarationSyntax.SyntaxTree))).First(t => t.doc != null);
            var factoryGeneratorEngine = new FactoryGeneratorEngine(project, this.templatePath);

            var usingsToFilterOut = new[] { concreteClassTypeSymbol.ContainingNamespace.ToString() };

            // Collect extern alias directives from both the concrete class and the factory interface.
            // Externs can appear at file scope (CompilationUnitSyntax.Externs) or inside namespace
            // blocks (BaseNamespaceDeclarationSyntax.Externs). Collect from both locations.
            var concreteCompilation = concreteClassDeclarationSyntax.FirstAncestorOrSelf<CompilationUnitSyntax>();
            var concreteNamespace = concreteClassDeclarationSyntax.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
            var concreteExterns = (concreteCompilation?.Externs ?? default)
                .Concat(concreteNamespace?.Externs ?? default);

            var interfaceExterns = factoryInterfaceTypeSymbol.DeclaringSyntaxReferences
                .SelectMany(r =>
                {
                    var syntax = r.GetSyntax();
                    var fileExterns = syntax.FirstAncestorOrSelf<CompilationUnitSyntax>()?.Externs ?? default;
                    var nsExterns = syntax.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>()?.Externs ?? default;
                    return fileExterns.Concat(nsExterns);
                });

            var allExterns = concreteExterns.Concat(interfaceExterns)
                .GroupBy(e => e.Identifier.Text)
                .Select(g => g.First())
                .ToArray();

            var outerUsingDeclarations = FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<CompilationUnitSyntax>().Usings, usingsToFilterOut);
            var innerUsingDeclarations = FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>().Usings, usingsToFilterOut);

            // Collect all usings from the concrete class (including alias-qualified ones from the class itself)
            var concreteUsingsAll = (concreteCompilation?.Usings ?? default)
                .Concat(concreteNamespace?.Usings ?? default)
                .ToArray();

            // Also pull in usings from the factory interface's source file — it may reference
            // types (e.g. parameter types in Create()) whose namespaces are not imported by the
            // concrete class's file. Collect both file-level and namespace-level usings, including
            // alias-qualified usings (e.g. ExternAlias::Namespace) which reference extern aliases.
            var interfaceUsings = factoryInterfaceTypeSymbol.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax())
                .Where(n => n != null)
                .SelectMany(n =>
                {
                    var compilationUsings = n.FirstAncestorOrSelf<CompilationUnitSyntax>()?.Usings ?? default;
                    var namespaceUsings = n.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>()?.Usings ?? default;
                    return compilationUsings.Concat(namespaceUsings);
                })
                .ToArray();

            // Combine usings from both sources
            var allUsings = concreteUsingsAll.Concat(interfaceUsings).ToArray();

            // For alias-qualified usings (e.g. ExternAlias::Namespace.Type), extract the
            // unqualified namespace names. The Name property for an alias-qualified using
            // is an AliasQualifiedNameSyntax, so we need to extract the right side (after ::).
            var aliasQualifiedNamespaces = allUsings
                .Where(u => u.Alias != null)
                .Select(u =>
                {
                    // For "using Alias::Namespace;", u.Name might be an AliasQualifiedNameSyntax
                    // where ToString() includes "Alias::Namespace". We want just "Namespace".
                    var name = u.Name;
                    if (name is Microsoft.CodeAnalysis.CSharp.Syntax.AliasQualifiedNameSyntax aliasQual)
                    {
                        // Extract just the right side (after ::)
                        name = aliasQual.Name;
                    }
                    var nameText = name.ToString().Trim();
                    Logger.DebugFormat("Alias-qualified using: Alias={0}, Extracted namespace={1}", u.Alias.Name.Identifier.Text, nameText);
                    return nameText;
                })
                .Where(ns => !string.IsNullOrWhiteSpace(ns))
                .ToList();

            // Collect all namespaces visible in the generated file so we can shorten FQNs.
            // Include both unaliased usings AND alias-qualified usings, because once a using
            // directive (even alias-qualified) brings a namespace into scope, types from that
            // namespace can be referenced without the full prefix.
            var @namespace = GetDeclarationNamespaceFullName(concreteClassDeclarationSyntax);
            var knownNamespaces = allUsings
                .Where(u => u.Alias == null)
                .Select(u => u.Name.ToString())
                .Concat(aliasQualifiedNamespaces)  // Also include alias-qualified namespaces
                .Append(@namespace)
                .Append(concreteClassTypeSymbol.ContainingNamespace.ToString())
                .Distinct()
                .ToList();

            // Merge interface usings into innerUsingDeclarations, skipping any already present
            // or belonging to the generated file's own namespace. Track both simple names for
            // unaliased usings and full text for alias-qualified usings to avoid duplicates.
            var existingUsingSignatures = new HashSet<string>(
                outerUsingDeclarations.Concat(innerUsingDeclarations)
                    .Select(u => u.ToFullString().Trim()));
            existingUsingSignatures.Add(@namespace);
            existingUsingSignatures.UnionWith(usingsToFilterOut);

            // Take the leading trivia (indentation) from an existing inner using so the
            // appended directives match the file's indentation style exactly.
            var innerUsingLeadingTrivia = innerUsingDeclarations.Count > 0
                ? innerUsingDeclarations[0].GetLeadingTrivia()
                : SyntaxFactory.TriviaList(SyntaxFactory.Whitespace("    "));

            foreach (var u in allUsings)
            {
                var signature = u.ToFullString().Trim();
                if (existingUsingSignatures.Add(signature))
                    innerUsingDeclarations = innerUsingDeclarations.Add(
                        u.WithLeadingTrivia(innerUsingLeadingTrivia)
                         .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
            }

            var factoryInterfaceMethods = GetSuitableFactoryInterfaceMethods(concreteClassTypeSymbol, closedFactoryInterfaceTypeSymbol, genericArgumentTypeSymbols);

            var allContractMethodParameters = factoryInterfaceMethods.SelectMany(contractTypeMethod => contractTypeMethod.symbol.Parameters).ToArray();
            if (!factoryInterfaceMethods.Any())
            {
                throw new InvalidOperationException($"The interface {closedFactoryInterfaceTypeSymbol} has no suitable method returning any interface implemented by {concreteClassTypeSymbol}. Please check if their is any.");
            }

            // Ensure every namespace referenced by factory method parameter types and return types
            // has a corresponding using directive. This catches implicit namespaces (e.g. 'System'
            // for IObservable<T>) that are never explicitly imported in the source files.
            // Collect all missing namespaces first, sort them (System.* first, then alphabetically),
            // then insert them at the correct position in the using list.
            // Skip namespaces already covered by alias-qualified usings to avoid duplicates.
            var allFactoryTypes = factoryInterfaceMethods
                .SelectMany(m => m.symbol.Parameters.Select(p => p.Type).Append(m.returnType));
            // Track namespace strings (not full using signatures) to detect missing namespaces
            var existingNamespaceNames = new HashSet<string>(knownNamespaces);
            existingNamespaceNames.UnionWith(aliasQualifiedNamespaces); // Prevent duplicate plain usings for alias-qualified namespaces
            var missingNamespaces = CollectNamespacesFromTypes(allFactoryTypes)
                .Select(ns => ns.ToString())
                .Where(nsName => !string.IsNullOrEmpty(nsName) && existingNamespaceNames.Add(nsName))
                .OrderBy(nsName => nsName.StartsWith("System", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(nsName => nsName, StringComparer.Ordinal)
                .ToList();

            foreach (var nsName in missingNamespaces)
            {
                knownNamespaces.Add(nsName);
                // " " prefix on the name gives the required space between 'using' keyword and name.
                var newUsing = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(" " + nsName))
                    .WithLeadingTrivia(innerUsingLeadingTrivia)
                    .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

                // Insert System.* usings before the first non-System using; append others at end.
                if (nsName.StartsWith("System", StringComparison.Ordinal))
                {
                    var insertIdx = innerUsingDeclarations
                        .Select((u, i) => (u, i))
                        .FirstOrDefault(t => !t.u.Name.ToString().TrimStart().StartsWith("System", StringComparison.Ordinal))
                        .i;
                    innerUsingDeclarations = innerUsingDeclarations.Insert(insertIdx, newUsing);
                }
                else
                {
                    innerUsingDeclarations = innerUsingDeclarations.Add(newUsing);
                }
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
            var generateCodeArguments = new[] { new Value("\"DeveloperInTheFlow.FactoryGenerator\"", false), new Value($"\"{this.version}\"", true) };

            // Class attributes
            var classAttributes = concreteClassDeclarationSyntax.AttributeLists
                                                                .SelectMany(al => al.Attributes).Where(attributeSyntax =>
                                                                                                       {
                                                                                                           var attributeName = attributeSyntax.Name.ToString();
                                                                                                           return this.attributeImportList.Any(attributeName.Contains);
                                                                                                       })
                                                                .Select(a =>
                                                                        {
                                                                            var arguments = Enumerable.Empty<Value>();
                                                                            if (a.ArgumentList != null && a.ArgumentList.Arguments.Any())
                                                                            {
                                                                                var lastIndex = a.ArgumentList.Arguments.Count - 1;
                                                                                arguments = a.ArgumentList.Arguments.Select((arg,
                                                                                                                             i) => new Value(arg.ToString(), i == lastIndex));
                                                                            }

                                                                            return Attribute.Create(a.Name.ToString(), arguments);
                                                                        })
                                                                .Concat(
                                                                [
                                                                    Attribute.Create("global::System.CodeDom.Compiler.GeneratedCode", generateCodeArguments),
                                                                    Attribute.Create("global::System.Diagnostics.DebuggerNonUserCodeAttribute")
                                                                ]).ToArray();

            // Generic types of the factory — merge concrete class constraints by position for open-generic forwarding.
            // Materialize to array so both GenericTypes and GenericTypeConstraints can iterate it independently.
            var classGenericTypes = this.genericTypeBuilderService.Build(factoryInterfaceTypeSymbol.TypeParameters, genericArgumentTypeSymbols, concreteClassTypeSymbol.TypeParameters).ToArray();

            // Constructor of the factory
            var constructor = this.constructorBuilderService.Build(concreteClassDeclarationSyntax, injectedParameters, knownNamespaces);

            // Fields of the factory
            var fields = this.fieldsBuilderService.Build(injectedParameters, knownNamespaces).ToArray();

            // Methods of the factory
            var methods = this.methodsBuilderService.Build(concreteClassTypeSymbol, fields, injectedParameters, factoryInterfaceMethods, factoryInterfaceName, factoryInterfaceTypeSymbol, knownNamespaces);

            // Interface of the factory — use the closed symbol so it already has concrete type arguments
            var inheritFqn = closedFactoryInterfaceTypeSymbol.ToString();
            var inherit = ShortenTypeName(inheritFqn, knownNamespaces);

            // The factory
            var factoryClass = new Class(classAttributes, concreteClassName, constructor, methods, fields, classGenericTypes, inherit, factoryName);

            // The file containing the factory
            // Place extern aliases inside the namespace (before inner usings) to match C# convention
            // where externs from interface namespace blocks are preserved
            var innerContent = allExterns.Any()
                ? string.Concat(allExterns.Select(e => "    " + e.ToFullString())) + "\r\n" + innerUsingDeclarations.ToFullString()
                : innerUsingDeclarations.ToFullString();
            var factoryFile = FactoryFile.Create(
                                                 @namespace,
                                                 factoryClass,
                                                 innerContent,
                                                 outerUsingDeclarations.ToFullString());

            object model = factoryFile;
            var transformationScript = $@"{Path.GetDirectoryName(this.templatePath)}\{inheritFqn}.tcs";

            // Execute the script associated to the template in order to adapt the model for the template whether it exists.
            if (File.Exists(transformationScript))
            {
                var json = JObject.FromObject(factoryFile);
                var attributesArray = (JArray)json["Class"]["Attributes"];
                var factoryForAttributes = attributesArray.Where(a => a["Name"]?.ToString().Contains("FactoryFor") == true).ToList();

                model = factoryForAttributes.Count > 1
                            ? this.TransformMultipleFactoryForAttributes(json, factoryForAttributes, transformationScript)
                            : this.Transform(json, transformationScript);
            }

            var projectFolder = Path.GetDirectoryName(project.FilePath);
            if (projectFolder == null)
            {
                throw new InvalidOperationException("Cannot determines the folder path of the project.");
            }

            // The result of the generator
            var factoryResult = factoryGeneratorEngine.Generate(model, inheritFqn);

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

        private JObject Transform(JObject factoryFile,
                                  string transformationScriptPath)
        {
            dynamic script = this.csscriptEvaluator.Value.LoadFile(transformationScriptPath);
            return script.Transform(factoryFile);
        }

        private JObject TransformMultipleFactoryForAttributes(JObject json,
                                                              List<JToken> factoryForAttributes,
                                                              string transformationScript)
        {
            var attributesArray = (JArray)json["Class"]["Attributes"];
            var systemAttributeNames = new[] { "global::System.CodeDom.Compiler.GeneratedCode", "global::System.Diagnostics.DebuggerNonUserCodeAttribute" };

            // Separate FactoryFor from other attributes
            var otherAttributes = attributesArray.Where(a => a["Name"]?.ToString().Contains("FactoryFor") != true).ToList();
            attributesArray.Clear();
            foreach (var attr in otherAttributes) attributesArray.Add(attr);

            // Transform each FactoryFor and collect newly generated attributes
            var transformedAttributes = new List<JToken>();
            foreach (var factoryForAttr in factoryForAttributes)
            {
                var tempJson = (JObject)json.DeepClone();
                ((JArray)tempJson["Class"]["Attributes"]).Add(factoryForAttr);

                if (this.Transform(tempJson, transformationScript) is JObject transformedObj)
                {
                    var newAttributes = ((JArray)transformedObj["Class"]["Attributes"])
                        .Where(attr =>
                               {
                                   var name = attr["Name"]?.ToString();
                                   return !systemAttributeNames.Contains(name) &&
                                          otherAttributes.All(o => o["Name"]?.ToString() != name);
                               });
                    transformedAttributes.AddRange(newAttributes);
                }
            }

            // Transform base model and add all transformed attributes
            var model = this.Transform(json, transformationScript);
            if (model != null)
            {
                var finalAttrs = (JArray)model["Class"]["Attributes"];
                foreach (var attr in transformedAttributes) finalAttrs.Add(attr);
            }

            return model;
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
            public bool Equals(IParameterSymbol x,
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
            public int GetHashCode(IParameterSymbol obj)
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