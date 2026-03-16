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
            if (!Directory.Exists(botBaseDirectory))
            {
                Directory.CreateDirectory(Path.GetFullPath(botBaseDirectory));
            }
        }

        internal static void MakeSureLogsDirectoryExists(string logDirectory)
        {
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }
        }
    }
}
