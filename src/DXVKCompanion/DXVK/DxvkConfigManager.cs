using System.IO;
using DXVKCompanion.Models;

namespace DXVKCompanion.DXVK
{
    public class DxvkConfigManager
    {
        public void WriteConfig(GameProfile profile)
        {
            try
            {
                string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDir))
                    return;

                string configPath = Path.Combine(gameDir, "dxvk.conf");

                using var writer = new StreamWriter(configPath, false);

                if (profile.HudEnabled)
                    writer.WriteLine("dxvk.hud = fps,devinfo");

                if (profile.FrameLimit > 0)
                    writer.WriteLine($"dxvk.maxFrameRate = {profile.FrameLimit}");
            }
            catch
            {
                // Ignore config write failures
            }
        }
    }
}
