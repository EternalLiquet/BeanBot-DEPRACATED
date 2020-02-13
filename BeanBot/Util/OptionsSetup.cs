using Serilog;
using System;
using System.IO;

namespace BeanBot.Util
{
    public static class OptionsSetup
    {
        public readonly static string botTokenDirectory = Path.Combine(DirectorySetup.botBaseDirectory, "BeanToken");
        public readonly static string botTokenFilePath = Path.Combine(OptionsSetup.botTokenDirectory, "beantoken.succsuccsucc");
        public readonly static string ilServerIdPath = Path.Combine(OptionsSetup.botTokenDirectory, "serverid.succsuccsucc");

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

        public static void MakeSureServerIDFileExists()
        {
            if (!File.Exists(ilServerIdPath))
            {
                Log.Error("Server ID file not found!");
                Log.Error($"Server ID file created automatically at: {Path.GetFullPath(ilServerIdPath)}");
                Console.Write("To configure your server ID, please copy and paste your bean token here\n> ");
                string serverIdInput = Console.ReadLine();
                File.WriteAllText(ilServerIdPath, serverIdInput);
            }
            else
            {
                Log.Information($"Bean token file found at: {ilServerIdPath}");
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

        public static long GetServerIdFromServerIdFile()
        {
            long serverId;
            try
            {
                Log.Information("Attempting to read token");
                serverId = long.Parse(File.ReadAllText(ilServerIdPath));
            }
            catch (FileNotFoundException e)
            {
                Log.Error(e.ToString());
                MakeSureServerIDFileExists();
                serverId = long.Parse(File.ReadAllText(ilServerIdPath));
            }
            catch (FormatException e)
            {
                Log.Error(e.ToString());
                Log.Error("Deleting current ID file as it is incorrect");
                File.Delete(ilServerIdPath);
                MakeSureServerIDFileExists();
                serverId = long.Parse(File.ReadAllText(ilServerIdPath));
            }
            catch (Exception e)
            {
                Log.Error(e.ToString());
                throw e;
            }
            return serverId;
        }
    }
}
