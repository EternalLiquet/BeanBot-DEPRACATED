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
        public readonly static string botToken;

        public static string BotToken => botToken;

        public static void StartupOperations()
        {
            LoggerSetup.CreateLoggerConfiguration();
            DirectorySetup.MakeSureAllDirectoriesExist();
        }
    }
}
