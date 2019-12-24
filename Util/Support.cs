using Serilog;

namespace BeanBot.Util
{
    public static class Support
    {
        public static string BotToken { get; private set; }

        public static void StartupOperations()
        {
            LoggerSetup.CreateLoggerConfiguration();
            DirectorySetup.MakeSureAllDirectoriesExist();
            TokenSetup.MakeSureBeanTokenFileExists();
            BotToken = TokenSetup.GetBeanTokenFromBeanTokenFile();
            Log.Information("Startup Operations complete");
        }
    }
}
