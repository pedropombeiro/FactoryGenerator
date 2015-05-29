namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    public class Argument
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="Argument"/> class.
        /// </summary>
        public Argument(string value,
                        string name)
        {
            this.Value = value;
            this.Name = name;
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Argument"/> class.
        /// </summary>
        public Argument(string value,
                        string name,
                        bool isInjected)
            : this(value, name)
        {
            this.IsInjected = isInjected;
        }

        #endregion

        #region Public Properties

        public bool IsInjected { get; private set; }

        public bool IsLast { get; set; }

        public string Name { get; private set; }

        public string Value { get; private set; }

        #endregion
    }
}