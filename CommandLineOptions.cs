namespace DeveloperInTheFlow.FactoryGenerator
{
    using CommandLine;
    using CommandLine.Text;

    public class CommandLineOptions
    {
        #region Public Properties

        [Option('a', "attribute-import-list", Required = false, HelpText = "Attributes to import", DefaultValue = "")]
        public string AttributeImportList { get; set; }

        [Option("teamcity-output", Required = false, HelpText = "Enable TeamCity output", DefaultValue = false)]
        public bool EnableTeamCityOutput { get; set; }

        [ParserState]
        public IParserState LastParserState { get; set; }

        [Option('s', "solution", Required = true, HelpText = "The path to the solution file to process")]
        public string SolutionPath { get; set; }

        [Option('d', "doc", Required = false, HelpText = "Import XML documentation into generated factories", DefaultValue = false)]
        public bool WriteXmlDoc { get; set; }

        #endregion

        #region Public Methods and Operators

        [HelpOption]
        public string GetUsage()
        {
            return HelpText.AutoBuild(this,
                                      current => HelpText.DefaultParsingErrorsHandler(this, current));
        }

        #endregion
    }
}