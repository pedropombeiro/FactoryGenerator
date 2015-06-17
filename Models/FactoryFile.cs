namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    using System;

    using DeveloperInTheFlow.FactoryGenerator.Services;

    public class FactoryFile
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="FactoryFile"/> class.
        /// </summary>
        protected FactoryFile()
        {
        }

        #endregion

        #region Public Properties

        public Class Class { get; private set; }

        public string InnerUsings { get; private set; }

        public string Namespace { get; private set; }

        public string OuterUsings { get; private set; }

        #endregion

        #region Public Methods and Operators

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