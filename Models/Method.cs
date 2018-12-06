namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    ///     Model representing a factory method.
    /// </summary>
    public class Method
    {
        #region Fields

        private readonly IEnumerable<GenericType> genericTypes;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="Method"/> class.
        /// </summary>
        public Method(
            string name,
            string returnType,
            string newInstanceType,
            IEnumerable<Argument> arguments,
            IEnumerable<Parameter> implementationParameters,
            IEnumerable<GenericType> genericTypes,
            string xmlDoc)
        {
            this.Name = name;
            this.ReturnType = returnType;
            this.NewInstanceType = newInstanceType;
            this.XmlDoc = xmlDoc;
            this.Arguments = arguments.ToArray();
            this.ImplementationParameters = implementationParameters.ToArray();
            this.genericTypes = genericTypes;

            var lastParameter = this.ImplementationParameters.LastOrDefault();
            if (lastParameter != null)
            {
                lastParameter.IsLast = true;
            }
        }

        #endregion

        #region Public Properties

        /// <summary>
        ///     Arguments available for building a factory methods.
        /// </summary>
        public IEnumerable<Argument> Arguments { get; set; }

        /// <summary>
        ///     The generic type constraints of the method.
        /// </summary>
        public string[] GenericTypeConstraints
        {
            get
            {
                return this.genericTypes.Select(x => x.Constraints).Where(c => c != null).ToArray();
            }
        }

        /// <summary>
        ///     The generic type of the method.
        /// </summary>
        public string GenericTypes
        {
            get
            {
                return string.Join(",", this.genericTypes.Select(x => x.Name));
            }
        }

        /// <summary>
        ///     The <see cref="Parameter"/> passed to the method to build the object.
        /// </summary>
        public IEnumerable<Parameter> ImplementationParameters { get; set; }

        /// <summary>
        ///     The name of the method.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        ///     The type of the object that will be builded.
        /// </summary>
        public string NewInstanceType { get; set; }

        /// <summary>
        ///     The return type of the method.
        /// </summary>
        public string ReturnType { get; set; }

        /// <summary>
        ///     The XML documentation of the method.
        /// </summary>
        public string XmlDoc { get; set; }

        #endregion
    }
}