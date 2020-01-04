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
            MakeSureBotTokenDirectoryExists(Path.GetFullPath(TokenSetup.botTokenDirectory));
        }

        private static void MakeSureBaseDirectoryExists()
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

        private static void MakeSureBotTokenDirectoryExists(string botTokenDirectory)
        {
            if (Directory.Exists(botTokenDirectory))
            {
                Log.Information($"Bean Bot token file directory found at {botTokenDirectory}");
            }
            else
            {
                Log.Error($"Bean Bot token file directory not found, creating directory at: {botTokenDirectory}");
                Directory.CreateDirectory(botTokenDirectory);
            }
        }
    }
}
