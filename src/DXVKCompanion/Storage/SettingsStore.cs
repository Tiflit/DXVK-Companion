using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DXVKCompanion.Models;

namespace DXVKCompanion.Storage
{
    public class SettingsStore
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public GlobalManagementPolicy GlobalPolicy { get; set; } = GlobalManagementPolicy.Manual;

        public bool AutoEnableDxvkForNewGames
        {
            get => GlobalPolicy == GlobalManagementPolicy.Automated;
            set => GlobalPolicy = value ? GlobalManagementPolicy.Automated : GlobalManagementPolicy.Manual;
        }

        public bool LaunchOnStartup { get; set; } = false;

        [JsonIgnore]
        public string? CustomSettingsPath { get; set; }

        [JsonIgnore]
        public Action? SimulatedTempWriteFailure { get; set; }

        [JsonIgnore]
        public Action? SimulatedCommitFailure { get; set; }

        [JsonIgnore]
        public string SettingsFilePath =>
            CustomSettingsPath ?? Path.Combine(Paths.Root, "settings.json");

        public static SettingsStore Load(string? settingsPath = null)
        {
            string path = settingsPath ?? Path.Combine(Paths.Root, "settings.json");
            try
            {
                if (!File.Exists(path))
                    return new SettingsStore { CustomSettingsPath = settingsPath };

                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<SettingsStore>(json);

                if (settings != null)
                {
                    settings.CustomSettingsPath = settingsPath;
                    return settings;
                }

                return new SettingsStore { CustomSettingsPath = settingsPath };
            }
            catch
            {
                return new SettingsStore { CustomSettingsPath = settingsPath };
            }
        }

        public bool Save(out string? errorMessage)
        {
            errorMessage = null;
            string targetPath = SettingsFilePath;
            string? targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                try
                {
                    Directory.CreateDirectory(targetDir);
                }
                catch (Exception ex)
                {
                    errorMessage = $"Failed to save settings ({ex.GetType().Name}).";
                    return false;
                }
            }

            string tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                SimulatedTempWriteFailure?.Invoke();

                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(tempPath, json);

                SimulatedCommitFailure?.Invoke();

                if (File.Exists(targetPath))
                {
                    File.Move(tempPath, targetPath, overwrite: true);
                }
                else
                {
                    File.Move(tempPath, targetPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        // Clean only operation-owned temporary file when safe
                    }
                }

                errorMessage = $"Failed to save settings ({ex.GetType().Name}).";
                return false;
            }
        }

        public void Save()
        {
            Save(out _);
        }

        public SettingsStore Clone()
        {
            return new SettingsStore
            {
                GlobalPolicy = this.GlobalPolicy,
                LaunchOnStartup = this.LaunchOnStartup,
                CustomSettingsPath = this.CustomSettingsPath,
                SimulatedTempWriteFailure = this.SimulatedTempWriteFailure,
                SimulatedCommitFailure = this.SimulatedCommitFailure
            };
        }
    }
}
