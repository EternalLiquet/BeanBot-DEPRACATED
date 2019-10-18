using Serilog;
using System;
using System.IO;

namespace BeanBot.Util
{
    public static class DirectorySetup
    {
        public readonly static string botBaseDirectory = $"{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)}\\BeanBot\\";
        public readonly static string botTokenDirectory = botBaseDirectory + "BeanToken\";

        public static void MakeSureAllDirectoriesExist()
        {
            MakeSureBaseDirectoryExists();
            MakeSureBotTokenDirectoryExists();
        }

        private static void MakeSureBaseDirectoryExists()
        {
            if (Directory.Exists(botBaseDirectory))
            {
                Log.Information($"Bean Bot base file directory found at {botBaseDirectory}");
            }
            else
            {
                Log.Error($"Bean Bot base file directory not found, creating directory at: {botBaseDirectory}");
                Directory.CreateDirectory(botBaseDirectory);
            }
        }

        private static void MakeSureBotTokenDirectoryExists()
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
