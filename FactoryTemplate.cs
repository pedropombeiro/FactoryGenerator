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
    using Microsoft.CodeAnalysis.Simplification;
    using Microsoft.CodeAnalysis.Text;

    public class FactoryTemplate
    {
        #region Fields

        private readonly List<KeyValuePair<ClassDeclarationSyntax, TypeDeclarationSyntaxSource>> allClasses = new List<KeyValuePair<ClassDeclarationSyntax, TypeDeclarationSyntaxSource>>(128);

        private readonly List<InterfaceDeclarationSyntax> allInterfaces = new List<InterfaceDeclarationSyntax>(128);

        private readonly Workspace workspace;

        private Solution solution;

        #endregion

        #region Constructors and Destructors

        [DebuggerStepThrough]
        public FactoryTemplate(Workspace workspace,
                               Solution solution)
        {
            this.workspace = workspace;
            this.solution = solution;
        }

        #endregion

        #region Public Methods and Operators

        public async Task ExecuteAsync()
        {
            var projectDependencyGraph = await this.solution.GetProjectDependencyGraphAsync();

            foreach (var projectId in projectDependencyGraph.GetTopologicallySortedProjects())
            {
                var project = this.solution.GetProject(projectId);
                var compilation = await project.GetCompilationAsync();

                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    var syntaxRootNode = syntaxTree.GetRoot();
                    var classDeclarations =
                        syntaxRootNode.DescendantNodesAndSelf(syntaxNode => !(syntaxNode is ClassDeclarationSyntax))
                                      .OfType<ClassDeclarationSyntax>()
                                      .Where(classDeclarationSyntax => classDeclarationSyntax.AttributeLists.Count > 0 && classDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes).Any(x => x.Name.ToString() == "GenerateT4Factory"))
                                      .ToArray();
                    var interfaceDeclarations =
                        syntaxRootNode.DescendantNodesAndSelf(syntaxNode => !(syntaxNode is InterfaceDeclarationSyntax))
                                      .OfType<InterfaceDeclarationSyntax>()
                                      .ToArray();
                    var typeDeclarationSyntaxSource = new TypeDeclarationSyntaxSource { Compilation = compilation, SemanticModel = compilation.GetSemanticModel(syntaxTree) };

                    if (classDeclarations.Any())
                    {
                        foreach (var classDeclarationSyntax in classDeclarations)
                        {
                            Console.WriteLine("[{0}]", string.Join(", ", classDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes).Select(x => x.Name)));
                            Console.WriteLine(classDeclarationSyntax.Identifier.Value);
                            Console.WriteLine();

                            this.allClasses.Add(new KeyValuePair<ClassDeclarationSyntax, TypeDeclarationSyntaxSource>(classDeclarationSyntax, typeDeclarationSyntaxSource));
                        }
                        //Console.WriteLine("Attributes: {0}", string.Join(", ", attributes.Select(a => a.Name)));
                    }

                    //GenerateFactoriesForAttributedClasses(classDeclarations, interfaceDeclarations, semanticModel);

                    this.allInterfaces.AddRange(interfaceDeclarations);
                }
            }

            foreach (var keyValuePair in this.allClasses)
            {
                this.GenerateFactoryForClass(keyValuePair.Key, keyValuePair.Value);

                Console.WriteLine(new string('-', 80));
            }

            /*foreach (var concreteTypeDeclarationSyntax in allClasses)
            {
                var constructors = concreteTypeDeclarationSyntax.DescendantNodes().OfType<ConstructorDeclarationSyntax>().Where(cds => cds.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)));
                var factoryContractTypeName = GetFactoryInterfaceGenericName(concreteTypeDeclarationSyntax);
                string factoryContractTypeFullName = GetFactoryInterfaceFullName(this.contractType, concreteTypeDeclarationSyntax);
            }*/

            this.workspace.TryApplyChanges(this.solution);
        }

        #endregion

        #region Methods

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

        private static string GetFactoryInterfaceGenericName(ClassDeclarationSyntax concreteType)
        {
            var factoryInterfaceName = string.Format("I{0}Factory{1}", concreteType.Identifier.Value, /*GetGenericArguments(concreteType)*/string.Empty);

            return factoryInterfaceName;
        }

        private static string GetFactoryInterfaceName(ClassDeclarationSyntax concreteClassDeclarationSyntax)
        {
            var factoryInterfaceName = string.Format("I{0}Factory", concreteClassDeclarationSyntax.Identifier.ValueText);

            return factoryInterfaceName;
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

        private SyntaxList<UsingDirectiveSyntax> FilterOutUsings(SyntaxList<UsingDirectiveSyntax> usings,
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

        private IEnumerable<InterfaceDeclarationSyntax> FindNonAutoGeneratedInterfaceDeclarationSyntaxByFullName(string interfaceFullName)
        {
            return from interfaceDeclarationSyntax in this.allInterfaces
                   where GetDeclarationFullName(interfaceDeclarationSyntax) == interfaceFullName
                   let attributes = interfaceDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes)
                   where attributes.All(a => a.Name.ToString() != "global::System.CodeDom.Compiler.GeneratedCodeAttribute")
                   select interfaceDeclarationSyntax;
        }

        private IEnumerable<InterfaceDeclarationSyntax> FindNonAutoGeneratedInterfaceDeclarationSyntaxByNameUnderNamespace(string interfaceFullName,
                                                                                                                           string topLevelNamespace)
        {
            return from interfaceDeclarationSyntax in this.allInterfaces
                   where interfaceDeclarationSyntax.Identifier.ValueText == interfaceFullName
                   let attributes = interfaceDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes)
                   where attributes.All(a => a.Name.ToString() != "global::System.CodeDom.Compiler.GeneratedCodeAttribute")
                   where GetDeclarationNamespaceFullName(interfaceDeclarationSyntax).StartsWith(topLevelNamespace)
                   select interfaceDeclarationSyntax;
        }

        private void GenerateFactoryForClass(ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                             TypeDeclarationSyntaxSource concreteClassDeclarationSyntaxSource)
        {
            Func<AttributeSyntax, bool> predicate = (a => a.Name.ToFullString() == "GenerateT4Factory");

            var attributes = concreteClassDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes);
            var codeAttribute = attributes.Single(predicate);
            var expandedCodeAttribute = Simplifier.Expand(codeAttribute, concreteClassDeclarationSyntaxSource.SemanticModel, this.workspace);

            InterfaceDeclarationSyntax contractInterfaceDeclaration;
            if (expandedCodeAttribute.ArgumentList != null && expandedCodeAttribute.ArgumentList.Arguments.Any())
            {
                var typeOfArgument =
                    (TypeOfExpressionSyntax)expandedCodeAttribute.ArgumentList.Arguments.Single().Expression;
                contractInterfaceDeclaration = this.GetInterfaceTypeDeclaration(typeOfArgument.Type);
            }
            else
            {
                var contractInterfaceName = string.Format("I{0}", concreteClassDeclarationSyntax.Identifier.ValueText);
                contractInterfaceDeclaration = this.allInterfaces.Single(interfaceDeclarationSyntax => interfaceDeclarationSyntax.Identifier.ValueText == contractInterfaceName);
            }

            var contractInterfaceFullName = GetDeclarationFullName(contractInterfaceDeclaration);
            InterfaceDeclarationSyntax contractInterfaceDeclarationSyntax;

            try
            {
                contractInterfaceDeclarationSyntax = this.allInterfaces.SingleOrDefault(x => x.Arity == contractInterfaceDeclaration.Arity && GetDeclarationFullName(x) == contractInterfaceFullName) ??
                                                     this.allInterfaces.Single(x => x.Identifier.ValueText.EndsWith(contractInterfaceDeclaration.Identifier.ValueText));
            }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException(string.Format("{0} interface not found in current project", contractInterfaceDeclaration));
            }

            var factoryClassGenericName = GetFactoryClassGenericName(concreteClassDeclarationSyntax);

            Console.WriteLine("Rendering factory implementation {0}\r\n\tfor interface {1}\r\n\ttargeting {2}...", factoryClassGenericName, contractInterfaceFullName, GetDeclarationFullName(concreteClassDeclarationSyntax));

            this.RenderFactoryImplementation(concreteClassDeclarationSyntax, contractInterfaceDeclaration, GetSafeFileName(factoryClassGenericName));

            Console.WriteLine("Looking for factory interface for type {0}...", contractInterfaceDeclarationSyntax.Identifier);

            var factoryInterfaceFullName = this.GetFactoryInterfaceFullName(contractInterfaceDeclarationSyntax, concreteClassDeclarationSyntax);

            Console.WriteLine("Factory interface is {0}", factoryInterfaceFullName);

            if (!this.UserGeneratedInterfaceExists(factoryInterfaceFullName) &&
                !this.UserGeneratedInterfaceExists(string.Format("{0}Factory", contractInterfaceDeclarationSyntax.Identifier.ValueText)))
            {
                var factoryInterfaceGenericName = GetFactoryInterfaceGenericName(concreteClassDeclarationSyntax);

                Console.WriteLine("Factory interface not found. Rendering factory interface {0}...", factoryInterfaceGenericName);

                this.RenderFactoryInterface(concreteClassDeclarationSyntax, contractInterfaceDeclarationSyntax, GetSafeFileName(factoryInterfaceGenericName));
            }
        }

        private string GetFactoryInterfaceFullName(InterfaceDeclarationSyntax contractInterfaceDeclarationSyntax,
                                                   ClassDeclarationSyntax concreteClassDeclarationSyntax)
        {
            var typeParameters = concreteClassDeclarationSyntax.TypeParameterList == null
                                     ? string.Empty
                                     : string.Join(", ",
                                                   concreteClassDeclarationSyntax.TypeParameterList.Parameters.Select(tps => tps.Identifier.Text));

            var compilation = this.allClasses.Single(kvp => kvp.Key == concreteClassDeclarationSyntax).Value.Compilation; // TODO: Improve data structure
            var resolvedType = compilation.GetTypeByMetadataName(GetDeclarationFullName(concreteClassDeclarationSyntax));
            var contractInterfaceFullName = GetDeclarationFullName(contractInterfaceDeclarationSyntax);
            var implementedInterfacesDerivedFromContract = resolvedType.AllInterfaces
                                                                       .Where(interfaceNamedSymbol => interfaceNamedSymbol.ToString() == contractInterfaceFullName || interfaceNamedSymbol.Interfaces.Any(bins => bins.ToString() == contractInterfaceFullName))
                                                                       .ToArray();

            string factoryInterfaceFullName = null;

            if (implementedInterfacesDerivedFromContract.Any())
            {
                var possibleFactoryFullNames = implementedInterfacesDerivedFromContract.Select(interfaceDerivedFromContract => string.Format("{0}.{1}Factory{2}", interfaceDerivedFromContract.ContainingNamespace, interfaceDerivedFromContract.Name, typeParameters))
                                                                                       .ToList();

                factoryInterfaceFullName = possibleFactoryFullNames.FirstOrDefault(this.UserGeneratedInterfaceExists);
            }

            if (factoryInterfaceFullName == null)
            {
                // Is there any interface with the correct factory name in or below the concrete type namespace?
                var factoryInterfaceName = GetFactoryInterfaceGenericName(concreteClassDeclarationSyntax);

                Console.WriteLine("    GetFactoryInterfaceFullName fallback in concreteType namespace with factoryInterfaceName {0}", factoryInterfaceName);

                var concreteClassDeclarationNamespaceFullName = GetDeclarationNamespaceFullName(concreteClassDeclarationSyntax);
                var fallbackFactoryInterfaceFullName = this.FindNonAutoGeneratedInterfaceDeclarationSyntaxByNameUnderNamespace(factoryInterfaceName, concreteClassDeclarationNamespaceFullName)
                                                           .Select(GetDeclarationFullName)
                                                           .FirstOrDefault();
                Console.WriteLine(fallbackFactoryInterfaceFullName == null
                                      ? "    1st fallback not found: {0}.*.{1}"
                                      : "    1st fallback found: {2}",
                                  concreteClassDeclarationNamespaceFullName,
                                  factoryInterfaceName,
                                  fallbackFactoryInterfaceFullName);

                if (fallbackFactoryInterfaceFullName != null)
                {
                    factoryInterfaceFullName = fallbackFactoryInterfaceFullName;
                }
                else
                {
                    var factoryInterfaceName2 = GetFactoryInterfaceName(concreteClassDeclarationSyntax);

                    if (factoryInterfaceName2 != factoryInterfaceName)
                    {
                        fallbackFactoryInterfaceFullName = this.FindNonAutoGeneratedInterfaceDeclarationSyntaxByNameUnderNamespace(factoryInterfaceName2, concreteClassDeclarationNamespaceFullName)
                                                               .Select(GetDeclarationFullName)
                                                               .FirstOrDefault();

                        Console.WriteLine(fallbackFactoryInterfaceFullName == null
                                              ? "    2nd fallback not found: {0}.*.{1}"
                                              : "    2nd fallback found: {2}",
                                          concreteClassDeclarationNamespaceFullName,
                                          factoryInterfaceName2,
                                          fallbackFactoryInterfaceFullName);

                        if (fallbackFactoryInterfaceFullName != null)
                        {
                            factoryInterfaceFullName = fallbackFactoryInterfaceFullName;
                        }
                    }
                }
            }

            return factoryInterfaceFullName;
        }

        private InterfaceDeclarationSyntax GetInterfaceTypeDeclaration(TypeSyntax type)
        {
            var typeFullName = type.ToString();

            return this.allInterfaces.Single(itd => string.Format("global::{0}", GetDeclarationFullName(itd)) == typeFullName);
        }

        private void RenderFactoryImplementation(ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                                 InterfaceDeclarationSyntax contractInterfaceDeclarationSyntax,
                                                 string factoryName)
        {
            var fileName = string.Format("{0}.Generated.cs", factoryName);

            var usingsToFilterOut = new[] { "T4Factories", GetDeclarationNamespaceFullName(concreteClassDeclarationSyntax) };
            var outerUsingDeclarations = this.FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<CompilationUnitSyntax>().Usings, usingsToFilterOut);
            var innerUsingDeclarations = this.FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<NamespaceDeclarationSyntax>().Usings, usingsToFilterOut);

            var constructorsStringBuilder = new StringBuilder();
            var constructors = concreteClassDeclarationSyntax.Members.OfType<ConstructorDeclarationSyntax>().ToArray();
            foreach (var constructor in constructors)
            {
                /*var allParameters = constructor.Parameters.Cast<EnvDTE80.CodeParameter2>();
                var parameters = from parameter in allParameters
                                 where !IsParameterInjected(parameter)
                                 select parameter;

                WriteLine(string.Empty);
                Write(this.RenderXmlDoc(constructor.DocComment));#>*/
                constructorsStringBuilder.AppendFormat("{0} Create{1};", GetDeclarationFullName(contractInterfaceDeclarationSyntax), constructor.ParameterList);
            }

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
                .Replace("<#=factoryContractTypeFullName#>", this.GetFactoryInterfaceFullName(contractInterfaceDeclarationSyntax, concreteClassDeclarationSyntax))
                .Replace("<#=GeneratedCodeAttribute#>", "global::System.CodeDom.Compiler.GeneratedCode(\"AutoGenFactories\", \"0.1\")")
                .Replace("<#=concreteXmlDocSafeTypeName#>", GetXmlDocSafeTypeName(GetDeclarationFullName(concreteClassDeclarationSyntax)))
                .Replace("<#=factoryMethods#>", constructorsStringBuilder.ToString());

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

        private void RenderFactoryInterface(ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                            InterfaceDeclarationSyntax contractInterfaceDeclarationSyntax,
                                            string factoryName)
        {
            var fileName = string.Format("{0}.Generated.cs", factoryName);

            var usingsToFilterOut = new[] { "T4Factories", GetDeclarationNamespaceFullName(concreteClassDeclarationSyntax) };
            var outerUsingDeclarations = this.FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<CompilationUnitSyntax>().Usings, usingsToFilterOut);
            var innerUsingDeclarations = this.FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<NamespaceDeclarationSyntax>().Usings, usingsToFilterOut);

            var constructorsStringBuilder = new StringBuilder();
            var constructors = concreteClassDeclarationSyntax.Members.OfType<ConstructorDeclarationSyntax>().ToArray();
            foreach (var constructor in constructors)
            {
                /*var allParameters = constructor.Parameters.Cast<EnvDTE80.CodeParameter2>();
                var parameters = from parameter in allParameters
                                 where !IsParameterInjected(parameter)
                                 select parameter;

                WriteLine(string.Empty);
                Write(this.RenderXmlDoc(constructor.DocComment));#>*/
                constructorsStringBuilder.AppendFormat("{0} Create{1};", GetDeclarationFullName(contractInterfaceDeclarationSyntax), constructor.ParameterList);
            }

            var code = @"﻿#pragma warning disable 1591

<#=outerUsings#>namespace <#=namespaceFullName#>
{<#=innerUsings#>
    /// <summary>
    /// The contract for the factory generating <see cref=""<#=contractXmlDocSafeTypeName#>"" /> instances.
    /// </summary>
    [<#=GeneratedCodeAttribute#>]
    public partial interface <#=factoryContractTypeName#>
    {
        #region Factory Methods

        <#=factoryMethods#>

        #endregion
    }
}"
                .Replace("<#=namespaceFullName#>", GetDeclarationNamespaceFullName(concreteClassDeclarationSyntax))
                .Replace("<#=outerUsings#>", outerUsingDeclarations.Any()
                                                 ? string.Format("{0}\r\n", innerUsingDeclarations.ToFullString())
                                                 : string.Empty)
                .Replace("<#=innerUsings#>", innerUsingDeclarations.Any()
                                                 ? string.Format("\r\n{0}", innerUsingDeclarations.ToFullString())
                                                 : string.Empty)
                .Replace("<#=factoryContractTypeName#>", factoryName)
                .Replace("<#=GeneratedCodeAttribute#>", "global::System.CodeDom.Compiler.GeneratedCode(\"AutoGenFactories\", \"0.1\")")
                .Replace("<#=contractXmlDocSafeTypeName#>", GetXmlDocSafeTypeName(GetDeclarationFullName(concreteClassDeclarationSyntax)))
                .Replace("<#=factoryMethods#>", constructorsStringBuilder.ToString());

            var project = this.solution.Projects.First(proj => proj.GetDocument(concreteClassDeclarationSyntax.SyntaxTree) != null);
            var typeDeclarationDocument = project.GetDocument(concreteClassDeclarationSyntax.SyntaxTree);
            var existingDocument =
                project.Documents.FirstOrDefault(doc => doc.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));

            if (existingDocument != null)
            {
                project = project.RemoveDocument(existingDocument.Id);
            }

            var newDocument = project.AddDocument(fileName, code, typeDeclarationDocument.Folders);

            this.solution = newDocument.Project.Solution;
        }

        private void UpdateDocument(Document document,
                                    SourceText newText)
        {
            var oldSolution = this.solution;
            var newSolution = oldSolution.WithDocumentText(document.Id, newText);
            this.solution = newSolution;
        }

        private bool UserGeneratedInterfaceExists(string interfaceFullName)
        {
            Console.Write("    Searching for {0} type... ", interfaceFullName);

            var userGeneratedInterfaceExists = this.FindNonAutoGeneratedInterfaceDeclarationSyntaxByFullName(interfaceFullName).Any();

            Console.WriteLine(userGeneratedInterfaceExists
                                  ? "found."
                                  : "not found.");

            return userGeneratedInterfaceExists;
        }

        #endregion

        private class TypeDeclarationSyntaxSource
        {
            #region Public Properties

            public Compilation Compilation { get; set; }

            public SemanticModel SemanticModel { get; set; }

            #endregion
        }
    }
}