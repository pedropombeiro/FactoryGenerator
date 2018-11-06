﻿namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System.Collections.Generic;
    using System.Linq;

    using DeveloperInTheFlow.FactoryGenerator.Models;

    using Microsoft.CodeAnalysis;

    /// <summary>
    ///     Service responsible for building the generic type models.
    /// </summary>
    public class GenericTypeBuilderService
    {
        #region Public Methods and Operators

	    /// <summary>
	    ///     Yields arguments for generic types.
	    /// </summary>
	    /// <param name="typeParameterSymbols">
	    ///     The <see cref="Enumerable"/> of the generic type as parameters.
	    /// </param>
	    /// <param name="genericArgumentTypeSymbols"></param>
	    /// <returns>
	    ///     An <see cref="Enumerable"/> of arguments representing generic type.
	    /// </returns>
	    public IEnumerable<GenericType> Build(
		    IEnumerable<ITypeParameterSymbol> typeParameterSymbols,
		    INamedTypeSymbol[] genericArgumentTypeSymbols = null)
        {
            return typeParameterSymbols.Select((x, i) => (i, new GenericType(x.Name)))
                                       .Where(t => genericArgumentTypeSymbols?[t.Item1] == null)
                                       .Select(t => t.Item2);
        }

        /// <summary>
        ///     Builds a string representing generic types.
        /// </summary>
        /// <param name="typeParameterSymbols">
        ///     The <see cref="Enumerable"/> of the generic type as parameters.
        /// </param>
        /// <returns>
        ///     A string representing generic types.
        /// </returns>
        public string BuildString(IEnumerable<ITypeParameterSymbol> typeParameterSymbols)
        {
            var parameterSymbols = typeParameterSymbols as ITypeParameterSymbol[] ?? typeParameterSymbols.ToArray();
            return parameterSymbols.Any()
                       ? string.Format("<{0}>", string.Join(",", parameterSymbols.Select(x => x.Name)))
                       : string.Empty;
        }

        #endregion
    }
}