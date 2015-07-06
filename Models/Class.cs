namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    using System.Collections.Generic;
    using System.Linq;

    public class Class
    {
        #region Fields

        private readonly IEnumerable<GenericType> genericTypes;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="Class"/> class.
        /// </summary>
        public Class(IEnumerable<Attribute> attributes,
                     string concreteClassName,
                     Constructor constructor,
                     IEnumerable<Method> methods,
                     IEnumerable<Field> fields,
                     IEnumerable<GenericType> genericTypes,
                     string inherit,
                     string name)
        {
            this.genericTypes = genericTypes;
            this.Attributes = attributes;
            this.ConcreteClassName = concreteClassName;
            this.Constructor = constructor;
            this.Methods = methods;
            this.Fields = fields;
            this.Inherit = inherit;
            this.Name = name;
        }

        #endregion

        #region Public Properties

        public IEnumerable<Attribute> Attributes { get; set; }

        public string ConcreteClassName { get; set; }

        public Constructor Constructor { get; set; }

        public IEnumerable<Field> Fields { get; set; }

        public string GenericTypes
        {
            get
            {
                return string.Join(",", this.genericTypes.Select(x => x.Name));
            }
        }

        public string Inherit { get; set; }

        public IEnumerable<Method> Methods { get; set; }

        public string Name { get; set; }

        #endregion
    }
}