using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BeanBot.Util
{
    public static class AppSettings
    {
        public readonly static string settingsFileDirectory = Path.Combine(DirectorySetup.botBaseDirectory, "Settings");
        public readonly static string settingsFilePath = Path.Combine(AppSettings.settingsFileDirectory, "beanSettings.json");

        public static Dictionary<string, string> Settings { get; private set; }

        public static void MakeSureSettingsJsonExists()
        {
            if (!File.Exists(settingsFilePath))
            {
                Log.Error("Settings file not found");
                Log.Error($"Settings file created automatically at: {Path.GetFullPath(settingsFilePath)}");
                Log.Information("Starting settings file creation process");
                string jsonStringSettings = CreateNewSettings();
                File.WriteAllText(settingsFilePath, jsonStringSettings);
            }
            else
            {
                Log.Information($"App settings file found at: {settingsFilePath}");
            }
        }

        public static void ReadSettingsFromFile()
        {
            try
            {
                string jsonSettings = File.ReadAllText(settingsFilePath);
                Settings = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonSettings);
            }
            catch (FileNotFoundException e)
            {
                Log.Error($"File not found at {Path.GetFullPath(settingsFilePath)}, redirecting to creation method");
                MakeSureSettingsJsonExists();
            }
            catch (Exception e)
            {
                Log.Error($"{e.Message}");
            }
        }

        public static void FixTokenFile()
        {
            Console.Write("Please enter a valid bot token\n>");
            string token = Console.ReadLine();
            Settings["botToken"] = token;
        }

        private static string CreateNewSettings()
        {
            return JsonConvert.SerializeObject(CreateSettingsDictionary(), Formatting.Indented);
        }

        private static Dictionary<string, string> CreateSettingsDictionary()
        {
            bool repeat = true;
            Dictionary<string, string> dictToJson = new Dictionary<string, string>();
            while (repeat)
            {
                Console.Write("Please enter the setting key, or enter \"break\" to quit\n> ");
                string arg1 = Console.ReadLine();
                Console.Write("Please enter the setting value\n> ");
                string arg2 = Console.ReadLine();
                if (!arg1.Equals("break"))
                {
                    try
                    {
                        dictToJson.Add(arg1, arg2);
                    }
                    catch (ArgumentNullException e)
                    {
                        Log.Error($"Parameter Name Null: {e.Message}");
                    }
                    catch (ArgumentException e)
                    {
                        Log.Error($"Parameter Not Acceptable: {e.ParamName}");
                    }
                }
                else
                {
                    repeat = false;
                }
            }
            return dictToJson;
        }
    }
}
