namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;

    using DeveloperInTheFlow.FactoryGenerator.Models;

    using Humanizer;

    using Microsoft.CodeAnalysis;

    public class MethodsBuilderService
    {
        #region Fields

        private readonly GenericTypeBuilderService genericTypeBuilderService;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="MethodsBuilderService"/> class.
        /// </summary>
        public MethodsBuilderService(GenericTypeBuilderService genericTypeBuilderService)
        {
            this.genericTypeBuilderService = genericTypeBuilderService;
        }

        #endregion

        #region Public Methods and Operators

        /// <summary>
        ///     Builds models representing methods of the factories.
        /// </summary>
        /// <param name="concreteClassSymbol">
        ///     Represents the concrete class.
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
        public IEnumerable<Method> Build(INamedTypeSymbol concreteClassSymbol,
                                         IParameterSymbol[] injectedParameters,
                                         IMethodSymbol[] factoryMethods,
                                         string factoryInterfaceName)
        {
            if (!factoryMethods.Any())
            {
                return Enumerable.Empty<Method>();
            }

            var methods = new List<Method>();

// ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var factoryMethod in factoryMethods)
            {
                var constructor = GetConstructorFromFactoryMethod(factoryMethod, concreteClassSymbol);
                var factoryMethodParameters = factoryMethod.Parameters;

                var arguments = factoryMethodParameters.Select(x => new Argument(BuildArgument(x)));
                var constructorArguments = constructor.Parameters.Select(x => new Argument(GetConstructorArgument(x, injectedParameters, factoryInterfaceName)));
                var genericArguments = this.genericTypeBuilderService.Build(factoryMethod.TypeParameters);

                methods.Add(new Method("Create", factoryMethod.ReturnType.ToString(), concreteClassSymbol.ToString(), arguments, constructorArguments, genericArguments));
            }

            return methods;
        }

        #endregion

        #region Methods

        private static string BuildArgument(IParameterSymbol parameterSymbol)
        {
            return string.Format("{0}{1} {2}", BuildAttributes(parameterSymbol.GetAttributes()), parameterSymbol.Type, parameterSymbol.Name);
        }

        private static string BuildAttributes(ImmutableArray<AttributeData> attributes)
        {
            return attributes.Any()
                       ? string.Format("[{0}] ", string.Join(",", attributes.Select(x => x.ToString())))
                       : string.Empty;
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
            if (!concreteClassSymbol.AllInterfaces.Select(i => i.OriginalDefinition).Contains(factoryMethodSymbol.ReturnType.OriginalDefinition))
            {
                var message = "The factory method does not return the correct type (i.e. a type inherited by {0}). Are you sure the attribute maps to the correct factory type? Currently it maps to {1}."
                    .FormatWith(concreteClassSymbol, factoryMethodSymbol.ContainingType);

                throw new InvalidOperationException(message);
            }

            var factoryMethodParameters = factoryMethodSymbol.Parameters;
            var instanceConstructors = concreteClassSymbol.InstanceConstructors
                                                          .Where(c => c.DeclaredAccessibility == Accessibility.Public)
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
                throw new InvalidOperationException("Could not select a constructor from {0} type with the following parameters: ({1}).".FormatWith(concreteClassSymbol.Name, string.Join(", ", factoryMethodParameters.Select(x => x.Name))), e);
            }
        }

        #endregion
    }
}