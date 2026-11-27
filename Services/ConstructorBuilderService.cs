namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System.Collections.Generic;
    using System.Linq;

    using DeveloperInTheFlow.FactoryGenerator.Models;

    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    /// <summary>
    ///     Service responsible for building the <see cref="Constructor"/> model.
    /// </summary>
    public class ConstructorBuilderService
    {
        #region Fields

        private readonly ArgumentsBuilderService argumentsBuilderService;

        private readonly IEnumerable<string> attributeImportList;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="ConstructorBuilderService"/> class.
        /// </summary>
        public ConstructorBuilderService(IEnumerable<string> attributeImportList,
                                         ArgumentsBuilderService argumentsBuilderService)
        {
            this.attributeImportList = attributeImportList;
            this.argumentsBuilderService = argumentsBuilderService;
        }

        #endregion

        #region Public Methods and Operators

        /// <summary>
        ///     Builds the model representing the constructor of the factory.
        /// </summary>
        /// <param name="concreteClassDeclarationSyntax">
        ///   The concrete class that will be builded with the factory.
        /// </param>
        /// <param name="injectedParameters">
        ///   The injected parameters in the factory.
        /// </param>
        /// <param name="knownNamespaces">
        ///   The collection of namespaces with using directives in the generated file.
        /// </param>
        /// <returns>
        ///     The model representing the constructor of the factory.
        /// </returns>
        public Constructor Build(ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                 IEnumerable<IParameterSymbol> injectedParameters,
                                 System.Collections.Generic.ICollection<string> knownNamespaces = null)
        {
            var parameterSymbols = injectedParameters as IParameterSymbol[] ?? injectedParameters.ToArray();
            if (!parameterSymbols.Any())
            {
                return null;
            }

            var allConstructorAttributes = concreteClassDeclarationSyntax.Members.OfType<ConstructorDeclarationSyntax>()
                                                                         .SelectMany(cs => cs.AttributeLists.SelectMany(al => al.Attributes))
                                                                         .ToArray();

            var importedConstructorAttributes = allConstructorAttributes.Where(attributeSyntax =>
                                                                               {
                                                                                   var attributeName = attributeSyntax.Name.ToString();
                                                                                   return this.attributeImportList.Any(attributeName.Contains);
                                                                               }).ToArray();

            var constructorAttributes = new List<Attribute>(importedConstructorAttributes.Select(x => Attribute.Create(x.ToString())));
            var constructorArguments = this.argumentsBuilderService.BuildConstructorArgument(parameterSymbols, knownNamespaces);

            return new Constructor(constructorArguments, constructorAttributes);
        }

        #endregion
    }
}