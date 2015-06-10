namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    using Microsoft.CodeAnalysis;

    public class FactorGeneratoryResult
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="FactorGeneratoryResult"/> class.
        /// </summary>
        public FactorGeneratoryResult(Document document,
                                      string code)
        {
            this.Code = code;
            this.Document = document;
        }

        #endregion

        #region Public Properties

        public string Code { get; private set; }

        public Document Document { get; private set; }

        #endregion
    }
}