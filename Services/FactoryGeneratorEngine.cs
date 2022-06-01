namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

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
        /// <param name="factoryFor">
        ///     The template name of the factory that will be used of generating the factory.
        /// </param>
        /// <param name="isSdkStyleProject">
        ///     Whether this is an sdk style project where the file doesn't need to be added to the csproj.
        /// </param>
        /// <returns>
        ///     The <see cref="Document"/> representing the factory file.
        /// </returns>
        public FactorGeneratoryResult Generate(string fileName,
                                               object model,
                                               string factoryFor)
        {
            var template = this.ResolveTemplatePath(factoryFor);
            var code = Render.FileToString(template, model);
            return new FactorGeneratoryResult(code);
        }

        #endregion

        #region Methods

        private string ResolveTemplatePath(string factoryFor)
        {
            var customTemplatePath = string.Format(@"{0}\{1}.render", Path.GetDirectoryName(this.templatePath), factoryFor);
            if (File.Exists(customTemplatePath))
            {
                return customTemplatePath;
            }

            return this.templatePath;
        }

        #endregion
    }
}