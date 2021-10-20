using MongoDB.Driver;

using Serilog;

namespace BeanBot.Util
{
    public static class MongoDbClient
    {
        public static MongoClient client;
        public static IMongoDatabase beanDatabase;
        public static void InstantiateMongoDriver()
        {
            Log.Information("Instantiating Database Connection");
            client = new MongoClient(AppSettings.Settings["mongoConnectionString"]);
            beanDatabase = client.GetDatabase("BeanBotDB");
            Log.Information("Database Connection complete");
        }
    }
}
