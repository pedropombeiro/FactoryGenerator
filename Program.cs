namespace DeveloperInTheFlow.FactoryGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;

    using CommandLine;

    using Common.Logging;

    using Microsoft.Build.Locator;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.MSBuild;

    using NLog;

    using LogManager = Common.Logging.LogManager;

    internal class Program
    {
        #region Static Fields

        private static readonly ILog Logger;

        #endregion

        #region Constructors and Destructors

        static Program()
        {
            // Replaces obsolete ConfigurationItemFactory.Default.RegisterItemsFromAssembly(Assembly.GetExecutingAssembly());
            NLog.LogManager.Setup().SetupExtensions(ext => ext.RegisterAssembly(Assembly.GetExecutingAssembly()));

            Logger = LogManager.GetLogger<Program>();
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

        private static void Main(string[] args)
        {
            var result = Parser.Default.ParseArguments(() => new CommandLineOptions(), args) as Parsed<CommandLineOptions>;
            if (result == null)
            {
                Environment.Exit(1);
            }

            CommandLineOptions = result.Value;

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
                var vsInstances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
                var vs2017 = vsInstances.FirstOrDefault(x => x.Version.Major == 15);
                var vs2019 = vsInstances.FirstOrDefault(x => x.Version.Major == 16);
                var vs2022 = vsInstances.FirstOrDefault(x => x.Version.Major == 17);
                var vs2026 = vsInstances.FirstOrDefault(x => x.Version.Major == 18);

                VisualStudioInstance usedInstance = vs2026 ?? vs2022 ?? vs2019 ?? vs2017;
                if (usedInstance == null)
                    throw new Exception("Could not find VS2026, VS2022, VS2019 or VS2017 installation");

                MSBuildLocator.RegisterInstance(usedInstance);

                GenerateFactoriesAsync(
                                       CommandLineOptions.SolutionPath,
                                       CommandLineOptions.AttributeImportList.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries),
                                       CommandLineOptions.WriteXmlDoc,
                                       CommandLineOptions.TemplatePath,
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