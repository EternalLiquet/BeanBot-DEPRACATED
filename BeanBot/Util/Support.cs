using Serilog;

namespace BeanBot.Util
{
    public static class Support
    {
        public static string BotToken { get; private set; }
        public static long ILServerId { get; private set; }

        public static void StartupOperations()
        {
            LogHandler.CreateLoggerConfiguration();
            DirectorySetup.MakeSureAllDirectoriesExist();
            OptionsSetup.MakeSureBeanTokenFileExists();
            OptionsSetup.MakeSureServerIDFileExists();
            BotToken = OptionsSetup.GetBeanTokenFromBeanTokenFile();
            ILServerId = OptionsSetup.GetServerIdFromServerIdFile();
            Log.Information("Startup Operations complete");
        }
    }
}
