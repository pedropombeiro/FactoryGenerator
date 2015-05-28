namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    using System.Collections.Generic;
    using System.Linq;

    public class Constructor
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="Constructor"/> class.
        /// </summary>
        public Constructor(IEnumerable<Argument> arguments,
                           IEnumerable<Attribute> attributes)
        {
            this.Arguments = arguments;
            this.Attributes = attributes;

            var lastArgument = this.Arguments.LastOrDefault();
            if (lastArgument != null)
            {
                lastArgument.IsLast = true;
            }
        }

        #endregion

        #region Public Properties

        public IEnumerable<Argument> Arguments { get; private set; }

        public IEnumerable<Attribute> Attributes { get; set; }

        #endregion
    }
}