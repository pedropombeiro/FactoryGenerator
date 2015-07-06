namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    /// <summary>
    ///     Model representing a field of <see cref="Class"/>.
    /// </summary>
    public class Field
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="Field"/> class.
        /// </summary>
        public Field(string name,
                     string type,
                     bool isValueType)
        {
            this.Name = name;
            this.Type = type;
            this.IsValueType = isValueType;
        }

        #endregion

        #region Public Properties

        /// <summary>
        ///     Get the value indicating whether the value of the <see cref="Field"/> is a natif type, i.e.: bool, int, string, etc.
        /// </summary>
        public bool IsValueType { get; set; }

        /// <summary>
        ///     Gets the name of the <see cref="Field"/>.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        ///     Gets the type of the <see cref="Field"/>.
        /// </summary>
        public string Type { get; set; }

        #endregion
    }
}