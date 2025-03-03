using Newtonsoft.Json;
using System;
using System.IO;

namespace MyriadMusicTagger
{
    public static class SettingsManager
    {
        private const string SettingsFileName = "settings.json";

        public static AppSettings LoadSettings()
        {
            if (File.Exists(SettingsFileName))
            {
                var json = File.ReadAllText(SettingsFileName);
                return JsonConvert.DeserializeObject<AppSettings>(json) ?? CreateNewSettings();
            }
            
            return CreateNewSettings();
        }

        private static AppSettings CreateNewSettings()
        {
            Console.WriteLine("Settings file not found. Please enter the following information:");
            
            var settings = new AppSettings();
            
            Console.Write("AcoustID Client Key: ");
            settings.AcoustIDClientKey = Console.ReadLine() ?? string.Empty;
            
            Console.Write("Playout Write Key: ");
            settings.PlayoutWriteKey = Console.ReadLine() ?? string.Empty;
            
            Console.Write("Playout Read Key: ");
            settings.PlayoutReadKey = Console.ReadLine() ?? string.Empty;
            
            Console.Write("Delay Between Requests (seconds, default: 3.0): ");
            var delayInput = Console.ReadLine();
            if (!string.IsNullOrEmpty(delayInput) && double.TryParse(delayInput, out double delay))
            {
                settings.DelayBetweenRequests = delay;
            }
            
            Console.Write("Playout API URL (default: http://localhost:9180/BrMyriadPlayout/v6): ");
            var apiUrl = Console.ReadLine();
            settings.PlayoutApiUrl = string.IsNullOrEmpty(apiUrl) ? "http://localhost:9180/BrMyriadPlayout/v6" : apiUrl;
            
            SaveSettings(settings);
            return settings;
        }

        private static void SaveSettings(AppSettings settings)
        {
            var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(SettingsFileName, json);
            Console.WriteLine("Settings saved to " + SettingsFileName);
        }
    }
}