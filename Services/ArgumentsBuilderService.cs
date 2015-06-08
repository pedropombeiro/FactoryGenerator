namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System.Collections.Generic;
    using System.Linq;

    using DeveloperInTheFlow.FactoryGenerator.Models;

    using Microsoft.CodeAnalysis;

    /// <summary>
    ///     Service responsible for managing argument models.
    /// </summary>
    public class ArgumentsBuilderService
    {
        #region Fields

        private readonly ParameterSymbolBuilderService parameterSymbolBuilderService;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="ArgumentsBuilderService"/> class.
        /// </summary>
        public ArgumentsBuilderService(ParameterSymbolBuilderService parameterSymbolBuilderService)
        {
            this.parameterSymbolBuilderService = parameterSymbolBuilderService;
        }

        #endregion

        #region Public Methods and Operators

        /// <summary>
        ///     Builds <see cref="Argument"/> models for a factory constructor.
        /// </summary>
        /// <param name="parameterSymbols">
        ///   The parameter representing an argument from Roslyn.
        /// </param>
        /// <returns>
        ///     The argument models.
        /// </returns>
        public IEnumerable<Argument> BuildConstructorArgument(IEnumerable<IParameterSymbol> parameterSymbols)
        {
            var arguments = parameterSymbols.Select(x => this.BuildArgument(x, true));

            return this.SetLastArgument(arguments);
        }

        /// <summary>
        ///     Builds <see cref="Argument"/> models for a factory methods.
        /// </summary>
        /// <param name="parameterSymbols">
        ///     The parameter representing an argument from Roslyn.
        /// </param>
        /// <returns>
        ///     The argument models.
        /// </returns>
        public IEnumerable<Argument> BuildMethodArgument(IEnumerable<IParameterSymbol> parameterSymbols)
        {
            var arguments = parameterSymbols.Select(x => this.BuildArgument(x, false));

            return this.SetLastArgument(arguments);
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

        private Argument BuildArgument(IParameterSymbol parameterSymbol,
                                       bool isShort)
        {
            return new Argument(this.parameterSymbolBuilderService.BuildArgumentType(parameterSymbol, isShort),
                                parameterSymbol.Name,
                                this.parameterSymbolBuilderService.BuildAttributes(parameterSymbol),
                                this.parameterSymbolBuilderService.DeterminesIfValueType(parameterSymbol.Type));
        }

        #endregion
    }
}