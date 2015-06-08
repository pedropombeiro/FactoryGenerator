namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    public class Value
    {
        #region Constructors and Destructors

        /// <summary>
        /// Initializes a new instance of the <see cref="Value"/> class.
        /// </summary>
        public Value(string text,
                     bool isLast)
        {
            this.Text = text;
            this.IsLast = isLast;
        }

        #endregion

        #region Public Properties

        public bool IsLast { get; private set; }

        public string Text { get; private set; }

        #endregion
    }
}