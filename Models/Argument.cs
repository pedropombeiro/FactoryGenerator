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

        #endregion

        #region Public Properties

        /// <summary>
        ///     Gets the attribute of the argument.
        /// </summary>
        public string Attribute { get; set; }

        /// <summary>
        ///     Gets the value indicating whether the <see cref="Argument"/> is the last.
        /// </summary>
        public bool IsLast { get; set; }

        /// <summary>
        ///     Gets the value indicating whether the <see cref="Argument"/> is a natif type, i.e.: bool, int, string, etc.
        /// </summary>
        public bool IsValueType { get; set; }

        /// <summary>
        ///     Gets the name of the <see cref="Argument"/>.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        ///     Gets the type of the <see cref="Argument"/>.
        /// </summary>
        public string Type { get; set; }

        #endregion
    }
}