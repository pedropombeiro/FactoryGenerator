namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using DeveloperInTheFlow.FactoryGenerator.Models;

    using Humanizer;

    using Microsoft.CodeAnalysis;

    /// <summary>
    ///     Service responsible for building method models.
    /// </summary>
    public class MethodsBuilderService
    {
        #region Fields

        private readonly ArgumentsBuilderService argumentsBuilderService;

        private readonly GenericTypeBuilderService genericTypeBuilderService;

        private readonly ParameterSymbolBuilderService parameterSymbolBuilderService;

        private readonly bool writeXmlDoc;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="MethodsBuilderService"/> class.
        /// </summary>
        public MethodsBuilderService(GenericTypeBuilderService genericTypeBuilderService,
                                     ArgumentsBuilderService argumentsBuilderService,
                                     ParameterSymbolBuilderService parameterSymbolBuilderService,
                                     bool writeXmlDoc)
        {
            this.genericTypeBuilderService = genericTypeBuilderService;
            this.argumentsBuilderService = argumentsBuilderService;
            this.parameterSymbolBuilderService = parameterSymbolBuilderService;
            this.writeXmlDoc = writeXmlDoc;
        }

        #endregion

        #region Public Methods and Operators

	    /// <summary>
	    ///     Builds models representing methods of the factories.
	    /// </summary>
	    /// <param name="concreteClassSymbol">
	    ///     Represents the concrete class.
	    /// </param>
	    /// <param name="fields">
	    ///     Fields available in the factory.
	    /// </param>
	    /// <param name="injectedParameters">
	    ///     The injected parameters in the factory.
	    /// </param>
	    /// <param name="factoryMethods">
	    ///     The factory methods retrieved with Roslyn.
	    /// </param>
	    /// <param name="factoryInterfaceName">
	    ///     The interface name of the factory.
	    /// </param>
	    /// <returns>
	    ///     Models representing the factory methods.
	    /// </returns>
		public IEnumerable<Method> Build(
			INamedTypeSymbol concreteClassSymbol,
			IEnumerable<Field> fields,
			IParameterSymbol[] injectedParameters,
			(ITypeSymbol returnType, IMethodSymbol symbol)[] factoryMethods,
			string factoryInterfaceName,
			INamedTypeSymbol factoryInterfaceTypeSymbol = null,
			ICollection<string> knownNamespaces = null)
		{
			if (!factoryMethods.Any())
			{
				return Enumerable.Empty<Method>();
			}

			fields = fields as Field[] ?? fields.ToArray();
			var methods = new List<Method>();

			// For open-generic forwarding factories (e.g. typeof(IFoo<,,>)), the factory class uses
			// the factory interface's type parameter names (e.g. TFunctionState), while the concrete
			// class may use different names for the same positional type parameters (e.g. TFunctionStates).
			// Build the constructor instantiation type using the interface's type parameter names so the
			// generated code compiles correctly inside the factory class.
			string newInstanceType;
			if (factoryInterfaceTypeSymbol != null && factoryInterfaceTypeSymbol.TypeParameters.Length > 0
				&& concreteClassSymbol.TypeParameters.Length == factoryInterfaceTypeSymbol.TypeParameters.Length)
			{
				var interfaceTypeParamNames = string.Join(", ", factoryInterfaceTypeSymbol.TypeParameters.Select(tp => tp.Name));
				newInstanceType = $"{concreteClassSymbol.ContainingNamespace}.{concreteClassSymbol.Name}<{interfaceTypeParamNames}>";
			}
			else
			{
				newInstanceType = concreteClassSymbol.ToString();
			}

			newInstanceType = FactoryGenerator.ShortenTypeName(newInstanceType, knownNamespaces);

// ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var factoryMethod in factoryMethods)
            {
                var constructor = GetConstructorFromFactoryMethod(factoryMethod.symbol, concreteClassSymbol);
                var factoryMethodParameters = factoryMethod.symbol.Parameters;

                var arguments = this.argumentsBuilderService.BuildMethodArgument(factoryMethodParameters, knownNamespaces)
                                    .ToArray();
                var constructorArguments = this.BuildConstructorParameters(constructor, arguments, fields, injectedParameters, factoryInterfaceName);
                var genericArguments = this.genericTypeBuilderService.Build(factoryMethod.symbol.TypeParameters);

                if (this.writeXmlDoc)
                {
                    var xmlComments = BuildXmlDoc(factoryMethod.symbol);
                    methods.Add(new Method("Create", FactoryGenerator.ShortenTypeName(factoryMethod.returnType.ToString(), knownNamespaces), newInstanceType, arguments, constructorArguments, genericArguments, xmlComments));
                }
                else
                {
                    methods.Add(new Method("Create", FactoryGenerator.ShortenTypeName(factoryMethod.returnType.ToString(), knownNamespaces), newInstanceType, arguments, constructorArguments, genericArguments, string.Empty));
                }
            }

            return methods;
        }

        #endregion

        #region Methods

        private static string BuildXmlDoc(ISymbol factoryMethod)
        {
            var documentationCommentXml = factoryMethod.GetDocumentationCommentXml();
            var relevantLines = documentationCommentXml.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                                       .SkipWhile(line => !line.StartsWith(" "))
                                                       .TakeWhile(line => line != "</member>")
                                                       .ToArray();
            var indent = relevantLines.First().Length - relevantLines.First().TrimStart().Length;
            var relevantLinesAsXmlDoc = relevantLines.Select(line => string.Format("        /// {0}", line.Substring(indent)));

            return string.Join("\r\n", relevantLinesAsXmlDoc);
        }

        private static string GetConstructorArgument(IParameterSymbol parameterSymbol,
                                                     IEnumerable<IParameterSymbol> injectedParameters,
                                                     string factoryInterfaceName)
        {
            return injectedParameters.Any(x => x.Name == parameterSymbol.Name)
                       ? string.Format("this.{0}", parameterSymbol.Name)
                       : parameterSymbol.Type.Name == factoryInterfaceName
                             ? "this"
                             : parameterSymbol.Name;
        }

        private static IMethodSymbol GetConstructorFromFactoryMethod(IMethodSymbol factoryMethodSymbol,
                                                                     INamedTypeSymbol concreteClassSymbol)
        {
            var factoryMethodParameters = factoryMethodSymbol.Parameters;
            var instanceConstructors = concreteClassSymbol.InstanceConstructors
                                                          .Where(c => c.DeclaredAccessibility == Accessibility.Public || c.DeclaredAccessibility == Accessibility.Internal)
                                                          .ToArray();

            try
            {
                var matchedInstanceConstructors = instanceConstructors.GroupBy(c => factoryMethodParameters.Select(fmp => fmp.Name)
                                                                                                           .Count(c.Parameters.Select(cp => cp.Name).Contains))
                                                                      .ToArray();
                var selectedGrouping = matchedInstanceConstructors.SingleOrDefault(g => g.Key == factoryMethodParameters.Length)
                                       ?? matchedInstanceConstructors.OrderBy(g => g.Key - factoryMethodParameters.Length).First();
                var selectedConstructor = selectedGrouping.First();

                return selectedConstructor;
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException(string.Format("Could not select a constructor from {0} type with the following parameters: ({1}).", concreteClassSymbol.Name, string.Join(", ", factoryMethodParameters.Select(x => x.Name))), e);
            }
        }

        private IEnumerable<Parameter> BuildConstructorParameters(IMethodSymbol constructor,
                                                                  IEnumerable<Argument> methodArguments,
                                                                  IEnumerable<Field> parameters,
                                                                  IEnumerable<IParameterSymbol> injectedParameters,
                                                                  string factoryInterfaceName)
        {
            var injectedParameterSymbols = injectedParameters as IParameterSymbol[] ?? injectedParameters.ToArray();

            var constructorParameters = constructor.Parameters
                                                   .Where(x => parameters.Any(f => f.Name == x.Name) || methodArguments.Any(a => a.Name == x.Name) || x.Type.Name == factoryInterfaceName)
                                                   .Select(x => new Parameter(GetConstructorArgument(x,
                                                                                                     injectedParameterSymbols,
                                                                                                     factoryInterfaceName),
                                                                              injectedParameterSymbols.Any(p => p.Name == x.Name),
                                                                              this.parameterSymbolBuilderService.DeterminesIfValueType(x.Type)));

            return constructorParameters;
        }

        #endregion
    }
}