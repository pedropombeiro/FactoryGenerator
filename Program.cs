namespace DeveloperInTheFlow.FactoryGenerator
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;

    using CommandLine;

    using Microsoft.Build.Locator;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.MSBuild;

    using NLog;

    internal class Program
    {
        #region Static Fields

        private static readonly Logger Logger;

        #endregion

        #region Constructors and Destructors

        static Program()
        {
            Logger = NLog.LogManager.GetCurrentClassLogger();
        }

        #endregion

        #region Properties

        internal static CommandLineOptions CommandLineOptions { get; private set; }

        #endregion

        #region Methods

        private static async Task GenerateFactoriesAsync(string solutionPath,
                                                         IEnumerable<string> attributeImportList,
                                                         bool writeXmlDoc,
                                                         string templatePath,
                                                         bool forceGeneration)
        {
            var workspace = MSBuildWorkspace.Create();
            workspace.WorkspaceFailed += (o,
                                          e) =>
                                         {
                                             if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure
                                                 // related to WPF
                                                 && !e.Diagnostic.Message.Contains("Microsoft.WinFx.targets"))
                                             {
                                                 Logger.ErrorFormat("Error: {0}", e.Diagnostic.Message);
                                             }
                                         };
            var solution = await workspace.OpenSolutionAsync(solutionPath);

            var factoryGenerator = new FactoryGenerator(workspace, solution, attributeImportList, writeXmlDoc, templatePath, forceGeneration);

            await factoryGenerator.ExecuteAsync();
        }

        private static void ConfigureLogging()
        {
            var configuration = new NLog.Config.LoggingConfiguration();
            var consoleTarget = new NLog.Targets.ColoredConsoleTarget("console")
            {
                Layout = "${message}",
                UseDefaultRowHighlightingRules = true,
            };
            configuration.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, consoleTarget);

            if (CommandLineOptions.EnableTeamCityOutput)
            {
                var progressTarget = new NLog.Targets.ConsoleTarget("TeamCity_progressMessage")
                {
                    Layout = "##teamcity[progressMessage '${message}']",
                };
                var buildStatusTarget = new NLog.Targets.ConsoleTarget("TeamCity_buildStatus")
                {
                    Layout = "##teamcity[buildStatus status='FAILURE' text='${message}']",
                };
                configuration.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Warn, progressTarget);
                configuration.AddRule(NLog.LogLevel.Error, NLog.LogLevel.Fatal, buildStatusTarget);
            }

            NLog.LogManager.Configuration = configuration;
        }

        private static void Main(string[] args)
        {
            var result = Parser.Default.ParseArguments(() => new CommandLineOptions(), args) as Parsed<CommandLineOptions>;
            if (result == null)
            {
                if (args.Any(arg => arg == "--help" || arg == "-h" || arg == "--version"))
                    return;

                Environment.Exit(1);
                return;
            }

            CommandLineOptions = result.Value;
            ConfigureLogging();
            var templatePath = CommandLineOptions.TemplatePath;
            if (string.Equals(templatePath, "DefaultTemplate.render", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(templatePath))
            {
                templatePath = Path.Combine(AppContext.BaseDirectory, templatePath);
            }

            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;
            Console.WriteLine("Factory Generator version {0}", version);
            Console.WriteLine();
            Console.WriteLine("Command line arguments:");
            Console.WriteLine("  Solution (-s): {0}", CommandLineOptions.SolutionPath);
            Console.WriteLine("  Template Path (-t): {0}", CommandLineOptions.TemplatePath);
            Console.WriteLine("  Attribute Import List (-a): {0}", CommandLineOptions.AttributeImportList);
            Console.WriteLine();

            try
            {
                MSBuildLocator.RegisterDefaults();

                GenerateFactoriesAsync(CommandLineOptions.SolutionPath,
                                       CommandLineOptions.AttributeImportList.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries),
                                       CommandLineOptions.WriteXmlDoc,
                                       templatePath,
                                       CommandLineOptions.ForceGeneration)
                    .Wait();
            }
            catch (AggregateException e)
            {
                var innerException = e.Flatten().InnerException;
                Logger.Fatal(innerException.Message, e);

                Environment.ExitCode = innerException.HResult;
            }
            catch (Exception e)
            {
                Logger.Fatal(e.Message, e);
                Environment.ExitCode = e.HResult;
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