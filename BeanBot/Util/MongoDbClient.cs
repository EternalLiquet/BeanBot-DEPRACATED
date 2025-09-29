using MongoDB.Driver;

using Serilog;
using System;

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
            if (client == null)
            {
                Log.Fatal("MongoDB Client failed to initialize. Check connection string.");
                throw new Exception("MongoDB Client failed to initialize. Check connection string.");
            }
            beanDatabase = client.GetDatabase("BeanBotDB");
            if (beanDatabase == null)
            {
                Log.Fatal("MongoDB Database failed to initialize. Check connection string.");
                throw new Exception("MongoDB Database failed to initialize. Check connection string.");
            }
            Log.Information("Database Connection complete");
        }
    }
}
