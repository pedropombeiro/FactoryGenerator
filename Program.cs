namespace DeveloperInTheFlow.AutoGenFactories
{
    using System;
    using System.Threading.Tasks;

    using Microsoft.CodeAnalysis.MSBuild;

    internal class Program
    {
        #region Methods

        private static async Task GenerateFactoriesAsync(string solutionPath,
                                                         string[] attributeImportList,
                                                         bool writeXmlDoc)
        {
            var workspace = MSBuildWorkspace.Create();

            var solution = await workspace.OpenSolutionAsync(solutionPath);

            foreach (var project in solution.Projects)
            {
                Console.WriteLine(project.Name);
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

            try
            {
                GenerateFactoriesAsync(commandLineOptions.SolutionPath, commandLineOptions.AttributeImportList.Split(new []{ ',', ';' }, StringSplitOptions.RemoveEmptyEntries), commandLineOptions.WriteXmlDoc).Wait();
            }
            catch (AggregateException e)
            {
                var innerException = e.Flatten().InnerException;
                Console.WriteLine(innerException);

                if (commandLineOptions.PauseOnError)
                {
                    Console.WriteLine("Press any key to exit.");
                    Console.Read();
                }

                Environment.Exit(innerException.HResult);
            }
        }

        #endregion
    }
}