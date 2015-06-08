namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    using System.Collections.Generic;

    public class Attribute
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="Attribute"/> class.
        /// </summary>
        public Attribute(string name)
        {
            this.Name = name;
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Attribute"/> class.
        /// </summary>
        public Attribute(string name,
                         IEnumerable<Value> arguments)
            : this(name)
        {
            this.Arguments = arguments;
        }

        #endregion

        #region Public Properties

        public IEnumerable<Value> Arguments { get; private set; }

        public string Name { get; private set; }

        #endregion
    }
}