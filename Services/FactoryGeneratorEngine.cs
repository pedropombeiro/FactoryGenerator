namespace DeveloperInTheFlow.FactoryGenerator.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using DeveloperInTheFlow.FactoryGenerator;
    using DeveloperInTheFlow.FactoryGenerator.Models;

    using Microsoft.CodeAnalysis;

    using NLog;

    using Nustache.Core;

    /// <summary>
    ///     Service responsible for building the factory <see cref="Document"/>.
    /// </summary>
    public class FactoryGeneratorEngine
    {
        #region Static Fields

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        #endregion

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
        /// <param name="model">
        ///     Model representing the factory.
        /// </param>
        /// <param name="factoryFor">
        ///     The template name of the factory that will be used of generating the factory.
        /// </param>
        /// <returns>
        ///     The <see cref="Document"/> representing the factory file.
        /// </returns>
        public FactorGeneratoryResult Generate(object model,
                                               string factoryFor)
        {
            var template = this.ResolveTemplatePath(factoryFor);
            Logger.Debug($"Render template used: {template}");
            var code = Render.FileToString(template, model);
            return new FactorGeneratoryResult(code);
        }

        #endregion

        #region Methods

        private string ResolveTemplatePath(string factoryFor)
        {
            var templateDirectory = Path.GetDirectoryName(this.templatePath);
            var customTemplatePath = Path.Combine(templateDirectory, $"{factoryFor}.render");
            
            if (File.Exists(customTemplatePath))
            {
                return customTemplatePath;
            }

            return this.templatePath;
        }

        #endregion
    }
}