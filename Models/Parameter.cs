namespace DeveloperInTheFlow.FactoryGenerator.Models
{
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

        public bool IsInjected { get; private set; }

        public bool IsLast { get; set; }

        public bool IsValueType { get; private set; }

        public string Value { get; private set; }

        #endregion
    }
}