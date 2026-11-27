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
												var concreteParam = !concreteClassTypeParameters.IsDefaultOrEmpty && i < concreteClassTypeParameters.Length
													? concreteClassTypeParameters[i]
													: null;
												string whereStatement;
												if (concreteParam != null
													&& this.GetWhereStatement(x) == null
													&& this.GetWhereStatement(concreteParam) != null)
												{
													whereStatement = this.GetWhereStatement(concreteParam, x.Name);
												}
												else
												{
													whereStatement = this.GetWhereStatement(x, additionalConstraintsSource: concreteParam);
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

        private string GetWhereStatement(ITypeParameterSymbol typeParameterSymbol, string nameOverride = null, ITypeParameterSymbol additionalConstraintsSource = null)
        {
            string result = "where " + (nameOverride ?? typeParameterSymbol.Name) + " : ";

             List<string> constraints = new List<string>();

            if (typeParameterSymbol.HasReferenceTypeConstraint || (additionalConstraintsSource?.HasReferenceTypeConstraint ?? false))
            {
                constraints.Add("class");
            }

            if (typeParameterSymbol.HasValueTypeConstraint || (additionalConstraintsSource?.HasValueTypeConstraint ?? false))
            {
                constraints.Add("struct");
            }

            constraints.AddRange(typeParameterSymbol.ConstraintTypes.Cast<INamedTypeSymbol>().Select(ct => ct.Name + this.BuildString(ct.TypeParameters)));

            // Also merge interface constraints from the concrete class's type parameter that are
            // not already present (e.g. IEcuGroupFunctionStep when the factory interface only
            // declares IComparable for the same type parameter).
            if (additionalConstraintsSource != null)
            {
                var existingNames = new System.Collections.Generic.HashSet<string>(constraints);
                foreach (var ct in additionalConstraintsSource.ConstraintTypes.Cast<INamedTypeSymbol>())
                {
                    var entry = ct.Name + this.BuildString(ct.TypeParameters);
                    if (existingNames.Add(entry))
                        constraints.Add(entry);
                }
            }

            if (constraints.Count == 0)
                return null;

            result += string.Join(", ", constraints);

            return result;
        }
    }
}