using Serilog;

using System.IO;

namespace BeanBot.Util
{
    public static class DirectorySetup
    {
        public readonly static string botBaseDirectory = Path.Combine("BeanBotFiles");
        public readonly static string logsDirectory = Path.Combine(botBaseDirectory, "Logs");

        public static void MakeSureAllDirectoriesExist()
        {
            MakeSureBaseDirectoryExists();
            MakeSureLogsDirectoryExists(Path.GetFullPath(logsDirectory));
        }

        internal static void MakeSureBaseDirectoryExists()
        {
            if (Directory.Exists(botBaseDirectory))
            {
                Log.Information($"Bean Bot base file directory found at {Path.GetFullPath(botBaseDirectory)}");
            }
            else
            {
                Log.Information($"Creating Bean Bot base file directory at: {Path.GetFullPath(botBaseDirectory)}");
                Directory.CreateDirectory(Path.GetFullPath(botBaseDirectory));
            }
        }

        internal static void MakeSureLogsDirectoryExists(string logDirectory)
        {
            if (Directory.Exists(logDirectory))
            {
                Log.Information($"Bean Bot log directory found at {logDirectory}");
            }
            else
            {
                Log.Information($"Creating Bean Bot log directory at: {logDirectory}");
                Directory.CreateDirectory(logDirectory);
            }
        }
    }
}
