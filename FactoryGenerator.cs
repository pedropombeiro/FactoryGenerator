namespace DeveloperInTheFlow.AutoGenFactories
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Text;

    public class FactoryGenerator
    {
        #region Fields

        private readonly Workspace workspace;

        private Solution solution;

        #endregion

        #region Constructors and Destructors

        [DebuggerStepThrough]
        public FactoryGenerator(Workspace workspace,
                               Solution solution)
        {
            this.workspace = workspace;
            this.solution = solution;
        }

        #endregion

        #region Public Methods and Operators

        public async Task ExecuteAsync()
        {
            var chrono = new Stopwatch();

            chrono.Start();

            var projectDependencyGraph = await this.solution.GetProjectDependencyGraphAsync();
            var tasksList = new List<Task>(16);
            foreach (var projectId in projectDependencyGraph.GetTopologicallySortedProjects())
            {
                var project = this.solution.GetProject(projectId);
                var compilation = await project.GetCompilationAsync();

                tasksList.Add(this.GenerateFactoriesInProjectAsync(compilation));
            }

            await Task.WhenAll(tasksList);

            this.workspace.TryApplyChanges(this.solution);

            chrono.Stop();

            var ellapsedTime = TimeSpan.FromMilliseconds(chrono.ElapsedMilliseconds);

            Console.WriteLine("Completed in {0}", ellapsedTime);
        }

        #endregion

        #region Methods

        private static string BuildFactoryImplementationConstructorsCodeSection(string factoryImplementationTypeName,
                                                                                ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                                                                ICollection<IParameterSymbol> injectedParameters)
        {
            var factoryConstructorsStringBuilder = new StringBuilder();
            if (injectedParameters.Count > 0)
            {
                var allConstructorAttributes = concreteClassDeclarationSyntax.Members.OfType<ConstructorDeclarationSyntax>()
                                                                             .SelectMany(cs => cs.AttributeLists.SelectMany(al => al.Attributes))
                                                                             .ToArray();

                factoryConstructorsStringBuilder.AppendLine("        #region Constructors");
                factoryConstructorsStringBuilder.AppendLine();

                if (allConstructorAttributes.Any())
                {
                    factoryConstructorsStringBuilder.AppendLine(string.Format("        [{0}]", string.Join(", ", allConstructorAttributes.Select(cs => cs.ToString()))));
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
            }

            return factoryConstructorsStringBuilder.ToString();
        }

        private static string BuildFactoryImplementationFieldsCodeSection(IParameterSymbol[] injectedParameters)
        {
            var fieldsStringBuilder = new StringBuilder();
            if (injectedParameters.Length > 0)
            {
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
            }
            return fieldsStringBuilder.ToString();
        }

        private static string BuildFactoryImplementationMethodsCodeSection(INamedTypeSymbol concreteClassSymbol,
                                                                           INamedTypeSymbol factoryInterfaceSymbol,
                                                                           IParameterSymbol[] injectedParameters)
        {
            var factoryMethodsStringBuilder = new StringBuilder();
            var factoryMethods = factoryInterfaceSymbol.GetMembers()
                                                       .OfType<IMethodSymbol>()
                                                       .Where(methodSymbol => concreteClassSymbol.AllInterfaces.Contains(methodSymbol.ReturnType))
                                                       .ToArray();

            if (factoryMethods.Any())
            {
                factoryMethodsStringBuilder.AppendLine("        #region Public Factory Methods");
                factoryMethodsStringBuilder.AppendLine();

                foreach (var factoryMethod in factoryMethods)
                {
                    var factoryMethodParameters = factoryMethod.Parameters;
                    var factoryMethodParameterCount = factoryMethodParameters.Count();
                    var selectedConstructor = concreteClassSymbol.InstanceConstructors
                                                                 .Single(c => c.Parameters.Select(cp => cp.Name)
                                                                               .Intersect(factoryMethodParameters.Select(fmp => fmp.Name)).Count() == factoryMethodParameterCount);

                    var parameterListAsText = factoryMethodParameters.Select(p =>
                                                                             {
                                                                                 var attributes = p.GetAttributes();
                                                                                 var attributeSection = attributes.Any()
                                                                                                            ? string.Format("[{0}] ", string.Join(", ", attributes.Select(a => a.ToString())))
                                                                                                            : string.Empty;

                                                                                 return string.Format("{0}{1} {2}", attributeSection, p.Type, p.Name);
                                                                             });

#if WRITEXMLDOC
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
#endif
                    if (factoryMethodParameters.Length > 1)
                    {
                        factoryMethodsStringBuilder.AppendFormat(
                                                                 @"        public {0} Create(
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
                        factoryMethodsStringBuilder.AppendFormat(
                                                                 @"        public {0} Create({1})
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

        private static string GetDeclarationFullName(BaseTypeDeclarationSyntax typeDeclarationSyntax)
        {
            var fullyQualifiedName = string.Format("{0}.{1}", GetDeclarationNamespaceFullName(typeDeclarationSyntax), typeDeclarationSyntax.Identifier.ValueText);

            return fullyQualifiedName;
        }

        private static string GetDeclarationNamespaceFullName(BaseTypeDeclarationSyntax typeDeclarationSyntax)
        {
            var namespaceDeclarationSyntax = typeDeclarationSyntax.FirstAncestorOrSelf<NamespaceDeclarationSyntax>();
            return namespaceDeclarationSyntax.Name.ToString();
        }

        private static string GetFactoryClassGenericName(ClassDeclarationSyntax concreteTypeDeclarationSyntax)
        {
            var factoryClassName = string.Format("{0}Factory{1}", concreteTypeDeclarationSyntax.Identifier.ValueText, string.Empty /*GetGenericArguments(concreteTypeDeclarationSyntax)*/);

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

        private async Task GenerateFactoriesInProjectAsync(Compilation compilation)
        {
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var syntaxRootNode = await syntaxTree.GetRootAsync();
                var classDeclarations =
                    syntaxRootNode.DescendantNodesAndSelf(syntaxNode => !(syntaxNode is ClassDeclarationSyntax))
                                  .OfType<ClassDeclarationSyntax>()
                                  .Where(classDeclarationSyntax => classDeclarationSyntax.AttributeLists.Count > 0 && classDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes).Any(x => x.Name.ToString() == "GenerateT4Factory"))
                                  .ToArray();

                if (classDeclarations.Any())
                {
                    foreach (var classDeclarationSyntax in classDeclarations)
                    {
                        Console.WriteLine("[{0}]", string.Join(", ", classDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes).Select(x => x.Name)));
                        Console.WriteLine(classDeclarationSyntax.Identifier.Value);
                        Console.WriteLine();

                        var resolvedConcreteClassType = compilation.GetTypeByMetadataName(GetDeclarationFullName(classDeclarationSyntax));

                        this.GenerateFactoryForClass(classDeclarationSyntax, resolvedConcreteClassType);

                        Console.WriteLine(new string('-', 80));
                    }
                }
            }
        }

        private void GenerateFactoryForClass(ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                             INamedTypeSymbol concreteClassTypeSymbol)
        {
            var factoryAttribute = concreteClassTypeSymbol.GetAttributes().Single(a => a.AttributeClass.ToString() == "T4Factories.GenerateT4FactoryAttribute");

            INamedTypeSymbol factoryInterfaceTypeSymbol;
            if (factoryAttribute.ConstructorArguments != null && factoryAttribute.ConstructorArguments.Any())
            {
                var typeOfArgument = factoryAttribute.ConstructorArguments.Single();
                factoryInterfaceTypeSymbol = (INamedTypeSymbol)typeOfArgument.Value;
            }
            else
            {
                throw new InvalidOperationException("Factory type must be specified in GenerateT4FactoryAttribute.");
            }

            var factoryClassGenericName = GetFactoryClassGenericName(concreteClassDeclarationSyntax);
            var factoryInterfaceFullName = factoryInterfaceTypeSymbol.ToString();

            Console.WriteLine("Rendering factory implementation {0}\r\n\tfor class {1}\r\n\ttargeting {2}...", factoryClassGenericName, factoryInterfaceFullName, GetDeclarationFullName(concreteClassDeclarationSyntax));

            this.RenderFactoryImplementation(concreteClassDeclarationSyntax, concreteClassTypeSymbol, factoryInterfaceTypeSymbol, GetSafeFileName(factoryClassGenericName));
        }

        private void RenderFactoryImplementation(ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                                 INamedTypeSymbol concreteClassTypeSymbol,
                                                 INamedTypeSymbol factoryInterfaceTypeSymbol,
                                                 string factoryName)
        {
            var fileName = string.Format("{0}.Generated.cs", factoryName);

            var usingsToFilterOut = new[] { "T4Factories", concreteClassTypeSymbol.ContainingNamespace.ToString() };
            var outerUsingDeclarations = FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<CompilationUnitSyntax>().Usings, usingsToFilterOut);
            var innerUsingDeclarations = FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<NamespaceDeclarationSyntax>().Usings, usingsToFilterOut);

            var allConstructorParameters = concreteClassTypeSymbol.Constructors
                                                                  .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                                                                  .SelectMany(constructor => constructor.Parameters)
                                                                  .ToArray();
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

            var injectedParameters = (from parameter in (IEnumerable<IParameterSymbol>)allConstructorParameters
                                      where !allContractMethodParameters.Any(contractMethodParameter => CompareParameters(contractMethodParameter, parameter))
                                      select parameter).ToArray();

            var factoryFieldsCodeSection = BuildFactoryImplementationFieldsCodeSection(injectedParameters);
            var factoryConstructorsCodeSection = BuildFactoryImplementationConstructorsCodeSection(factoryName, concreteClassDeclarationSyntax, injectedParameters);
            var factoryMethodsCodeSection = BuildFactoryImplementationMethodsCodeSection(concreteClassTypeSymbol, factoryInterfaceTypeSymbol, injectedParameters);

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
                .Replace("<#=concreteXmlDocSafeTypeName#>", GetXmlDocSafeTypeName(GetDeclarationFullName(concreteClassDeclarationSyntax)))
                .Replace("<#=factoryFields#>", factoryFieldsCodeSection)
                .Replace("<#=factoryConstructors#>", factoryConstructorsCodeSection)
                .Replace("<#=factoryMethods#>", factoryMethodsCodeSection);

            var project = this.solution.Projects.First(proj => proj.GetDocument(concreteClassDeclarationSyntax.SyntaxTree) != null);
            var typeDeclarationDocument = project.GetDocument(concreteClassDeclarationSyntax.SyntaxTree);
            var existingDocument = project.Documents.FirstOrDefault(doc => doc.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));

            if (existingDocument != null)
            {
                this.UpdateDocument(existingDocument, SourceText.From(code));
            }
            else
            {
                var newDocument = project.AddDocument(fileName, code, typeDeclarationDocument.Folders);

                this.solution = newDocument.Project.Solution;
            }
        }

        private void UpdateDocument(Document document,
                                    SourceText newText)
        {
            var oldSolution = this.solution;
            var newSolution = oldSolution.WithDocumentText(document.Id, newText);
            this.solution = newSolution;
        }

        #endregion
    }
}