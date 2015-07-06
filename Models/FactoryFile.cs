namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    using System;

    using DeveloperInTheFlow.FactoryGenerator.Services;

    /// <summary>
    ///     Model representing the factory file.
    /// </summary>
    public class FactoryFile
    {
        #region Public Properties

        /// <summary>
        ///     Gets the <see cref="Class"/> available in the <see cref="FactoryFile"/>.
        /// </summary>
        public Class Class { get; set; }

        /// <summary>
        ///     Gets the template name used for generating the factory.
        /// </summary>
        public string FactoryFor { get; set; }

        /// <summary>
        ///     The inner usings of the factory.
        /// </summary>
        public string InnerUsings { get; set; }

        /// <summary>
        ///     Gets the namespace of the factory.
        /// </summary>
        public string Namespace { get; set; }

        /// <summary>
        ///     Gets the outer usings of the factory.
        /// </summary>
        public string OuterUsings { get; set; }

        #endregion

        #region Public Methods and Operators

        /// <summary>
        ///     Creates a <see cref="FactoryFile"/>.
        /// </summary>
        /// <param name="ns"></param>
        /// <param name="class"></param>
        /// <param name="innerUsings"></param>
        /// <param name="outerUsings"></param>
        /// <returns></returns>
        public static FactoryFile Create(string ns,
                                         Class @class,
                                         string innerUsings,
                                         string outerUsings)
        {
            var dynamicType = CreateDynamicType(@class);

            var factoryFile = (FactoryFile)Activator.CreateInstance(dynamicType);

            factoryFile.Class = @class;
            factoryFile.Namespace = ns;
            factoryFile.InnerUsings = innerUsings;
            factoryFile.OuterUsings = outerUsings;
            factoryFile.FactoryFor = @class.Inherit;

            return factoryFile;
        }

        #endregion

        #region Methods

        private static Type CreateDynamicType(Class @class)
        {
            var factoryInterface = @class.Inherit.Replace('.', '_');

            return DynamicTypeBuilderService.CreateDynamicType(string.Format("{0}FactoryFile", factoryInterface), typeof(FactoryFile), factoryInterface);
        }

        #endregion
    }
}