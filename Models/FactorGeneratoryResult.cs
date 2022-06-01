namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    using Microsoft.CodeAnalysis;

    public class FactorGeneratoryResult
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="FactorGeneratoryResult"/> class.
        /// </summary>
        public FactorGeneratoryResult(string code)
        {
            this.Code = code;
        }

        #endregion

        #region Public Properties

        public string Code { get; }

        #endregion
    }
}