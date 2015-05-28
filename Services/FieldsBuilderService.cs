namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System.Collections.Generic;
    using System.Linq;

    using DeveloperInTheFlow.FactoryGenerator.Models;

    using Microsoft.CodeAnalysis;

    public class FieldsBuilderService
    {
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
                       : parameterSymbols.Select(x => new Field(x.Name, x.Type.ToString()));
        }

        #endregion
    }
}