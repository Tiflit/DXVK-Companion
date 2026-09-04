using System.Text.Json.Serialization;

namespace DXVKCompanion.Models
{
    public class GameProfile
    {
        public string ExePath { get; set; }
        public string ExeName { get; set; }

        // Graphics API classification (DX9, DX11, DX12, Vulkan, etc.)
        public GraphicsApi Api { get; set; } = GraphicsApi.Unknown;

        // Architecture: x32 or x64
        public string Architecture { get; set; } = "Unknown";

        // DXVK state
        public bool DxvkEnabled { get; set; } = false;

        // Standardized property name
        public string? DxvkVersion { get; set; }

        // HUD toggle
        public bool HudEnabled { get; set; } = false;

        // Frame limiter
        public int FrameLimit { get; set; } = 0;

        public GameProfile(string exePath)
        {
            ExePath = exePath;
            ExeName = System.IO.Path.GetFileName(exePath);
        }
    }
}
