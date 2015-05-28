namespace DeveloperInTheFlow.FactoryGenerator
{
    using System;
    using System.Reflection;
    using System.Threading.Tasks;

    using Common.Logging;

    using Microsoft.CodeAnalysis.MSBuild;

    using NLog.Config;

    internal class Program
    {
        #region Static Fields

        private static readonly ILog Logger;

        #endregion

        #region Constructors and Destructors

        static Program()
        {
            ConfigurationItemFactory.Default.RegisterItemsFromAssembly(Assembly.GetExecutingAssembly());

            Logger = LogManager.GetLogger<Program>();
        }

        #endregion

        #region Properties

        internal static CommandLineOptions CommandLineOptions { get; private set; }

        #endregion

        #region Methods

        private static async Task GenerateFactoriesAsync(string solutionPath,
                                                         string[] attributeImportList,
                                                         bool writeXmlDoc,
                                                         string templatePath)
        {
            var workspace = MSBuildWorkspace.Create();
            var solution = await workspace.OpenSolutionAsync(solutionPath);

            var factoryGenerator = new FactoryGenerator(workspace, solution, attributeImportList, writeXmlDoc, templatePath);

            await factoryGenerator.ExecuteAsync();
        }

        private static void Main(string[] args)
        {
            CommandLineOptions = new CommandLineOptions();
            if (!CommandLine.Parser.Default.ParseArguments(args, CommandLineOptions))
            {
                Environment.Exit(1);
            }

            try
            {
                GenerateFactoriesAsync(CommandLineOptions.SolutionPath,
                                       CommandLineOptions.AttributeImportList.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries),
                                       CommandLineOptions.WriteXmlDoc,
                                       CommandLineOptions.TemplatePath)
                    .Wait();
            }
            catch (AggregateException e)
            {
                var innerException = e.Flatten().InnerException;
                Logger.Fatal(innerException.Message, e);

                Environment.ExitCode = innerException.HResult;
            }
            finally
            {
                NLog.LogManager.Flush();
                NLog.LogManager.Shutdown();
            }
        }

        #endregion
    }
}