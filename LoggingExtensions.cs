namespace DeveloperInTheFlow.FactoryGenerator
{
    using System.Globalization;

    using NLog;

    internal static class LoggingExtensions
    {
        internal static void DebugFormat(this Logger logger, string format, params object[] args)
        {
            logger.Debug(string.Format(CultureInfo.InvariantCulture, format, args));
        }

        internal static void ErrorFormat(this Logger logger, string format, params object[] args)
        {
            logger.Error(string.Format(CultureInfo.InvariantCulture, format, args));
        }

        internal static void InfoFormat(this Logger logger, string format, params object[] args)
        {
            logger.Info(string.Format(CultureInfo.InvariantCulture, format, args));
        }

        internal static void WarnFormat(this Logger logger, string format, params object[] args)
        {
            logger.Warn(string.Format(CultureInfo.InvariantCulture, format, args));
        }
    }
}
