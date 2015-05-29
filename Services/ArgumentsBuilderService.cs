namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;

    using DeveloperInTheFlow.FactoryGenerator.Models;

    using Microsoft.CodeAnalysis;

    /// <summary>
    ///     Service responsible for managing argument models.
    /// </summary>
    public class ArgumentsBuilderService
    {
        #region Public Methods and Operators

        /// <summary>
        ///     Builds <see cref="Argument"/> models.
        /// </summary>
        /// <param name="parameterSymbols">
        ///     The parameter representing an argument from Roslyn.
        /// </param>
        /// <returns>
        ///     The argument models.
        /// </returns>
        public IEnumerable<Argument> Build(IEnumerable<IParameterSymbol> parameterSymbols)
        {
            var arguments = parameterSymbols.Select(BuildArgument);

            return this.SetLastArgument(arguments);
        }

        /// <summary>
        ///     Builds a single <see cref="Argument"/>.
        /// </summary>
        /// <param name="value">
        ///     The value of the argument.
        /// </param>
        /// <param name="parameterSymbol">
        ///     The parameter representing an argument from Roslyn.
        /// </param>
        /// <returns>
        ///     The argument model.
        /// </returns>
        public Argument BuildSingle(string value,
                                    IParameterSymbol parameterSymbol)
        {
            return new Argument(value, parameterSymbol.Name);
        }

        /// <summary>
        ///     Search and set the property <see cref="Argument.IsLast"/> to True.
        /// </summary>
        /// <param name="arguments">
        ///     The argument models.
        /// </param>
        /// <returns>
        ///     The argument model with the last element defined.
        /// </returns>
        public IEnumerable<Argument> SetLastArgument(IEnumerable<Argument> arguments)
        {
            var arrArguments = arguments as Argument[] ?? arguments.ToArray();
            var lastOrDefault = arrArguments.LastOrDefault();
            if (lastOrDefault != null)
            {
                lastOrDefault.IsLast = true;
            }

            return arrArguments;
        }

        #endregion

        #region Methods

        private static Argument BuildArgument(IParameterSymbol parameterSymbol)
        {
            return new Argument(BuildArgumentValue(parameterSymbol), parameterSymbol.Name);
        }

        private static string BuildArgumentValue(IParameterSymbol parameterSymbol)
        {
            if (parameterSymbol.DeclaringSyntaxReferences.Any())
            {
                return parameterSymbol.DeclaringSyntaxReferences[0].GetSyntax().ToString();
            }

            return string.Format("{0}{1} {2}", BuildAttributes(parameterSymbol.GetAttributes()),
                                 parameterSymbol.Type,
                                 parameterSymbol.Name);
        }

        private static string BuildAttributes(ImmutableArray<AttributeData> attributes)
        {
            return attributes.Any()
                       ? string.Format("[{0}] ", string.Join(",", attributes.Select(x => x.ToString())))
                       : string.Empty;
        }

        #endregion
    }
}