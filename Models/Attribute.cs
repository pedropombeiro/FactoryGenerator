namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    using System.Collections.Generic;
    using System.Linq;

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
                         IEnumerable<Argument> arguments)
            : this(name)
        {
            this.Arguments = arguments;
        }

        #endregion

        #region Public Properties

        public IEnumerable<Argument> Arguments { get; private set; }

        public string Name { get; private set; }

        #endregion
    }
}