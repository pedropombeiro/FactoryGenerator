namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Reflection.Emit;
    using System.Threading;

    public class DynamicTypeBuilderService
    {
        #region Static Fields

        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        private static ModuleBuilder modBuilder;

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

        #region Methods

        /// <summary>
        ///     Creates a dynamic type which extends the <paramref name="parentType"/> and adds implementations for each <paramref name="propertyNames"/> returning true.
        /// </summary>
        /// <param name="typeName">
        ///     The name of the newly created type.
        /// </param>
        /// <param name="parentType">
        ///     The type which is inherited.
        /// </param>
        /// <param name="propertyNames">
        ///     The names of the properties which should be implemented.
        /// </param>
        /// <returns>
        ///     A dynamic type extending the <paramref name="parentType"/>.
        /// </returns>
        public static Type CreateDynamicType(string typeName,
                                             Type parentType,
                                             params string[] propertyNames)
        {
            if (TypeCache.ContainsKey(typeName))
            {
                return TypeCache[typeName];
            }

            var typeBuilder = ModBuilder.DefineType(typeName,
                                                    TypeAttributes.Public |
                                                    TypeAttributes.Class |
                                                    TypeAttributes.AutoClass |
                                                    TypeAttributes.AnsiClass |
                                                    TypeAttributes.BeforeFieldInit |
                                                    TypeAttributes.AutoLayout,
                                                    parentType,
                                                    new Type[0]);

            foreach (var propertyName in propertyNames)
            {
                var propertyBuilder = typeBuilder.DefineProperty(propertyName, System.Reflection.PropertyAttributes.None, typeof(bool), new Type[0]);

                var getterBuilder = typeBuilder.DefineMethod(string.Format("get_{0}", propertyName),
                                                             MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                                                             typeof(bool),
                                                             Type.EmptyTypes);
                var getterIl = getterBuilder.GetILGenerator();

                getterIl.Emit(OpCodes.Ldc_I4_1);
                getterIl.Emit(OpCodes.Ret);

                propertyBuilder.SetGetMethod(getterBuilder);
            }

            var dynamicType = typeBuilder.CreateType();

            TypeCache.Add(typeName, dynamicType);

            return dynamicType;
        }

        #endregion
    }
}