namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    public class Field
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="Field"/> class.
        /// </summary>
        public Field(string name,
                     string type)
        {
            this.Name = name;
            this.Type = type;
        }

        #endregion

        #region Public Properties

        public string Name { get; private set; }

        public string Type { get; private set; }

        #endregion
    }
}