namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System.Collections.Generic;
    using System.IO;

    using DeveloperInTheFlow.FactoryGenerator.Models;

    using Microsoft.CodeAnalysis;

    using Nustache.Core;

    /// <summary>
    ///     Service responsible for building the factory <see cref="Document"/>.
    /// </summary>
    public class FactoryGeneratorEngine
    {
        #region Fields

        private readonly Project project;

        private readonly string templatePath;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="FactoryGeneratorEngine"/> class.
        /// </summary>
        public FactoryGeneratorEngine(Project project,
                                      string templatePath)
        {
            this.project = project;
            this.templatePath = templatePath;
        }

        #endregion

        #region Public Methods and Operators

        /// <summary>
        ///     Generates the factory as <see cref="Document"/>.
        /// </summary>
        /// <param name="fileName">
        ///     The file name of the factory.
        /// </param>
        /// <param name="folders">
        ///     Folders list.
        /// </param>
        /// <param name="model">
        ///     Model representing the factory.
        /// </param>
        /// <returns>
        ///     The <see cref="Document"/> representing the factory file.
        /// </returns>
        public FactorGeneratoryResult Generate(string fileName,
                                               IEnumerable<string> folders,
                                               FactoryFile model)
        {
            var template = this.ResolveTemplatePath(model);
            var code = Render.FileToString(template, model);
            var document = this.project.AddDocument(fileName, code, folders);

            return new FactorGeneratoryResult(document, code);
        }

        #endregion

        #region Methods

        private string ResolveTemplatePath(FactoryFile model)
        {
            var customTemplatePath = string.Format("{0}.render", model.FactoryFor);
            if (File.Exists(customTemplatePath))
            { 
                return customTemplatePath;
            }

            return templatePath;
        }

        #endregion
    }
}