using Serilog;

namespace BeanBot.Util
{
    public static class LoggerSetup
    {
        public static void CreateLoggerConfiguration()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File("logs\\BeanBotLogs.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();
            Log.Information("Util/Support.cs - CreateLoggerConfiguration: Logger Configuration complete");
        }
    }
}
