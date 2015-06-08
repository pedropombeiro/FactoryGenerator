namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System;
    using System.Linq;
    using System.Text.RegularExpressions;

    using Microsoft.CodeAnalysis;

    /// <summary>
    ///     Service responsible for building as string parameters <see cref="IParameterSymbol"/>.
    /// </summary>
    public class ParameterSymbolBuilderService
    {
        #region Static Fields

        private static readonly Regex ParseTypeRegex = new Regex(@"([^[\]]+) [^ ]+$");

        #endregion

        #region Public Methods and Operators

        /// <summary>
        ///     Builds the type as string of the <paramref name="parameterSymbol"/>.
        /// </summary>
        /// <param name="parameterSymbol">
        ///     The parameter type that you want as string.
        /// </param>
        /// <param name="isShort">
        ///     Expects to have a short type.
        /// </param>
        /// <returns>
        ///     The type of the <paramref name="parameterSymbol" /> as <see cref="string"/>.
        /// </returns>
        public string BuildArgumentType(IParameterSymbol parameterSymbol,
                                        bool isShort)
        {
            if (!isShort)
            {
                return parameterSymbol.Type.ToString();
            }

            var shortType = CleanFullType(parameterSymbol);
            return shortType;
        }

        /// <summary>
        ///     Builds attributes of the <paramref name="parameterSymbol"/>.
        /// </summary>
        /// <param name="parameterSymbol">
        ///     The parameter attributes that you want as string.
        /// </param>
        /// <returns>
        ///     Attributes of the <paramref name="parameterSymbol"/> as string.
        /// </returns>
        public string BuildAttributes(IParameterSymbol parameterSymbol)
        {
            var attributes = parameterSymbol.GetAttributes();

            return attributes.Any()
                       ? string.Format("[{0}]", string.Join(",", attributes.Select(x => x.ToString())))
                       : string.Empty;
        }

        public bool DeterminesIfValueType(ITypeSymbol typeSymbol)
        {
            var parameterType = typeSymbol.ToString();
            if (!typeSymbol.IsValueType &&
                (parameterType.Equals("system.string", StringComparison.OrdinalIgnoreCase) || parameterType.Equals("string", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return typeSymbol.IsValueType;
        }

        #endregion

        #region Methods

        /// <summary>
        ///     Cleans and returns only the type of the <paramref name="parameterSymbol"/> syntax as string.
        /// </summary>
        /// <param name="parameterSymbol">
        ///     The <see cref="IParameterSymbol"/> that you want only the type as string.
        /// </param>
        /// <returns>
        ///     The type of the <paramref name="parameterSymbol"/>.
        /// </returns>
        private static string CleanFullType(IParameterSymbol parameterSymbol)
        {
            var originalSyntax = parameterSymbol.DeclaringSyntaxReferences[0].GetSyntax().ToString();

            var resultRegex = ParseTypeRegex.Match(originalSyntax);

            if (!resultRegex.Success)
            {
                throw new InvalidOperationException(string.Format("Cannot parse the parameter {0} to identify the type.", originalSyntax));
            }

            var shortType = resultRegex.Groups[1].Value;

            return shortType;
        }

        #endregion
    }
}