namespace DeveloperInTheFlow.FactoryGenerator
{
    using NLog.Conditions;

    [ConditionMethods]
    public static class LoggingConditions
    {
        #region Public Methods and Operators

        [ConditionMethod("teamcity-output")]
        public static bool TeamCityOutputIsEnabled()
        {
            return Program.CommandLineOptions.EnableTeamCityOutput;
        }

        #endregion
    }
}