namespace DeveloperInTheFlow.AutoGenFactories
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    public class FactoryGenerator
    {
        #region Fields

        private readonly string[] attributeImportList;

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

            var projectDependencyGraph = await this.solution.GetProjectDependencyGraphAsync();
            var existingFactoriesTasksList = new List<Task<ICollection<string>>>(16);
            var newFactoriesTasksList = new List<Task<ICollection<string>>>(16);
            foreach (var projectId in projectDependencyGraph.GetTopologicallySortedProjects())
            {
                var project = this.solution.GetProject(projectId);
                var compilation = await project.GetCompilationAsync();

                var catalogTask = CatalogGeneratedFactoriesInProjectAsync(compilation);

                existingFactoriesTasksList.Add(catalogTask);
                newFactoriesTasksList.Add(this.GenerateFactoriesInProjectAsync(compilation));
            }

            await Task.WhenAll(existingFactoriesTasksList.Cast<Task>().Concat(newFactoriesTasksList));

            var existingGeneratedFactories = existingFactoriesTasksList.SelectMany(task => task.Result).ToArray();
            var newGeneratedFactories = newFactoriesTasksList.SelectMany(task => task.Result).ToArray();

            this.RemoveObsoleteFactoriesFromSolution(existingGeneratedFactories, newGeneratedFactories);

            this.workspace.TryApplyChanges(this.solution);

            chrono.Stop();

            var ellapsedTime = TimeSpan.FromMilliseconds(chrono.ElapsedMilliseconds);

            Console.WriteLine("Completed in {0}", ellapsedTime);
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

            foreach (var injectedParameter in injectedParameters)
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
            foreach (var project in solution.Projects)
            {
                var document = project.Documents.FirstOrDefault(d => d.FilePath == filePath);
                if (document != null)
                {
                    return document;
                }
            }

            return null;
        }

        private static string GetDeclarationFullName(TypeDeclarationSyntax typeDeclarationSyntax)
        {
            var typeParameterCount = typeDeclarationSyntax.TypeParameterList == null
                                         ? 0
                                         : typeDeclarationSyntax.TypeParameterList.Parameters.Count;
            var fullyQualifiedName = string.Format("{0}.{1}{2}",
                                                   GetDeclarationNamespaceFullName(typeDeclarationSyntax),
                                                   typeDeclarationSyntax.Identifier.ValueText,
                                                   typeParameterCount == 0
                                                       ? string.Empty
                                                       : string.Format("`{0}", typeParameterCount));

            return fullyQualifiedName;
        }

        private static string GetDeclarationNamespaceFullName(BaseTypeDeclarationSyntax typeDeclarationSyntax)
        {
            var namespaceDeclarationSyntax = typeDeclarationSyntax.FirstAncestorOrSelf<NamespaceDeclarationSyntax>();
            return namespaceDeclarationSyntax.Name.ToString();
        }

        private static AttributeData GetFactoryAttributeFromTypeSymbol(INamedTypeSymbol concreteClassTypeSymbol)
        {
            return concreteClassTypeSymbol.GetAttributes().Single(a =>
                                                                  {
                                                                      var attributeClassFullName = a.AttributeClass.ToString();
                                                                      return attributeClassFullName.EndsWith(".GenerateFactoryAttribute");
                                                                  });
        }

        private static string GetFactoryClassGenericName(ClassDeclarationSyntax concreteTypeDeclarationSyntax)
        {
            var factoryClassName = string.Format("{0}Factory{1}", concreteTypeDeclarationSyntax.Identifier.ValueText, concreteTypeDeclarationSyntax.TypeParameterList);

            return factoryClassName;
        }

        private static string GetParameterListCodeSection(IMethodSymbol constructor,
                                                          IEnumerable<IParameterSymbol> injectedParameters)
        {
            var parametersRepresentation = constructor.Parameters.Select(parameter =>
                                                                         string.Format("{0}{1}{2}",
                                                                                       injectedParameters.Select(p => p.Name).Contains(parameter.Name)
                                                                                           ? "this."
                                                                                           : string.Empty,
                                                                                       string.Empty /*GetParameterModifiers(parameter)*/,
                                                                                       parameter.Name));
            if (constructor.Parameters.Length > 1)
            {
                return string.Format("\r\n                {0}", string.Join(", \r\n                ", parametersRepresentation));
            }

            return string.Join(", ", parametersRepresentation);
        }

        private static string GetSafeFileName(string identifier)
        {
            return identifier.Contains("<")
                       ? identifier.Substring(0, identifier.IndexOf("<", StringComparison.Ordinal))
                       : identifier;
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

        private static IMethodSymbol SelectConstructorFromFactoryMethod(IMethodSymbol factoryMethod,
                                                                        INamedTypeSymbol concreteClassSymbol)
        {
            if (!concreteClassSymbol.AllInterfaces.Select(i => i.OriginalDefinition).Contains(factoryMethod.ReturnType.OriginalDefinition))
            {
                var message = string.Format("The factory method does not return the correct type (i.e. a type inherited by {0}). Are you sure the attribute maps to the correct factory type? Currently it maps to {1}.",
                                            concreteClassSymbol,
                                            factoryMethod.ContainingType);
                throw new InvalidOperationException(message);
            }

            var factoryMethodParameters = factoryMethod.Parameters;
            var instanceConstructors = concreteClassSymbol.InstanceConstructors
                                                          .Where(c => c.DeclaredAccessibility == Accessibility.Public);

            var selectedConstructor = instanceConstructors.OrderBy(c => c.Parameters.Length)
                                                          .First(c => factoryMethodParameters.Select(fmp => fmp.Name)
                                                                                             .All(c.Parameters.Select(cp => cp.Name).Contains));

            return selectedConstructor;
        }

        private static void UpdateDocument(Document document,
                                           string newText)
        {
            string originalText;
            Encoding originalEncoding;

            // Small hack to force files to be saved without changing encoding (Roslyn is currently saving files in Windows 1252 codepage).
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
                factoryConstructorsStringBuilder.AppendLine(string.Format("        [{0}]", string.Join(", ", importedConstructorAttributes.Select(cs => cs.ToString()))));
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
                                                          string.Join("\r\n            ", injectedParameters.Select(p => string.Format("this.{0} = {0};", p.Name))));
            factoryConstructorsStringBuilder.AppendLine();
            factoryConstructorsStringBuilder.AppendLine();

            factoryConstructorsStringBuilder.AppendLine("        #endregion");
            factoryConstructorsStringBuilder.AppendLine();

            return factoryConstructorsStringBuilder.ToString();
        }

        private string BuildFactoryImplementationMethodsCodeSection(INamedTypeSymbol concreteClassSymbol,
                                                                    INamedTypeSymbol factoryInterfaceSymbol,
                                                                    IParameterSymbol[] injectedParameters)
        {
            var factoryMethodsStringBuilder = new StringBuilder();
            var factoryMethods = factoryInterfaceSymbol.GetMembers()
                                                       .OfType<IMethodSymbol>()
                                                       .Where(methodSymbol => concreteClassSymbol.AllInterfaces.Select(i => i.OriginalDefinition).Contains(methodSymbol.ReturnType.OriginalDefinition))
                                                       .ToArray();

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
                                                                                                            ? string.Format("[{0}] ", string.Join(", ", attributes.Select(a => a.ToString())))
                                                                                                            : string.Empty;

                                                                                 return string.Format("{0}{1} {2}", attributeSection, p.Type, p.Name);
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
                            var relevantLinesAsXmlDoc = relevantLines.Select(line => string.Format("        /// {0}", line.Substring(indent)));

                            factoryMethodsStringBuilder.AppendLine(string.Join("\r\n", relevantLinesAsXmlDoc));
                        }
                    }

                    if (factoryMethodParameters.Length > 1)
                    {
                        factoryMethodsStringBuilder.AppendFormat(@"        public {0} Create(
            {1})
        {{
            return new {2}({3});
        }}",
                                                                 factoryMethod.ReturnType,
                                                                 string.Join(",\r\n            ", parameterListAsText),
                                                                 concreteClassSymbol,
                                                                 GetParameterListCodeSection(selectedConstructor, injectedParameters));
                    }
                    else
                    {
                        factoryMethodsStringBuilder.AppendFormat(@"        public {0} Create({1})
        {{
            return new {2}({3});
        }}",
                                                                 factoryMethod.ReturnType,
                                                                 string.Join(", ", parameterListAsText),
                                                                 concreteClassSymbol,
                                                                 GetParameterListCodeSection(selectedConstructor, injectedParameters));
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

                if (classDeclarations.Any())
                {
                    foreach (var classDeclarationSyntax in classDeclarations)
                    {
                        Console.WriteLine("[{0}]", string.Join(", ", classDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes).Select(x => x.Name)));
                        Console.WriteLine(classDeclarationSyntax.Identifier.Value);
                        Console.WriteLine();

                        var fullyQualifiedMetadataName = GetDeclarationFullName(classDeclarationSyntax);
                        var resolvedConcreteClassType = compilation.GetTypeByMetadataName(fullyQualifiedMetadataName);

                        var generatedFilePath = this.GenerateFactoryForClass(classDeclarationSyntax, resolvedConcreteClassType);
                        generatedFactoriesList.Add(generatedFilePath);

                        Console.WriteLine(new string('-', 80));
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
                throw new InvalidOperationException(string.Format("Factory type must be specified in {0} on {1}.", factoryAttribute.AttributeClass, GetDeclarationFullName(concreteClassDeclarationSyntax)));
            }

            var factoryClassGenericName = GetFactoryClassGenericName(concreteClassDeclarationSyntax);
            var factoryInterfaceFullName = factoryInterfaceTypeSymbol.ToString();

            Console.WriteLine("Rendering factory implementation {0}\r\n\tfor factory interface {1}\r\n\ttargeting {2}...", factoryClassGenericName, factoryInterfaceFullName, GetDeclarationFullName(concreteClassDeclarationSyntax));

            var generatedFilePath = this.RenderFactoryImplementation(concreteClassDeclarationSyntax, concreteClassTypeSymbol, factoryInterfaceTypeSymbol, GetSafeFileName(factoryClassGenericName));

            return generatedFilePath;
        }

        private void RemoveObsoleteFactoriesFromSolution(string[] existingGeneratedFactoryFilePaths,
                                                         string[] newGeneratedFactoryFilePaths)
        {
            var obsoleteFactoryFilePaths = existingGeneratedFactoryFilePaths.Where(existingFactory => !newGeneratedFactoryFilePaths.Contains(existingFactory)).ToArray();
            var newSolution = this.solution;

            foreach (var obsoleteFactoryFilePath in obsoleteFactoryFilePaths)
            {
                var obsoleteFactoryDocument = FindDocumentFromPath(newSolution, obsoleteFactoryFilePath);
                var newProject = obsoleteFactoryDocument.Project.RemoveDocument(obsoleteFactoryDocument.Id);

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
            var fileName = string.Format("{0}.Generated.cs", factoryName);

            var usingsToFilterOut = new[] { concreteClassTypeSymbol.ContainingNamespace.ToString() };
            var outerUsingDeclarations = FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<CompilationUnitSyntax>().Usings, usingsToFilterOut);
            var innerUsingDeclarations = FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<NamespaceDeclarationSyntax>().Usings, usingsToFilterOut);

            var contractTypeMethods = factoryInterfaceTypeSymbol.GetMembers().OfType<IMethodSymbol>().ToArray();
            var allContractMethodParameters = contractTypeMethods.SelectMany(contractTypeMethod => contractTypeMethod.Parameters).ToArray();
            if (!contractTypeMethods.Any())
            {
                Console.WriteLine("No Methods! Looking for parent interfaces...");
                foreach (var factoryParentInterfaceContractType in factoryInterfaceTypeSymbol.AllInterfaces)
                {
                    Console.WriteLine("  inheritedFrom = {0}", factoryParentInterfaceContractType.ToDisplayString());

                    contractTypeMethods = factoryParentInterfaceContractType.GetMembers().OfType<IMethodSymbol>().ToArray();
                    allContractMethodParameters = contractTypeMethods.SelectMany(contractTypeMethod => contractTypeMethod.Parameters).ToArray();
                    var parametersString = string.Join(", ", allContractMethodParameters.Select(x => x.Name));

                    Console.WriteLine("  parameters from inheritance : {0}", parametersString);
                }
            }

            var allConstructorParameters = contractTypeMethods.Select(factoryMethod => SelectConstructorFromFactoryMethod(factoryMethod, concreteClassTypeSymbol))
                                                              .SelectMany(selectedConstructor => selectedConstructor.Parameters)
                                                              .ToArray();
            var injectedParameters = (from parameter in (IEnumerable<IParameterSymbol>)allConstructorParameters
                                      where !allContractMethodParameters.Any(contractMethodParameter => CompareParameters(contractMethodParameter, parameter))
                                      select parameter).ToArray();

            var factoryFieldsCodeSection = BuildFactoryImplementationFieldsCodeSection(injectedParameters);
            var factoryConstructorsCodeSection = this.BuildFactoryImplementationConstructorsCodeSection(factoryName, concreteClassDeclarationSyntax, injectedParameters);
            var factoryMethodsCodeSection = this.BuildFactoryImplementationMethodsCodeSection(concreteClassTypeSymbol, factoryInterfaceTypeSymbol, injectedParameters);

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
                                                 ? string.Format("{0}\r\n", innerUsingDeclarations.ToFullString())
                                                 : string.Empty)
                .Replace("<#=innerUsings#>", innerUsingDeclarations.Any()
                                                 ? string.Format("\r\n{0}", innerUsingDeclarations.ToFullString())
                                                 : string.Empty)
                .Replace("<#=factoryTypeName#>", GetFactoryClassGenericName(concreteClassDeclarationSyntax))
                .Replace("<#=factoryContractTypeFullName#>", factoryInterfaceTypeSymbol.ToString())
                .Replace("<#=GeneratedCodeAttribute#>", "global::System.CodeDom.Compiler.GeneratedCode(\"AutoGenFactories\", \"0.1\")")
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

            return newDocument.FilePath;
        }

        #endregion
    }
}