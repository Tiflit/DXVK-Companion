using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using DXVKCompanion.Monitoring;

namespace DXVKCompanion.Models
{
    public class GameProfile
    {
        public string ExePath { get; set; }
        public string ExeName => Path.GetFileName(ExePath);

        public GraphicsApi Api { get; set; } = GraphicsApi.Unknown;
        public string Architecture { get; set; } = "x64"; // default

        public bool DxvkEnabled { get; set; } = false;
        public string LastVersion { get; set; } = "";

        public bool ShowHud { get; set; } = false;
        public int FrameLimit { get; set; } = 0;

        public List<string> InstalledDlls { get; set; } = new();

        public GameProfile(string exePath)
        {
            ExePath = exePath;
        }

        [JsonConstructor]
        public GameProfile(
            string exePath,
            GraphicsApi api,
            string architecture,
            bool dxvkEnabled,
            string lastVersion,
            bool showHud,
            int frameLimit,
            List<string> installedDlls)
        {
            ExePath = exePath;
            Api = api;
            Architecture = architecture;
            DxvkEnabled = dxvkEnabled;
            LastVersion = lastVersion;
            ShowHud = showHud;
            FrameLimit = frameLimit;
            InstalledDlls = installedDlls ?? new();
        }
    }
}
