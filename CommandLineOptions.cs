namespace DeveloperInTheFlow.AutoGenFactories
{
    using CommandLine;

    public class CommandLineOptions
    {
        #region Public Properties

        [Option('s', "solution", Required = true, HelpText = "The path to the solution file to process")]
        public string SolutionPath { get; set; }

        [Option('d', "doc", Required = false, HelpText = "Import XML documentation into generated factories", DefaultValue = false)]
        public bool WriteXmlDoc { get; set; }

        [Option('p', "pause", Required = false, HelpText = "Pause on error", DefaultValue = true)]
        public bool PauseOnError { get; set; }

        [Option('a', "attribute-import-list", Required = false, HelpText = "Attributes to import")]
        public string AttributeImportList { get; set; }

        #endregion
    }
}