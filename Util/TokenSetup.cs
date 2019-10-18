using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BeanBot.Util
{
    public static class TokenSetup
    {
        public readonly static string botTokenFilePath = DirectorySetup.botTokenDirectory + "beantoken.succsuccsucc";
        private static void MakeSureBeanTokenFileExists()
        {
            if (!File.Exists(botTokenFilePath))
            {
                File.WriteAllText(botTokenFilePath, "INSERT BOT TOKEN HERE");
                string beanTokenFileLocation = Path.GetFullPath(botTokenFilePath);
                Log.Error("Bean Token file not found!");
                Log.Error($"Bean Token file created automatically at: {beanTokenFileLocation}");
                Log.Error("Please configure this file with help using the documentation/readme");
            }

        }
        private static void GetBotTokenFromConfigFile()
        {
            string beanTokenFileContents = ReadBeanTokenFile();
            if (ValidBeanToken(beanTokenFileContents))
            {
                //botToken = beanTokenFileContents;
            }
        }

        private static string ReadBeanTokenFile()
        {
            try
            {
                string beanTokenFileContents = File.ReadAllText(botBasePath);
                return beanTokenFileContents;
            }
            catch (FileNotFoundException)
            {
                File.WriteAllText(botBasePath, "INSERT BOT TOKEN HERE");
                string beanTokenFileLocation = Path.GetFullPath(botBasePath);
                Log.Error("Bean Token file not found!");
                Log.Error($"Bean Token file created automatically at: {beanTokenFileLocation}");
                Log.Error("Please configure this file with help using the documentation/readme");
                return null;
            }
        }
        private static bool ValidBeanToken(string beanTokenFileContents)
        {
            if (beanTokenFileContents == "INSERT BOT TOKEN HERE" || beanTokenFileContents == null)
            {
                Log.Error($"Please configure the bean token file at {Path.GetFullPath(botBasePath)}");
                return false;
            }
            return true;
        }
    }
}
