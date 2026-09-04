using System;
using System.IO;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;

namespace DXVKCompanion.DXVK
{
    public class DxvkConfigManager
    {
        private readonly Paths _paths;

        public DxvkConfigManager()
        {
            _paths = new Paths();
        }

        public void WriteConfig(GameProfile profile)
        {
            try
            {
                string gameDir = Path.GetDirectoryName(profile.ExecutablePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDir))
                    return;

                string configPath = Path.Combine(gameDir, "dxvk.conf");

                using var writer = new StreamWriter(configPath, false);

                if (profile.HudEnabled)
                    writer.WriteLine("dxvk.hud = fps,devinfo");

                if (profile.FrameLimit > 0)
                    writer.WriteLine($"dxvk.maxfps = {profile.FrameLimit}");
            }
            catch
            {
                // Ignore config write failures
            }
        }
    }
}
