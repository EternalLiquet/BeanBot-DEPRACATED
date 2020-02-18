using Serilog;

using System.IO;

namespace BeanBot.Util
{
    public static class DirectorySetup
    {
        public readonly static string botBaseDirectory = Path.Combine("BeanBotFiles");

        public static void MakeSureAllDirectoriesExist()
        {
            Log.Information("Making sure all necessary directories exist");
            MakeSureBaseDirectoryExists();
            MakeSureSettingsDirectoryExists(Path.GetFullPath(AppSettings.settingsFileDirectory));
        }

        internal static void MakeSureBaseDirectoryExists()
        {
            if (Directory.Exists(botBaseDirectory))
            {
                Log.Information($"Bean Bot base file directory found at {Path.GetFullPath(botBaseDirectory)}");
            }
            else
            {
                Log.Error($"Bean Bot base file directory not found, creating directory at: {Path.GetFullPath(botBaseDirectory)}");
                Directory.CreateDirectory(Path.GetFullPath(botBaseDirectory));
            }
        }

        internal static void MakeSureSettingsDirectoryExists(string settingsFileDirectory)
        {
            if (Directory.Exists(settingsFileDirectory))
            {
                Log.Information($"Bean Bot settings file directory found at {settingsFileDirectory}");
            }
            else
            {
                Log.Error($"Bean Bot settings file directory not found, creating directory at: {settingsFileDirectory}");
                Directory.CreateDirectory(settingsFileDirectory);
            }
        }
    }
}
