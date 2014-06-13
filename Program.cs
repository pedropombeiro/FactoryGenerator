namespace DeveloperInTheFlow.AutoGenFactories
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;

    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.MSBuild;

    internal class Program
    {
        #region Methods

        private static Assembly CompileCodeIntoAssembly(string code,
                                                        string virtualPath)
        {
            // Parse the source file using Roslyn
            var syntaxTree = CSharpSyntaxTree.ParseText(code);

            // Add all the references we need for the compilation
            var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location);
            var references = new List<MetadataReference>();
            references.Add(new MetadataFileReference(Path.Combine(assemblyPath, "mscorlib.dll")));
            references.Add(new MetadataFileReference(Path.Combine(assemblyPath, "System.dll")));
            references.Add(new MetadataFileReference(Path.Combine(assemblyPath, "System.Linq.dll")));
            references.Add(new MetadataFileReference(Assembly.GetEntryAssembly().Location));
            /*foreach (Assembly referencedAssembly in BuildManager.GetReferencedAssemblies())
            {
                references.Add(new MetadataFileReference(referencedAssembly.Location));
            }*/

            var compilationOptions = new CSharpCompilationOptions(outputKind: OutputKind.DynamicallyLinkedLibrary);

            // Note: using a fixed assembly name, which doesn't matter as long as we don't expect cross references of generated assemblies
            var compilation = CSharpCompilation.Create("SomeAssemblyName", new[] { syntaxTree }, references, compilationOptions);

            // Generate the assembly into a memory stream
            var memStream = new MemoryStream();
            var emitResult = compilation.Emit(memStream);

            if (!emitResult.Success)
            {
                var diagnostic = emitResult.Diagnostics.First();
                var message = diagnostic.GetMessage();
                var linePosition = diagnostic.Location.GetLineSpan().StartLinePosition;

                //throw new HttpParseException(message, null, virtualPath, null, linePosition.Line + 1);
            }

            return Assembly.Load(memStream.GetBuffer());
        }

        private static async Task GenerateFactoriesAsync()
        {
            var workspace = MSBuildWorkspace.Create();
            var solution =
                await workspace.OpenSolutionAsync(@"C:\Users\Pedro\Documents\Liebherr\Lioba\solution\Lioba.sln");

            foreach (var project in solution.Projects)
            {
                Console.WriteLine(project.Name);
            }

            var factoryTemplate = new FactoryTemplate(workspace, solution);

            await factoryTemplate.ExecuteAsync();
        }

        private static void Main(string[] args)
        {
            /*string templateName = @"C:\Users\Pedro\Documents\visual studio 2013\Projects\RazorTemplating\Razor.tt";
            string[] names = new string[] { "Mikael", "Bill", "Steve" };*/

            Console.WriteLine("\n----------------------------------\nT4 Template\n----------------------------------\n");

            GenerateFactoriesAsync().Wait();

            //IWorkspace workspace = Workspace.TryGetWorkspace()LoadSolution(@"<%PROVIDE PATH OF .SLN FILE");
            /*var language = new CSharpRazorCodeLanguage();
            var host = new RazorEngineHost(language)
            {
                DefaultBaseClass = "object",
                DefaultClassName = "FactoryTemplate",
                DefaultNamespace = "RazorTemplating",
            };

            // Everyone needs the System namespace, right?
            host.NamespaceImports.Add("System");

            // Create a new Razor Template Engine

            var engine = new RazorTemplateEngine(host);

            var reader = new StreamReader(templateName);
            // Generate code for the template
            GeneratorResults razorResult = engine.GenerateCode(reader);
            // Use CodeDom to generate source code from the CodeCompileUnit
            var codeDomProvider = new CSharpCodeProvider();
            var srcFileWriter = new StringWriter();
            codeDomProvider.GenerateCodeFromCompileUnit(razorResult.GeneratedCode, srcFileWriter, new CodeGeneratorOptions());

            var code = srcFileWriter.ToString();
            Console.WriteLine(code);

            var assembly = CompileCodeIntoAssembly(code, @"C:\a.cs");*/
        }

        #endregion
    }

    public abstract class OrderInfoTemplateBase
    {
        #region Public Methods and Operators

        public abstract void Execute();

        public virtual void Write(object value)
        {
            /* TODO: Write value */
        }

        public virtual void WriteLiteral(object value)
        {
            /* TODO: Write literal */
        }

        #endregion
    }
}