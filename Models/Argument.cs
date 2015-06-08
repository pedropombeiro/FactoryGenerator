namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    public class Argument
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="Argument"/> class.
        /// </summary>
        public Argument(string type,
                        string name,
                        string attribute,
                        bool isValueType)
        {
            this.Type = type;
            this.Name = name;
            this.Attribute = attribute;
            this.IsValueType = isValueType;
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Argument"/> class.
        /// </summary>
        public Argument(string type,
                        string name,
                        string attribute,
                        bool isInjected,
                        bool isValueType)
            : this(type, name, attribute, isValueType)
        {
            this.IsInjected = isInjected;
        }

        #endregion

        #region Public Properties

        public string Attribute { get; private set; }

        public bool IsInjected { get; private set; }

        public bool IsLast { get; set; }

        public bool IsValueType { get; private set; }

        public string Name { get; private set; }

        public string Type { get; private set; }

        #endregion
    }
}