namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    /// <summary>
    ///     Model representing a parameter of a method.
    /// </summary>
    public class Parameter
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="Parameter"/> class.
        /// </summary>
        public Parameter(string value,
                         bool isInjected,
                         bool isValueType)
        {
            this.Value = value;
            this.IsInjected = isInjected;
            this.IsValueType = isValueType;
        }

        #endregion

        #region Public Properties

        /// <summary>
        ///     Gets the value indicating whether the <see cref="Parameter"/> has been injected into the constructor.
        /// </summary>
        public bool IsInjected { get; set; }

        /// <summary>
        ///     Get the value indicating whether the <see cref="Parameter"/> is the last.
        /// </summary>
        public bool IsLast { get; set; }

        /// <summary>
        ///     Get the value indicating whether the value of the <see cref="Parameter"/> is a natif type, i.e.: bool, int, string, etc.
        /// </summary>
        public bool IsValueType { get; set; }

        /// <summary>
        ///     Gets the value of the <see cref="Parameter"/>.
        /// </summary>
        public string Value { get; set; }

        #endregion
    }
}