namespace DeveloperInTheFlow.FactoryGenerator
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
            var consoleTarget = loggingConfiguration.FindTargetByName("console");

            if (commandLineOptions.EnableTeamCityOutput)
            {
                var teamCityProgressTarget = loggingConfiguration.FindTargetByName("TeamCity_progress");
                var teamCityErrorTarget = loggingConfiguration.FindTargetByName("TeamCity_error");
                var teamCityProgressLoggingRule = new LoggingRule("*", teamCityProgressTarget);
                var teamCityErrorLoggingRule = new LoggingRule("*", NLog.LogLevel.Error, teamCityErrorTarget);
                var consoleLoggingRule = new LoggingRule("*", consoleTarget);

                consoleLoggingRule.EnableLoggingForLevel(NLog.LogLevel.Debug);
                teamCityProgressLoggingRule.EnableLoggingForLevel(NLog.LogLevel.Info);
                teamCityProgressLoggingRule.EnableLoggingForLevel(NLog.LogLevel.Warn);

                loggingRules.Add(consoleLoggingRule);
                loggingRules.Add(teamCityProgressLoggingRule);
                loggingRules.Add(teamCityErrorLoggingRule);
            }
            else
            {
                var loggingRule = new LoggingRule("*", NLog.LogLevel.Debug, consoleTarget);
                loggingRules.Add(loggingRule);
            }

            try
            {
                GenerateFactoriesAsync(commandLineOptions.SolutionPath,
                                       commandLineOptions.AttributeImportList.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries),
                                       commandLineOptions.WriteXmlDoc)
                    .Wait();
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

                Environment.ExitCode = innerException.HResult;
            }
        }

        #endregion
    }
}