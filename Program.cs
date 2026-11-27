namespace DeveloperInTheFlow.FactoryGenerator
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using System.Xml;

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

        private static void PatchBuildHostBindingRedirects(string msbuildBinPath)
        {
            var assembliesToPatch = new[]
            {
                "Microsoft.Bcl.AsyncInterfaces",
                "System.Buffers",
                "System.Collections.Immutable",
                "System.Memory",
                "System.Numerics.Vectors",
                "System.Runtime.CompilerServices.Unsafe",
                "System.Threading.Tasks.Extensions",
            };

            var buildHostDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BuildHost-net472");
            var configPath = Path.Combine(buildHostDir, "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe.config");

            if (!Directory.Exists(buildHostDir) || !File.Exists(configPath))
            {
                Logger.Warn($"BuildHost-net472 not found at {buildHostDir}, skipping binding redirect patch.");
                return;
            }

            // MSBuild ships DLLs in its bin dir (e.g. .../MSBuild/Current/Bin), but
            // MSBuildLocator gives us the path which may point to the amd64 subdirectory.
            // Check both the given path and its parent to handle both cases.
            var searchDirs = new[] { msbuildBinPath, Path.GetDirectoryName(msbuildBinPath) }
                             .Where(d => d != null && Directory.Exists(d))
                             .Distinct()
                             .ToArray();

            var config = new XmlDocument();
            config.Load(configPath);

            var ns = new XmlNamespaceManager(config.NameTable);
            ns.AddNamespace("b", "urn:schemas-microsoft-com:asm.v1");

            var patched = false;

            foreach (var assemblyName in assembliesToPatch)
            {
                var sourceDll = searchDirs
                    .SelectMany(dir => Directory.EnumerateFiles(dir, $"{assemblyName}.dll", SearchOption.AllDirectories))
                    .FirstOrDefault();

                if (sourceDll == null)
                {
                    Logger.Debug($"Skipping {assemblyName}: not found in MSBuild bin.");
                    continue;
                }

                var newVersion = AssemblyName.GetAssemblyName(sourceDll).Version;
                var newVersionStr = newVersion.ToString(4);

                File.Copy(sourceDll, Path.Combine(buildHostDir, $"{assemblyName}.dll"), overwrite: true);

                var identityNodes = config.SelectNodes($"//b:assemblyIdentity[@name='{assemblyName}']", ns);
                if (identityNodes == null)
                    continue;

                foreach (XmlNode identityNode in identityNodes)
                {
                    var bindingRedirect = identityNode.ParentNode?.SelectSingleNode("b:bindingRedirect", ns) as XmlElement;
                    if (bindingRedirect == null)
                        continue;

                    var existing = bindingRedirect.GetAttribute("newVersion");
                    if (existing == newVersionStr)
                        continue;

                    bindingRedirect.SetAttribute("oldVersion", $"0.0.0.0-{newVersionStr}");
                    bindingRedirect.SetAttribute("newVersion", newVersionStr);
                    patched = true;
                    Logger.Debug($"Patched binding redirect for {assemblyName}: {existing} → {newVersionStr}");
                }
            }

            if (patched)
            {
                config.Save(configPath);
                Logger.Info("BuildHost-net472 binding redirects patched for current VS MSBuild version.");
            }
        }

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

                PatchBuildHostBindingRedirects(usedInstance.MSBuildPath);

                GenerateFactoriesAsync(CommandLineOptions.SolutionPath,
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