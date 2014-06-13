namespace DeveloperInTheFlow.AutoGenFactories
{
    using System;
    using System.Threading.Tasks;

    using Microsoft.CodeAnalysis.MSBuild;

    internal class Program
    {
        #region Methods

        private static async Task GenerateFactoriesAsync()
        {
            var workspace = MSBuildWorkspace.Create();
            var solution =
                await workspace.OpenSolutionAsync(@"C:\Users\Pedro\Documents\Liebherr\Lioba\solution\Lioba.sln");

            foreach (var project in solution.Projects)
            {
                Console.WriteLine(project.Name);
            }

            var factoryTemplate = new FactoryGenerator(workspace, solution);

            await factoryTemplate.ExecuteAsync();
        }

        private static void Main(string[] args)
        {
            Console.WriteLine("\n----------------------------------\nT4 Template\n----------------------------------\n");

            GenerateFactoriesAsync().Wait();
        }

        #endregion
    }
}