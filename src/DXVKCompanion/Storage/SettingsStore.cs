using System.IO;
using System.Text.Json;

namespace DXVKCompanion.Storage
{
    public class SettingsStore
    {
        public bool AutoEnableDxvkForNewGames { get; set; } = false;
        public bool LaunchOnStartup { get; set; } = false;

        private static string SettingsPath =>
            Path.Combine(Paths.Root, "settings.json");

        public static SettingsStore Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new SettingsStore();

                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<SettingsStore>(json);

                return settings ?? new SettingsStore();
            }
            catch
            {
                return new SettingsStore();
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

                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // Ignore write failures
            }
        }
    }
}
