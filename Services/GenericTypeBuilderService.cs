namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System.Collections.Immutable;
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
			INamedTypeSymbol[] genericArgumentTypeSymbols = null,
			ImmutableArray<ITypeParameterSymbol> concreteClassTypeParameters = default)
		{
			return typeParameterSymbols.Select((x, i) =>
										{
											// Use the concrete class's type parameter constraints at the same
											// position when the factory interface's type parameter has none,
											// but always emit the constraint using the factory interface's
											// type parameter name (x.Name) so the generated code is valid.
											string whereStatement;
											if (!concreteClassTypeParameters.IsDefaultOrEmpty
												&& i < concreteClassTypeParameters.Length
												&& this.GetWhereStatement(x) == null
												&& this.GetWhereStatement(concreteClassTypeParameters[i]) != null)
											{
												whereStatement = this.GetWhereStatement(concreteClassTypeParameters[i], x.Name);
											}
											else
											{
												whereStatement = this.GetWhereStatement(x);
											}

											return (i, new GenericType(x.Name, whereStatement));
										})
									   .Where(t => genericArgumentTypeSymbols == null
												   || t.Item1 >= genericArgumentTypeSymbols.Length
												   || genericArgumentTypeSymbols[t.Item1] == null)
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

        private string GetWhereStatement(ITypeParameterSymbol typeParameterSymbol, string nameOverride = null)
        {
            string result = "where " + (nameOverride ?? typeParameterSymbol.Name) + " : ";

             List<string> constraints = new List<string>();

            if (typeParameterSymbol.HasReferenceTypeConstraint)
            {
                constraints.Add("class");
            }

            if (typeParameterSymbol.HasValueTypeConstraint)
            {
                constraints.Add("struct");
            }

            constraints.AddRange(typeParameterSymbol.ConstraintTypes.Cast<INamedTypeSymbol>().Select(ct => ct.Name + this.BuildString(ct.TypeParameters)));

            if (constraints.Count == 0)
                return null;

            result += string.Join(", ", constraints);

            return result;
        }
    }
}