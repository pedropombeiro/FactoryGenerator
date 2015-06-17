namespace DeveloperInTheFlow.FactoryGenerator.Models
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Reflection.Emit;
    using System.Threading;

    public class Attribute
    {
        #region Static Fields

        private static ModuleBuilder modBuilder;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="Attribute"/> class.
        /// </summary>
        protected Attribute()
        {
        }

        #endregion

        #region Public Properties

        public IEnumerable<Value> Arguments { get; private set; }

        public string Name { get; private set; }

        #endregion

        #region Properties

        private static ModuleBuilder ModBuilder
        {
            get
            {
                if (modBuilder == null)
                {
                    var assemblyName = new AssemblyName();
                    assemblyName.Name = "AttributeTypesAssembly";
                    var thisDomain = Thread.GetDomain();
                    var asmBuilder = thisDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
                    modBuilder = asmBuilder.DefineDynamicModule(asmBuilder.GetName().Name, false);
                }

                return modBuilder;
            }
        }

        #endregion

        #region Public Methods and Operators

        public static Attribute Create(string name,
                                       IEnumerable<Value> arguments)
        {
            Type dynamicType = CreateDynamicType(name);

            var dynamicInstance = (Attribute)Activator.CreateInstance(dynamicType);

            dynamicInstance.Name = name;
            dynamicInstance.Arguments = arguments;

            return dynamicInstance;
        }

        public static Attribute Create(string name)
        {
            return Create(name, null);
        }

        #endregion

        #region Methods

        private static Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

        private static Type CreateDynamicType(string attributeName)
        {
            if (typeCache.ContainsKey(attributeName))
            {
                return typeCache[attributeName];
            }

            var typeBuilder = ModBuilder.DefineType(string.Format("{0}Attribute", attributeName),
                                                    TypeAttributes.Public |
                                                    TypeAttributes.Class |
                                                    TypeAttributes.AutoClass |
                                                    TypeAttributes.AnsiClass |
                                                    TypeAttributes.BeforeFieldInit |
                                                    TypeAttributes.AutoLayout,
                                                    typeof(Attribute),
                                                    new Type[0]);

            var propertyBuilder = typeBuilder.DefineProperty(attributeName, System.Reflection.PropertyAttributes.None, typeof(bool), new Type[0]);

            var getterBuilder = typeBuilder.DefineMethod(string.Format("get_{0}", attributeName),
                                                         MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                                                         typeof(bool),
                                                         Type.EmptyTypes);
            var getterIl = getterBuilder.GetILGenerator();

            getterIl.Emit(OpCodes.Ldc_I4_1);
            getterIl.Emit(OpCodes.Ret);

            propertyBuilder.SetGetMethod(getterBuilder);

            var dynamicType = typeBuilder.CreateType();

            typeCache.Add(attributeName, dynamicType);

            return dynamicType;
        }

        #endregion
    }
}