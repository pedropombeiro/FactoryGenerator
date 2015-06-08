namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    public class GenericType
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="GenericType"/> class.
        /// </summary>
        public GenericType(string name)
        {
            this.Name = name;
        }

        #endregion

        #region Public Properties

        public string Name { get; private set; }

        #endregion
    }
}