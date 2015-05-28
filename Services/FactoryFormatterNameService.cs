namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using Humanizer;

    using Microsoft.CodeAnalysis.CSharp.Syntax;

    public class FactoryFormatterNameService
    {
        #region Public Methods and Operators

        /// <summary>
        ///     Formats the factory name.
        /// </summary>
        /// <param name="concreteTypeDeclarationSyntax">
        ///     The concrete class that will be builded by the factory.
        /// </param>
        /// <returns>
        ///     The factory name.
        /// </returns>
        public string GetName(ClassDeclarationSyntax concreteTypeDeclarationSyntax)
        {
            return "{0}Factory".FormatWith(concreteTypeDeclarationSyntax.Identifier.ValueText);
        }

        #endregion
    }
}