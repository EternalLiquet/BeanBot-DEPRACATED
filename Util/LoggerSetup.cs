using Serilog;

using System.IO;

namespace BeanBot.Util
{
    public static class LoggerSetup
    {
        public static void CreateLoggerConfiguration()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Console()
                .WriteTo.Async(a => a.File(Path.Combine(DirectorySetup.botBaseDirectory, "Logs", "BeanBotLogs.txt"), rollingInterval: RollingInterval.Day))
                .CreateLogger();
            Log.Information("Logger Configuration complete");
        }
    }
}
