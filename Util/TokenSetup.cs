using Serilog;
using System;
using System.IO;

namespace BeanBot.Util
{
    public static class TokenSetup
    {
        public readonly static string botTokenDirectory = Path.Combine(DirectorySetup.botBaseDirectory, "BeanToken");
        public readonly static string botTokenFilePath = Path.Combine(TokenSetup.botTokenDirectory, "beantoken.succsuccsucc");

        public static void MakeSureBeanTokenFileExists()
        {
            if (!File.Exists(botTokenFilePath))
            {
                Log.Error("Bean Token file not found!");
                Log.Error($"Bean Token file created automatically at: {Path.GetFullPath(botTokenFilePath)}");
                Console.Write("To configure your bean token, please copy and paste your bean token here\n> ");
                string beanTokenInput = Console.ReadLine();
                File.WriteAllText(botTokenFilePath, beanTokenInput);
            }
            else
            {
                Log.Information($"Bean token file found at: {botTokenFilePath}");
            }
        }

        public static string GetBeanTokenFromBeanTokenFile()
        {
            string beanTokenFileContent;
            try
            {
                Log.Information("Attempting to read token");
                beanTokenFileContent = File.ReadAllText(botTokenFilePath);
            }
            catch (FileNotFoundException e)
            {
                Log.Error(e.ToString());
                MakeSureBeanTokenFileExists();
                beanTokenFileContent = File.ReadAllText(botTokenFilePath);
            }
            catch (Exception e)
            {
                Log.Error(e.ToString());
                throw e;
            }
            return beanTokenFileContent;
        }
    }
}
