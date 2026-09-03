using System.IO;
using DXVKCompanion.Models;

namespace DXVKCompanion.DXVK
{
    public class DxvkConfigManager
    {
        public void WriteLocalConfig(string gameDir, GameProfile profile)
        {
            string path = Path.Combine(gameDir, "dxvk.conf");

            using var writer = new StreamWriter(path);

            if (profile.ShowHud)
                writer.WriteLine("dxvk.hud = fps,version");

            if (profile.FrameLimit > 0)
                writer.WriteLine($"dxvk.maxFrameRate = {profile.FrameLimit}");
        }
    }
}
