using Serilog;

namespace BeanBot.Util
{
    public static class Support
    {
        public static void StartupOperations()
        {
            DirectorySetup.MakeSureAllDirectoriesExist();
            LogHandler.CreateLoggerConfiguration();
            AppSettings.LoadFromEnvironment();
            MongoDbClient.InstantiateMongoDriver();
            Log.Information("Startup Operations complete");
        }
    }
}
