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
        public static string BotToken { get; private set; }

        public static void StartupOperations()
        {
            LoggerSetup.CreateLoggerConfiguration();
            DirectorySetup.MakeSureAllDirectoriesExist();
            TokenSetup.MakeSureBeanTokenFileExists();
            BotToken = TokenSetup.GetBeanTokenFromBeanTokenFile();
        }
    }
}
