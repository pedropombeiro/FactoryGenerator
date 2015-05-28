namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System.Collections.Generic;
    using System.Linq;

    using DeveloperInTheFlow.FactoryGenerator.Models;

    using Microsoft.CodeAnalysis;

    public class GenericTypeBuilderService
    {
        #region Public Methods and Operators

        /// <summary>
        ///     Yields arguments for generic types.
        /// </summary>
        /// <param name="typeParameterSymbols">
        ///     The <see cref="Enumerable"/> of the generic type as parameters.
        /// </param>
        /// <returns>
        ///     An <see cref="Enumerable"/> of arguments representing generic type.
        /// </returns>
        public IEnumerable<Argument> Build(IEnumerable<ITypeParameterSymbol> typeParameterSymbols)
        {
            return typeParameterSymbols.Select(x => new Argument(x.Name));
        }

        #endregion
    }
}