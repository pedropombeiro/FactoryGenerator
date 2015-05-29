namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    using System.Collections.Generic;
    using System.Linq;

    public class Method
    {
        #region Fields

        private readonly IEnumerable<Argument> genericTypes;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="Method"/> class.
        /// </summary>
        public Method(string name,
                      string returnType,
                      string newInstanceType,
                      IEnumerable<Argument> arguments,
                      IEnumerable<Argument> constructorArguments,
                      IEnumerable<Argument> genericTypes,
                      string xmlDoc)
        {
            this.Name = name;
            this.ReturnType = returnType;
            this.NewInstanceType = newInstanceType;
            this.XmlDoc = xmlDoc;
            this.Arguments = arguments.ToArray();
            this.ConstructorArguments = constructorArguments.ToArray();
            this.genericTypes = genericTypes;
        }

        #endregion

        #region Public Properties

        public IEnumerable<Argument> Arguments { get; private set; }

        public IEnumerable<Argument> ConstructorArguments { get; private set; }

        public string GenericTypes
        {
            get
            {
                return string.Join(",", this.genericTypes.Select(x => x.Value));
            }
        }

        public string Name { get; private set; }

        public string NewInstanceType { get; set; }

        public string ReturnType { get; private set; }

        public string XmlDoc { get; set; }

        #endregion
    }
}