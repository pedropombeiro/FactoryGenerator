namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    using System;
    using System.Collections.Generic;

    using DeveloperInTheFlow.FactoryGenerator.Services;

    public class Attribute
    {
        #region Public Properties

        /// <summary>
        ///     Gets <see cref="Argument"/>s of the <see cref="Attribute"/>.
        /// </summary>
        public IEnumerable<Value> Arguments { get; set; }

        /// <summary>
        ///     Gets the name of the <see cref="Attribute"/>.
        /// </summary>
        public string Name { get; set; }

        #endregion

        #region Public Methods and Operators

        /// <summary>
        ///     Creates an <see cref="Attribute"/> with the <paramref name="name"/> and its <paramref name="arguments"/>.
        /// </summary>
        /// <param name="name">
        ///     The name of the <see cref="Attribute"/>.
        /// </param>
        /// <param name="arguments">
        ///     <see cref="Argument"/>s of the <see cref="Attribute"/>.
        /// </param>
        /// <returns>
        ///     An instance of the <see cref="Attribute"/> with the <paramref name="name"/> and its <paramref name="arguments"/>.
        /// </returns>
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