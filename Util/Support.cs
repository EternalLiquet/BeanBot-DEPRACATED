using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

using Serilog;

using YamlDotNet;
using YamlDotNet.RepresentationModel;

namespace BeanBot.Util
{
    public static class Support
    {
        public static string botToken;
        public static string botTokenPath = $@"{Environment.SpecialFolder.ProgramFiles}/BeanBot/beantoken.succsuccsucc";

        public static void StartupOperations()
        {
            CreateLoggerConfiguration();
            Log.Information("Util/Support.cs: Logger Configuration complete");
            GetBotTokenFromConfigFile();
        }

        private static void CreateLoggerConfiguration()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File("logs\\BeanBotLogs.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();
        }

        private static void GetBotTokenFromConfigFile()
        {
            string beanTokenFileContents = ReadBeanTokenFile();
            if (ValidBeanToken(beanTokenFileContents))
            {
                botToken = beanTokenFileContents;
            }
        }

        private static string ReadBeanTokenFile()
        {
            try
            {
                string beanTokenFileContents = File.ReadAllText(botTokenPath);
                return beanTokenFileContents;
            }
            catch (FileNotFoundException)
            {
                File.WriteAllText(botTokenPath, "INSERT BOT TOKEN HERE");
                string beanTokenFileLocation = Path.GetFullPath(botTokenPath);
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
                Log.Error($"Please configure the bean token file at {Path.GetFullPath(botTokenPath)}");
                return false;
            }
            return true;
        }
    }
}
