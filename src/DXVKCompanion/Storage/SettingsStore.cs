using System.IO;
using System.Text.Json;

namespace DXVKCompanion.Storage
{
    public class SettingsStore
    {
        private const string SettingsFileName = "settings.json";

        public bool AutoEnableDxvkForNewGames { get; set; } = false;
        public bool LaunchOnStartup { get; set; } = false;

        private string SettingsFilePath => Path.Combine(Paths.Root, SettingsFileName);

        public SettingsStore()
        {
            Load();
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                    return;

                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<SettingsStore>(json);

                if (settings == null)
                    return;

                AutoEnableDxvkForNewGames = settings.AutoEnableDxvkForNewGames;
                LaunchOnStartup = settings.LaunchOnStartup;
            }
            catch
            {
                // Ignore corrupted settings
            }
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // Ignore write failures
            }
        }
    }
}
