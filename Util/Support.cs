using System;
using System.Collections.Generic;
using System.Text;

using Serilog;

namespace BeanBot.Util
{
    public static class Support
    {
        static string botToken;

        public static void StartupOperations()
        {
            CreateLoggerConfiguration();
        }

        private static void CreateLoggerConfiguration()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File("logs\\BeanBotLogs.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();
        }
    }
}
