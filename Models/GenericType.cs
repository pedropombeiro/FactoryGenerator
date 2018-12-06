namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    /// <summary>
    ///     Model representing a generic type.
    /// </summary>
    public class GenericType
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="GenericType"/> class.
        /// </summary>
        public GenericType(
            string name,
            string constraints)
        {
            this.Name = name;
            this.Constraints = constraints;
        }

        #endregion

        #region Public Properties

        /// <summary>
        ///     Gets the constraints of the generic type.
        /// </summary>
        public string Constraints { get; set; }

        /// <summary>
        ///     Gets the name of the generic type.
        /// </summary>
        public string Name { get; set; }

        #endregion
    }
}