namespace DeveloperInTheFlow.FactoryGenerator
{
    using CommandLine;
    using CommandLine.Text;

    public class CommandLineOptions
    {
        #region Public Properties

        [Option('a', "attribute-import-list", Required = false, HelpText = "Attributes to import", Default = "")]
        public string AttributeImportList { get; set; }

        [Option("teamcity-output", Required = false, HelpText = "Enable TeamCity output", Default = false)]
        public bool EnableTeamCityOutput { get; set; }

        [Option('s', "solution", Required = true, HelpText = "The path to the solution file to process")]
        public string SolutionPath { get; set; }

        [Option('t', "templatePath", Required = false, HelpText = "The path of the template that will be used for generating factories.", Default = "DefaultTemplate.render")]
        public string TemplatePath { get; set; }

        [Option('d', "doc", Required = false, HelpText = "Import XML documentation into generated factories", Default = false)]
        public bool WriteXmlDoc { get; set; }

        #endregion
    }
}