namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    public class FactoryFile
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="FactoryFile"/> class.
        /// </summary>
        public FactoryFile(string ns,
                           Class @class,
                           string innerUsings,
                           string outerUsings)
        {
            this.Class = @class;
            this.Namespace = ns;
            this.InnerUsings = innerUsings;
            this.OuterUsings = outerUsings;
        }

        #endregion

        #region Public Properties

        public Class Class { get; private set; }

        public string InnerUsings { get; private set; }

        public string Namespace { get; private set; }

        public string OuterUsings { get; private set; }

        #endregion
    }
}