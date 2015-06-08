namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System.Collections.Generic;
    using System.Linq;

    using DeveloperInTheFlow.FactoryGenerator.Models;

    using Microsoft.CodeAnalysis;

    /// <summary>
    ///     Service responsible for building field models.
    /// </summary>
    public class FieldsBuilderService
    {
        #region Fields

        private readonly ParameterSymbolBuilderService parameterSymbolBuilderService;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="FieldsBuilderService"/> class.
        /// </summary>
        public FieldsBuilderService(ParameterSymbolBuilderService parameterSymbolBuilderService)
        {
            this.parameterSymbolBuilderService = parameterSymbolBuilderService;
        }

        #endregion

        #region Public Methods and Operators

        /// <summary>
        ///     Builds models representing fields of the factory.
        /// </summary>
        /// <param name="injectedParameters">
        ///     The injected parameters in the factory.
        /// </param>
        /// <returns>
        ///     Models representing fields in the factory.
        /// </returns>
        public IEnumerable<Field> Build(IEnumerable<IParameterSymbol> injectedParameters)
        {
            var parameterSymbols = injectedParameters as IParameterSymbol[] ?? injectedParameters.ToArray();
            return !parameterSymbols.Any()
                       ? Enumerable.Empty<Field>()
                       : parameterSymbols.Select(x => new Field(x.Name,
                                                                x.Type.ToString(),
                                                                this.parameterSymbolBuilderService.DeterminesIfValueType(x.Type)));
        }

        #endregion
    }
}