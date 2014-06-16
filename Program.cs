namespace DeveloperInTheFlow.AutoGenFactories
{
    using System;
    using System.Threading.Tasks;

    using Common.Logging;

    using Microsoft.CodeAnalysis.MSBuild;

    using NLog.Config;

    internal class Program
    {
        #region Static Fields

        private static readonly ILog Logger = LogManager.GetLogger<Program>();

        #endregion

        #region Methods

        private static async Task GenerateFactoriesAsync(string solutionPath,
                                                         string[] attributeImportList,
                                                         bool writeXmlDoc)
        {
            var workspace = MSBuildWorkspace.Create();
            var solution = await workspace.OpenSolutionAsync(solutionPath);

            if (Logger.IsTraceEnabled)
            {
                foreach (var project in solution.Projects)
                {
                    Logger.Trace(project.Name);
                }
            }

            var factoryGenerator = new FactoryGenerator(workspace, solution, attributeImportList, writeXmlDoc);

            await factoryGenerator.ExecuteAsync();
        }

        private static void Main(string[] args)
        {
            var commandLineOptions = new CommandLineOptions();
            if (!CommandLine.Parser.Default.ParseArguments(args, commandLineOptions))
            {
                Environment.Exit(1);
            }

            var loggingConfiguration = NLog.LogManager.Configuration;
            var loggingRules = loggingConfiguration.LoggingRules;

            if (commandLineOptions.EnableTeamCityOutput)
            {
                var teamCityLoggingRule = new LoggingRule("*", NLog.LogLevel.Info, loggingConfiguration.FindTargetByName("TeamCity"));
                var consoleLoggingRule = new LoggingRule("*", loggingConfiguration.FindTargetByName("console"));

                consoleLoggingRule.EnableLoggingForLevel(NLog.LogLevel.Debug);

                loggingRules.Add(teamCityLoggingRule);
                loggingRules.Add(consoleLoggingRule);
            }
            else
            {
                loggingRules.Add(new LoggingRule("*", NLog.LogLevel.Debug, loggingConfiguration.FindTargetByName("console")));
            }

            try
            {
                GenerateFactoriesAsync(commandLineOptions.SolutionPath, commandLineOptions.AttributeImportList.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries), commandLineOptions.WriteXmlDoc).Wait();
            }
            catch (AggregateException e)
            {
                var innerException = e.Flatten().InnerException;
                Logger.Fatal(innerException.Message, e);

                if (commandLineOptions.PauseOnError)
                {
                    Logger.Info("Press any key to exit.");
                    Console.ReadKey();
                }

                Environment.Exit(innerException.HResult);
            }
        }

        #endregion
    }
}