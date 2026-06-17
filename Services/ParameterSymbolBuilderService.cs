namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System;
    using System.Linq;

    using Microsoft.CodeAnalysis;

    /// <summary>
    ///     Service responsible for building as string parameters <see cref="IParameterSymbol"/>.
    /// </summary>
    public class ParameterSymbolBuilderService
    {
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
                                        bool isShort,
                                        System.Collections.Generic.ICollection<string> knownNamespaces = null)
        {
            if (!isShort)
            {
                return parameterSymbol.Type.ToString();
            }

            // Prefer reading the type name from the syntax (preserves explicit namespace
            // qualifications like SystemInterface.IVersion) ONLY when the syntax accurately
            // represents the current type. If the parameter is from a generic base interface
            // that has been constructed/substituted (e.g., IFoo<TEvent> -> IFoo<ActionLog>),
            // the syntax still shows the original type parameter name (TEvent), so we must
            // use the symbol's substituted type instead. Similarly, for generic types like
            // Option<T> or Func<T>, the syntax from a different file may have different using
            // directives, so prefer the symbol → ShortenTypeName path for generics.
            if (parameterSymbol.DeclaringSyntaxReferences.Length > 0)
            {
                var type = parameterSymbol.Type;
                // Check if this is a generic type (including delegate types like Func<>)
                var isGeneric = type is INamedTypeSymbol { TypeArguments.Length: > 0 } or IArrayTypeSymbol;
                if (!isGeneric)
                {
                    // For non-generic types, verify the syntax type name matches the symbol type name
                    var syntaxTypeName = CleanFullType(parameterSymbol);
                    var symbolTypeName = type.Name;
                    // Simple heuristic: if syntax ends with the symbol name, they likely match
                    if (syntaxTypeName.EndsWith(symbolTypeName, StringComparison.Ordinal))
                        return syntaxTypeName;
                }
            }

            // For all generic types, metadata-only types, or mismatched names, use the symbol
            // and apply namespace shortening based on the generated file's using directives
            var fullTypeName = parameterSymbol.Type.ToString();
            var shortened = FactoryGenerator.ShortenTypeName(fullTypeName, knownNamespaces);
            return shortened;
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
            if (!attributes.Any())
                return string.Empty;

            // Use the short class name (strip namespace and trailing 'Attribute') to match
            // what the developer wrote in source, e.g. [CanBeNull] not [JetBrains.Annotations.CanBeNullAttribute].
            var shortNames = attributes.Select(a =>
            {
                var name = a.AttributeClass?.Name ?? a.ToString();
                // Strip trailing 'Attribute' suffix (C# convention)
                if (name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length)
                    name = name.Substring(0, name.Length - "Attribute".Length);
                return name;
            });
            return $"[{string.Join(",", shortNames)}]";
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
            // The parameter syntax has children: [AttributeList*] TypeSyntax IdentifierName [EqualsValueClause]
            // We must skip attribute lists and return only the type node.
            var syntax = parameterSymbol.DeclaringSyntaxReferences[0].GetSyntax();
            var typeNode = syntax.ChildNodes()
                                 .FirstOrDefault(n => !(n is Microsoft.CodeAnalysis.CSharp.Syntax.AttributeListSyntax));
            return typeNode?.ToString() ?? parameterSymbol.Type.ToString();
        }

        #endregion
    }
}