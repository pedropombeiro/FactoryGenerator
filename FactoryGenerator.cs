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

    using Humanizer;

    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    public class FactoryGenerator
    {
        #region Static Fields

        private static readonly ILog Logger = LogManager.GetLogger<FactoryGenerator>();

        #endregion

        #region Fields

        private readonly string[] attributeImportList;

        private readonly string version = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;

        private readonly Workspace workspace;

        private readonly bool writeXmlDoc;

        private Solution solution;

        #endregion

        #region Constructors and Destructors

        [DebuggerStepThrough]
        public FactoryGenerator(Workspace workspace,
                                Solution solution,
                                string[] attributeImportList,
                                bool writeXmlDoc)
        {
            this.workspace = workspace;
            this.solution = solution;
            this.attributeImportList = attributeImportList;
            this.writeXmlDoc = writeXmlDoc;
        }

        #endregion

        #region Public Methods and Operators

        public async Task ExecuteAsync()
        {
            var chrono = new Stopwatch();

            chrono.Start();

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

        private static string BuildFactoryImplementationFieldsCodeSection(IParameterSymbol[] injectedParameters)
        {
            if (injectedParameters.Length == 0)
            {
                return string.Empty;
            }

            var fieldsStringBuilder = new StringBuilder();

            fieldsStringBuilder.AppendLine("        #region Fields");
            fieldsStringBuilder.AppendLine();

            foreach (var injectedParameter in injectedParameters.Distinct(new ParameterEqualityComparer()))
            {
                fieldsStringBuilder.AppendFormat("        private readonly {0} {1};",
                                                 injectedParameter.Type,
                                                 injectedParameter.Name);
                fieldsStringBuilder.AppendLine();
                fieldsStringBuilder.AppendLine();
            }

            fieldsStringBuilder.AppendLine("        #endregion");
            fieldsStringBuilder.AppendLine();
            return fieldsStringBuilder.ToString();
        }

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

        private static string GetParameterListCodeSection(IMethodSymbol constructor,
                                                          IEnumerable<IParameterSymbol> injectedParameters,
                                                          string factoryInterfaceName)
        {
            var parametersRepresentation = constructor.Parameters.Select(parameter =>
                                                                         "{0}{1}{2}".FormatWith(injectedParameters.Select(p => p.Name).Contains(parameter.Name)
                                                                                                    ? "this."
                                                                                                    : string.Empty,
                                                                                                string.Empty /*GetParameterModifiers(parameter)*/,
                                                                                                parameter.Type.Name == factoryInterfaceName
                                                                                                    ? "this"
                                                                                                    : parameter.Name));
            if (constructor.Parameters.Length > 1)
            {
                return "\r\n                {0}".FormatWith(string.Join(", \r\n                ", parametersRepresentation));
            }

            return string.Join(", ", parametersRepresentation);
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

        private string BuildFactoryImplementationConstructorsCodeSection(string factoryImplementationTypeName,
                                                                         ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                                                         ICollection<IParameterSymbol> injectedParameters)
        {
            if (injectedParameters.Count == 0)
            {
                return string.Empty;
            }

            var factoryConstructorsStringBuilder = new StringBuilder();
            var allConstructorAttributes = concreteClassDeclarationSyntax.Members.OfType<ConstructorDeclarationSyntax>()
                                                                         .SelectMany(cs => cs.AttributeLists.SelectMany(al => al.Attributes))
                                                                         .ToArray();
            var importedConstructorAttributes = allConstructorAttributes.Where(attributeSyntax =>
                                                                               {
                                                                                   var attributeName = attributeSyntax.Name.ToString();
                                                                                   return this.attributeImportList.Any(attributeName.Contains);
                                                                               })
                                                                        .ToArray();

            factoryConstructorsStringBuilder.AppendLine("        #region Constructors");
            factoryConstructorsStringBuilder.AppendLine();

            if (importedConstructorAttributes.Any())
            {
                factoryConstructorsStringBuilder.AppendLine("        [{0}]".FormatWith(string.Join(", ", importedConstructorAttributes.Select(cs => cs.ToString()))));
            }

            if (injectedParameters.Count == 1)
            {
                factoryConstructorsStringBuilder.AppendFormat(@"        public {0}({1})",
                                                              factoryImplementationTypeName,
                                                              injectedParameters.Single().DeclaringSyntaxReferences[0].GetSyntax());
            }
            else
            {
                factoryConstructorsStringBuilder.AppendFormat(@"        public {0}(
            {1})",
                                                              factoryImplementationTypeName,
                                                              string.Join(",\r\n            ", injectedParameters.Select(p => p.DeclaringSyntaxReferences[0].GetSyntax().ToString())));
            }

            factoryConstructorsStringBuilder.AppendFormat(@"
        {{
            {0}
        }}",
                                                          string.Join("\r\n            ", injectedParameters.Select(p => "this.{0} = {0};".FormatWith(p.Name))));
            factoryConstructorsStringBuilder.AppendLine();
            factoryConstructorsStringBuilder.AppendLine();

            factoryConstructorsStringBuilder.AppendLine("        #endregion");
            factoryConstructorsStringBuilder.AppendLine();

            return factoryConstructorsStringBuilder.ToString();
        }

        private string BuildFactoryImplementationMethodsCodeSection(INamedTypeSymbol concreteClassSymbol,
                                                                    IParameterSymbol[] injectedParameters,
                                                                    IMethodSymbol[] factoryMethods,
                                                                    string factoryInterfaceName)
        {
            var factoryMethodsStringBuilder = new StringBuilder();

            if (factoryMethods.Any())
            {
                factoryMethodsStringBuilder.AppendLine("        #region Public Factory Methods");
                factoryMethodsStringBuilder.AppendLine();

                foreach (var factoryMethod in factoryMethods)
                {
                    var selectedConstructor = SelectConstructorFromFactoryMethod(factoryMethod, concreteClassSymbol);
                    var factoryMethodParameters = factoryMethod.Parameters;
                    var parameterListAsText = factoryMethodParameters.Select(p =>
                                                                             {
                                                                                 var attributes = p.GetAttributes();
                                                                                 var attributeSection = attributes.Any()
                                                                                                            ? "[{0}] ".FormatWith(string.Join(", ", attributes.Select(a => a.ToString())))
                                                                                                            : string.Empty;

                                                                                 return "{0}{1} {2}".FormatWith(attributeSection, p.Type, p.Name);
                                                                             });

                    if (this.writeXmlDoc)
                    {
                        var documentationCommentXml = factoryMethod.GetDocumentationCommentXml();
                        if (!string.IsNullOrWhiteSpace(documentationCommentXml))
                        {
                            var relevantLines = documentationCommentXml.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                                                       .SkipWhile(line => !line.StartsWith(" "))
                                                                       .TakeWhile(line => line != "</member>")
                                                                       .ToArray();
                            var indent = relevantLines.First().Length - relevantLines.First().TrimStart().Length;
                            var relevantLinesAsXmlDoc = relevantLines.Select(line => "        /// {0}".FormatWith(line.Substring(indent)));

                            factoryMethodsStringBuilder.AppendLine(string.Join("\r\n", relevantLinesAsXmlDoc));
                        }
                    }

                    if (factoryMethodParameters.Length > 1)
                    {
                        factoryMethodsStringBuilder.AppendFormat(@"        public {0} Create{4}(
            {1})
        {{
            return new {2}({3});
        }}",
                                                                 factoryMethod.ReturnType,
                                                                 string.Join(",\r\n            ", parameterListAsText),
                                                                 concreteClassSymbol,
                                                                 GetParameterListCodeSection(selectedConstructor, injectedParameters, factoryInterfaceName),
                                                                 GetTypeParametersDeclaration(factoryMethod.TypeParameters));
                    }
                    else
                    {
                        factoryMethodsStringBuilder.AppendFormat(@"        public {0} Create{4}({1})
        {{
            return new {2}({3});
        }}",
                                                                 factoryMethod.ReturnType,
                                                                 string.Join(", ", parameterListAsText),
                                                                 concreteClassSymbol,
                                                                 GetParameterListCodeSection(selectedConstructor, injectedParameters, factoryInterfaceName),
                                                                 GetTypeParametersDeclaration(factoryMethod.TypeParameters));
                    }

                    factoryMethodsStringBuilder.AppendLine();
                    factoryMethodsStringBuilder.AppendLine();
                }

                factoryMethodsStringBuilder.Append("        #endregion");
            }

            return factoryMethodsStringBuilder.ToString();
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
                                      select parameter).ToArray();

            var factoryFieldsCodeSection = BuildFactoryImplementationFieldsCodeSection(injectedParameters);
            var factoryConstructorsCodeSection = this.BuildFactoryImplementationConstructorsCodeSection(factoryName, concreteClassDeclarationSyntax, injectedParameters);
            var factoryMethodsCodeSection = this.BuildFactoryImplementationMethodsCodeSection(concreteClassTypeSymbol, injectedParameters, factoryInterfaceMethods, factoryInterfaceName);

            var code = @"#pragma warning disable 1591

<#=outerUsings#>namespace <#=namespaceFullName#>
{<#=innerUsings#>
    /// <summary>
    /// The implementation for the factory generating <see cref=""<#=concreteXmlDocSafeTypeName#>"" /> instances.
    /// </summary>
    [<#=GeneratedCodeAttribute#>]
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    public partial class <#=factoryTypeName#> : <#=factoryContractTypeFullName#>
    {
<#=factoryFields#><#=factoryConstructors#><#=factoryMethods#>
    }
}"
                .Replace("<#=namespaceFullName#>", GetDeclarationNamespaceFullName(concreteClassDeclarationSyntax))
                .Replace("<#=outerUsings#>", outerUsingDeclarations.Any()
                                                 ? "{0}\r\n".FormatWith(innerUsingDeclarations.ToFullString())
                                                 : string.Empty)
                .Replace("<#=innerUsings#>", innerUsingDeclarations.Any()
                                                 ? "\r\n{0}".FormatWith(innerUsingDeclarations.ToFullString())
                                                 : string.Empty)
                .Replace("<#=factoryTypeName#>", GetFactoryClassGenericName(concreteClassDeclarationSyntax, factoryInterfaceTypeSymbol))
                .Replace("<#=factoryContractTypeFullName#>", factoryInterfaceTypeSymbol.ToString())
                .Replace("<#=GeneratedCodeAttribute#>", string.Format("global::System.CodeDom.Compiler.GeneratedCode(\"DeveloperInTheFlow.FactoryGenerator\", \"{0}\")", this.version))
                .Replace("<#=concreteXmlDocSafeTypeName#>", GetXmlDocSafeTypeName(concreteClassTypeSymbol.ToString()))
                .Replace("<#=factoryFields#>", factoryFieldsCodeSection)
                .Replace("<#=factoryConstructors#>", factoryConstructorsCodeSection)
                .Replace("<#=factoryMethods#>", factoryMethodsCodeSection);

            var project = this.solution.Projects.First(proj => proj.GetDocument(concreteClassDeclarationSyntax.SyntaxTree) != null);
            var typeDeclarationDocument = project.GetDocument(concreteClassDeclarationSyntax.SyntaxTree);
            var existingDocument = project.Documents.FirstOrDefault(doc => doc.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));

            if (existingDocument != null)
            {
                UpdateDocument(existingDocument, code);

                return existingDocument.FilePath;
            }

            var newDocument = project.AddDocument(fileName, code, typeDeclarationDocument.Folders);
            this.solution = newDocument.Project.Solution;

            var projectFolder = Path.GetDirectoryName(project.FilePath);
            var generatedFileFolderPath = Path.Combine(projectFolder, string.Join(@"\", typeDeclarationDocument.Folders));
            var generatedFilePath = Path.Combine(generatedFileFolderPath, newDocument.Name);

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
                return obj.GetHashCode();
            }

            #endregion
        }
    }
}