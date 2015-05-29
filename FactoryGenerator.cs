namespace DeveloperInTheFlow.FactoryGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Common.Logging;

    using DeveloperInTheFlow.FactoryGenerator.Models;
    using DeveloperInTheFlow.FactoryGenerator.Services;

    using Humanizer;

    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    using Attribute = DeveloperInTheFlow.FactoryGenerator.Models.Attribute;

    public class FactoryGenerator
    {
        #region Static Fields

        private static readonly ILog Logger = LogManager.GetLogger<FactoryGenerator>();

        #endregion

        #region Fields

        private readonly string version = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;

        private readonly Workspace workspace;

        private readonly bool writeXmlDoc;

        private Solution solution;

        private readonly FieldsBuilderService fieldsBuilderService;

        private readonly ConstructorBuilderService constructorBuilderService;

        private readonly MethodsBuilderService methodsBuilderService;

        private readonly GenericTypeBuilderService genericTypeBuilderService;

        private readonly string templatePath;

        #endregion

        #region Constructors and Destructors

        [DebuggerStepThrough]
        public FactoryGenerator(Workspace workspace,
                                Solution solution,
                                IEnumerable<string> attributeImportList,
                                bool writeXmlDoc,
                                string templatePath)
        {
            this.workspace = workspace;
            this.solution = solution;
            this.writeXmlDoc = writeXmlDoc;
            this.templatePath = templatePath;

            this.fieldsBuilderService = new FieldsBuilderService();
            this.constructorBuilderService = new ConstructorBuilderService(attributeImportList);
            this.genericTypeBuilderService = new GenericTypeBuilderService();
            this.methodsBuilderService = new MethodsBuilderService(this.genericTypeBuilderService);
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
            foreach (var projectId in projectDependencyGraph.GetTopologicallySortedProjects())
            {
                var project = this.solution.GetProject(projectId);
                var compilation = await project.GetCompilationAsync().ConfigureAwait(false);

                Logger.InfoFormat("Processing {0}...", project.Name);

                var catalogTask = CatalogGeneratedFactoriesInProjectAsync(compilation);
                var generateFactoriesTask = this.GenerateFactoriesInProjectAsync(compilation);

                existingFactoriesTasksList.Add(catalogTask);
                generatedFactoriesTasksList.Add(generateFactoriesTask);
            }

            await Task.WhenAll(existingFactoriesTasksList.Cast<Task>().Concat(generatedFactoriesTasksList));

            var existingGeneratedFactoryFilePaths = existingFactoriesTasksList.SelectMany(task => task.Result).ToArray();
            var generatedFactoryFilePaths = generatedFactoriesTasksList.SelectMany(task => task.Result).ToArray();

            var newFactoryFilePaths = generatedFactoryFilePaths.Except(existingGeneratedFactoryFilePaths).ToArray();
            var obsoleteFactoryFilePaths = existingGeneratedFactoryFilePaths.Except(generatedFactoryFilePaths).ToArray();
            this.RemoveObsoleteFactoriesFromSolution(obsoleteFactoryFilePaths);

            this.workspace.TryApplyChanges(this.solution);

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

        private static string GetDeclarationFullName(TypeDeclarationSyntax typeDeclarationSyntax)
        {
            var typeParameterCount = typeDeclarationSyntax.TypeParameterList == null
                                         ? 0
                                         : typeDeclarationSyntax.TypeParameterList.Parameters.Count;
            var fullyQualifiedName = "{0}.{1}{2}".FormatWith(GetDeclarationNamespaceFullName(typeDeclarationSyntax),
                                                             typeDeclarationSyntax.Identifier.ValueText,
                                                             typeParameterCount == 0
                                                                 ? string.Empty
                                                                 : "`{0}".FormatWith(typeParameterCount));

            return fullyQualifiedName;
        }

        private static string GetDeclarationNamespaceFullName(BaseTypeDeclarationSyntax typeDeclarationSyntax)
        {
            var namespaceDeclarationSyntax = typeDeclarationSyntax.FirstAncestorOrSelf<NamespaceDeclarationSyntax>();
            return namespaceDeclarationSyntax.Name.ToString();
        }

        private static AttributeData GetFactoryAttributeFromTypeSymbol(INamedTypeSymbol concreteClassTypeSymbol)
        {
            AttributeData factoryAttribute;
            try
            {
                factoryAttribute = concreteClassTypeSymbol.GetAttributes().Single(a =>
                                                                                  {
                                                                                      var attributeClassFullName = a.AttributeClass.ToString();
                                                                                      return attributeClassFullName.EndsWith(".GenerateFactoryAttribute") || attributeClassFullName.EndsWith(".GenerateFactory");
                                                                                  });
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException("Could not read GenerateFactoryAttribute from {0} type.".FormatWith(concreteClassTypeSymbol.Name), e);
            }

            if (factoryAttribute.AttributeClass.Kind == SymbolKind.ErrorType)
            {
                throw new InvalidOperationException("Cannot parse the attribute in {0}. Please check if the project is ready to compile successfully.".FormatWith(concreteClassTypeSymbol));
            }

            return factoryAttribute;
        }

        private static string GetFactoryClassGenericName(ClassDeclarationSyntax concreteTypeDeclarationSyntax,
                                                         INamedTypeSymbol factoryInterfaceTypeSymbol)
        {
            var factoryClassName = "{0}Factory{1}".FormatWith(concreteTypeDeclarationSyntax.Identifier.ValueText, GetTypeParametersDeclaration(factoryInterfaceTypeSymbol.TypeParameters));

            return factoryClassName;
        }

        private static string GetSafeFileName(string identifier)
        {
            return identifier.Contains("<")
                       ? identifier.Substring(0, identifier.IndexOf("<", StringComparison.Ordinal))
                       : identifier;
        }

        private static IMethodSymbol[] GetSuitableFactoryInterfaceMethods(INamedTypeSymbol concreteClassTypeSymbol,
                                                                          INamedTypeSymbol factoryInterfaceTypeSymbol)
        {
            return factoryInterfaceTypeSymbol
                .AllInterfaces
                .Add(factoryInterfaceTypeSymbol)
                .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
                .Where(methodSymbol => concreteClassTypeSymbol.AllInterfaces.Select(i => i.OriginalDefinition).Contains(methodSymbol.ReturnType.OriginalDefinition))
                .ToArray();
        }

        private static string GetTypeParametersDeclaration(ImmutableArray<ITypeParameterSymbol> typeParameterSymbols)
        {
            var typeParameters = string.Empty;

            if (typeParameterSymbols.Length > 0)
            {
                typeParameters = "<{0}>".FormatWith(string.Join(" ,", typeParameterSymbols.Select(t => t.Name)));
            }

            return typeParameters;
        }

        private static string GetXmlDocSafeTypeName(string typeName)
        {
            return typeName.Replace("<", "{").Replace(">", "}");
        }

        private static bool IsTypeDeclarationSyntaxFactoryTarget(ClassDeclarationSyntax classDeclarationSyntax)
        {
            var attributeListSyntaxes = classDeclarationSyntax.AttributeLists;

            return attributeListSyntaxes.Count > 0 &&
                   attributeListSyntaxes.SelectMany(a => a.Attributes).Any(x =>
                                                                           {
                                                                               var qualifiedNameSyntax = x.Name as Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax;
                                                                               var attributeClassName = qualifiedNameSyntax != null
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
            var ellapsedTime = TimeSpan.FromMilliseconds(chrono.ElapsedMilliseconds);
            var statisticsFormat = obsoleteFactoryFilePaths.Any() || newFactoryFilePaths.Any()
                                       ? "{0} (+{1}/-{2})"
                                       : "{0}";
            var factoryStatistics = statisticsFormat.FormatWith("factory".ToQuantity(generatedFactoryFilePaths.Length), newFactoryFilePaths.Length, obsoleteFactoryFilePaths.Length);

            Logger.InfoFormat("Generated {0} in {1}.", factoryStatistics, ellapsedTime.Humanize(2));
        }

        private static IMethodSymbol SelectConstructorFromFactoryMethod(IMethodSymbol factoryMethod,
                                                                        INamedTypeSymbol concreteClassSymbol)
        {
            if (!concreteClassSymbol.AllInterfaces.Select(i => i.OriginalDefinition).Contains(factoryMethod.ReturnType.OriginalDefinition))
            {
                var message = "The factory method does not return the correct type (i.e. a type inherited by {0}). Are you sure the attribute maps to the correct factory type? Currently it maps to {1}."
                    .FormatWith(concreteClassSymbol, factoryMethod.ContainingType);
                throw new InvalidOperationException(message);
            }

            var factoryMethodParameters = factoryMethod.Parameters;
            var instanceConstructors = concreteClassSymbol.InstanceConstructors
                                                          .Where(c => c.DeclaredAccessibility == Accessibility.Public)
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
                throw new InvalidOperationException("Could not select a constructor from {0} type with the following parameters: ({1}).".FormatWith(concreteClassSymbol.Name, string.Join(", ", factoryMethodParameters.Select(x => x.Name))), e);
            }
        }

        private static void UpdateDocument(Document document,
                                           string newText)
        {
            // Small hack to force files to be saved without changing encoding (Roslyn is currently saving files in Windows 1252 codepage).
            if (File.Exists(document.FilePath))
            {
                string originalText;
                Encoding originalEncoding;

                using (var streamReader = new StreamReader(document.FilePath, Encoding.Default))
                {
                    originalText = streamReader.ReadToEnd();

                    originalEncoding = streamReader.CurrentEncoding;
                }

                if (!newText.Equals(originalText))
                {
                    File.WriteAllText(document.FilePath, newText, originalEncoding);
                }
            }
            else
            {
                File.WriteAllText(document.FilePath, newText, Encoding.UTF8);
            }
        }

        private async Task<ICollection<string>> GenerateFactoriesInProjectAsync(Compilation compilation)
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

                // new SyntaxWalker().Visit(syntaxRootNode);
                if (classDeclarations.Any())
                {
                    foreach (var classDeclarationSyntax in classDeclarations)
                    {
                        Logger.DebugFormat("[{0}]", string.Join(", ", classDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes).Select(x => x.Name)));
                        Logger.Debug(classDeclarationSyntax.Identifier.Value);

                        var fullyQualifiedMetadataName = GetDeclarationFullName(classDeclarationSyntax);
                        var resolvedConcreteClassType = compilation.GetTypeByMetadataName(fullyQualifiedMetadataName);

                        var generatedFilePath = this.GenerateFactoryForClass(classDeclarationSyntax, resolvedConcreteClassType);
                        generatedFactoriesList.Add(generatedFilePath);

                        Logger.Debug(new string('-', 80));
                    }
                }
            }

            return generatedFactoriesList;
        }

        private string GenerateFactoryForClass(ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                               INamedTypeSymbol concreteClassTypeSymbol)
        {
            var factoryAttribute = GetFactoryAttributeFromTypeSymbol(concreteClassTypeSymbol);

            INamedTypeSymbol factoryInterfaceTypeSymbol;
            if (factoryAttribute.ConstructorArguments != null && factoryAttribute.ConstructorArguments.Any())
            {
                var typeOfArgument = factoryAttribute.ConstructorArguments.Single();
                factoryInterfaceTypeSymbol = (INamedTypeSymbol)typeOfArgument.Value;

                if (factoryInterfaceTypeSymbol.IsUnboundGenericType)
                {
                    factoryInterfaceTypeSymbol = factoryInterfaceTypeSymbol.ConstructedFrom;
                }
            }
            else
            {
                throw new InvalidOperationException("Factory type must be specified in {0} on {1}.".FormatWith(factoryAttribute.AttributeClass, GetDeclarationFullName(concreteClassDeclarationSyntax)));
            }

            var factoryClassGenericName = GetFactoryClassGenericName(concreteClassDeclarationSyntax, factoryInterfaceTypeSymbol);
            var factoryInterfaceFullName = factoryInterfaceTypeSymbol.ToString();

            Logger.InfoFormat("Rendering factory implementation {0}\r\n\tfor factory interface {1}\r\n\ttargeting {2}...", factoryClassGenericName, factoryInterfaceFullName, GetDeclarationFullName(concreteClassDeclarationSyntax));

            var generatedFilePath = this.RenderFactoryImplementation(concreteClassDeclarationSyntax, concreteClassTypeSymbol, factoryInterfaceTypeSymbol, GetSafeFileName(factoryClassGenericName));

            return generatedFilePath;
        }

        private void RemoveObsoleteFactoriesFromSolution(string[] obsoleteFactoryFilePaths)
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
                var newProject = obsoleteFactoryDocument.Project.RemoveDocument(obsoleteFactoryDocument.Id);

                Logger.InfoFormat("{0} from {1}.", Path.GetFileName(obsoleteFactoryFilePath), newProject.Name);
                File.Delete(obsoleteFactoryFilePath);

                newSolution = newProject.Solution;
            }

            Interlocked.Exchange(ref this.solution, newSolution);
        }

        private string RenderFactoryImplementation(ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                                   INamedTypeSymbol concreteClassTypeSymbol,
                                                   INamedTypeSymbol factoryInterfaceTypeSymbol,
                                                   string factoryName)
        {
            var fileName = "{0}.Generated.cs".FormatWith(factoryName);
            var factoryInterfaceName = factoryInterfaceTypeSymbol.Name;

            var project = this.solution.Projects.Single(proj => proj.GetDocument(concreteClassDeclarationSyntax.SyntaxTree) != null);
            var typeDeclarationDocument = project.GetDocument(concreteClassDeclarationSyntax.SyntaxTree);
            var factoryGeneratorEngine = new FactoryGeneratorEngine(project, this.templatePath);

            var usingsToFilterOut = new[] { concreteClassTypeSymbol.ContainingNamespace.ToString() };
            var outerUsingDeclarations = FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<CompilationUnitSyntax>().Usings, usingsToFilterOut);
            var innerUsingDeclarations = FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<NamespaceDeclarationSyntax>().Usings, usingsToFilterOut);


            var factoryInterfaceMethods = GetSuitableFactoryInterfaceMethods(concreteClassTypeSymbol, factoryInterfaceTypeSymbol);

            var allContractMethodParameters = factoryInterfaceMethods.SelectMany(contractTypeMethod => contractTypeMethod.Parameters).ToArray();
            if (!factoryInterfaceMethods.Any())
            {
                throw new InvalidOperationException("The interface {0} has no suitable method returning any interface implemented by {1}. Please check if their is any.".FormatWith(factoryInterfaceTypeSymbol, concreteClassTypeSymbol));
            }

            var allConstructorParameters = factoryInterfaceMethods.Select(factoryMethod => SelectConstructorFromFactoryMethod(factoryMethod, concreteClassTypeSymbol))
                                                                  .SelectMany(selectedConstructor => selectedConstructor.Parameters).ToArray();
            var constructorParametersUsingSelfType = allConstructorParameters.Where(p => p.Type.Name == factoryInterfaceTypeSymbol.Name).ToArray();
            var constructorParametersWithoutSelfType = allConstructorParameters.Except(constructorParametersUsingSelfType)
                                                                               .ToArray();
            var injectedParameters = (from parameter in (IEnumerable<IParameterSymbol>)constructorParametersWithoutSelfType
                                      where !allContractMethodParameters.Any(contractMethodParameter => CompareParameters(contractMethodParameter, parameter))
                                      select parameter).Distinct(new ParameterEqualityComparer()).ToArray();

            var concreteClassName = GetXmlDocSafeTypeName(concreteClassTypeSymbol.ToString());
            var @namespace = GetDeclarationNamespaceFullName(concreteClassDeclarationSyntax);

            // Class attributes
            var classAttributes = new[]
                                  {
                                      new Attribute("global::System.CodeDom.Compiler.GeneratedCode", new[] { new Argument("\"DeveloperInTheFlow.FactoryGenerator\"", string.Empty), new Argument(string.Format("\"{0}\"", this.version), string.Empty) }),
                                      new Attribute("global::System.Diagnostics.DebuggerNonUserCodeAttribute")
                                  };

            // Generic types of the factory
            var classGenericTypes = this.genericTypeBuilderService.Build(factoryInterfaceTypeSymbol.TypeParameters);

            // Constructor of the factory
            var constructor = this.constructorBuilderService.Build(concreteClassDeclarationSyntax, injectedParameters);

            // Fields of the factory
            var fields = this.fieldsBuilderService.Build(injectedParameters).ToArray();

            // Methods of the factory
            var methods = this.methodsBuilderService.Build(concreteClassTypeSymbol, fields, injectedParameters, factoryInterfaceMethods, factoryInterfaceName);

            // Interface of the factory
            var inherit = factoryInterfaceTypeSymbol.ToString();

            // The factory
            var factoryClass = new Class(classAttributes, concreteClassName, constructor, methods, fields, classGenericTypes, inherit, factoryName);

            // The file containing the factory
            var factoryFile = new FactoryFile(@namespace,
                factoryClass,
                innerUsingDeclarations.ToFullString(),
                outerUsingDeclarations.ToFullString());

            // The result of the generator
            var factoryResult = factoryGeneratorEngine.Generate(fileName, typeDeclarationDocument.Folders, factoryFile);

            var existingDocument = project.Documents.FirstOrDefault(doc => doc.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));

            if (existingDocument != null)
            {
                UpdateDocument(existingDocument, factoryResult.Code);

                return existingDocument.FilePath;
            }

            this.solution = factoryResult.Document.Project.Solution;

            var projectFolder = Path.GetDirectoryName(project.FilePath);
            if (projectFolder == null)
            {
                throw new InvalidOperationException("Cannot determines the folder path of the project.");
            }

            var generatedFileFolderPath = Path.Combine(projectFolder, string.Join(@"\", typeDeclarationDocument.Folders));
            var generatedFilePath = Path.Combine(generatedFileFolderPath, factoryResult.Document.Name);

            return generatedFilePath;
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