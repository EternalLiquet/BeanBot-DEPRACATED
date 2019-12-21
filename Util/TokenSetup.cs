using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BeanBot.Util
{
    public static class TokenSetup
    {
        public readonly static string botTokenDirectory = DirectorySetup.botBaseDirectory + "BeanToken\\";
        public readonly static string botTokenFilePath = TokenSetup.botTokenDirectory + "beantoken.succsuccsucc";

        public static void MakeSureBeanTokenFileExists()
        {
            if (!File.Exists(botTokenFilePath))
            {
                string beanTokenFileLocation = Path.GetFullPath(botTokenFilePath);
                Log.Error("Bean Token file not found!");
                Log.Error($"Bean Token file created automatically at: {beanTokenFileLocation}");
                Console.WriteLine("To configure your bean token, please copy and paste your bean token here\n> ");
                string beanTokenInput = Console.ReadLine();
                File.WriteAllText(botTokenFilePath, beanTokenInput);
            }
        }

        public static string GetBeanTokenFromBeanTokenFile()
        {
            string beanTokenFileContent;
            try
            {
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
