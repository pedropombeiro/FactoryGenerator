namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    public class Argument
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="Argument"/> class.
        /// </summary>
        public Argument(string value)
        {
            this.Value = value;
        }

        #endregion

        #region Public Properties

        public bool IsLast { get; set; }

        public string Value { get; set; }

        #endregion
    }
}