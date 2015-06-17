namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    using System;
    using System.Collections.Generic;

    using DeveloperInTheFlow.FactoryGenerator.Services;

    public class Attribute
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="Attribute"/> class.
        /// </summary>
        protected Attribute()
        {
        }

        #endregion

        #region Public Properties

        public IEnumerable<Value> Arguments { get; private set; }

        public string Name { get; private set; }

        #endregion

        #region Public Methods and Operators

        public static Attribute Create(string name,
                                       IEnumerable<Value> arguments)
        {
            var dynamicType = CreateDynamicType(name);

            var dynamicInstance = (Attribute)Activator.CreateInstance(dynamicType);

            dynamicInstance.Name = name;
            dynamicInstance.Arguments = arguments;

            return dynamicInstance;
        }

        public static Attribute Create(string name)
        {
            return Create(name, null);
        }

        #endregion

        #region Methods

        private static Type CreateDynamicType(string attributeName)
        {
            return DynamicTypeBuilderService.CreateDynamicType(string.Format("{0}Attribute", attributeName), typeof(Attribute), attributeName);
        }

        #endregion
    }
}