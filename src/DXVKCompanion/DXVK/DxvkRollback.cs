using System.IO;
using System.Threading.Tasks;
using DXVKCompanion.Models;
using DXVKCompanion.Utils;

namespace DXVKCompanion.DXVK
{
    public class DxvkRollback
    {
        private readonly FileUtils _files;

        public DxvkRollback(FileUtils files)
        {
            _files = files;
        }

        public async Task<bool> RestoreOriginalDllsAsync(GameProfile profile)
        {
            try
            {
                string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDir))
                    return false;

                string d3d9 = Path.Combine(gameDir, "d3d9.dll");
                string d3d11 = Path.Combine(gameDir, "d3d11.dll");
                string dxgi = Path.Combine(gameDir, "dxgi.dll");

                string d3d9Bak = d3d9 + ".bak";
                string d3d11Bak = d3d11 + ".bak";
                string dxgiBak = dxgi + ".bak";

                if (File.Exists(d3d9Bak))
                {
                    bool ok = await _files.SafeReplaceAsync(d3d9, d3d9Bak);
                    if (!ok) return false;
                }

                if (File.Exists(d3d11Bak))
                {
                    bool ok = await _files.SafeReplaceAsync(d3d11, d3d11Bak);
                    if (!ok) return false;
                }

                if (File.Exists(dxgiBak))
                {
                    bool ok = await _files.SafeReplaceAsync(dxgi, dxgiBak);
                    if (!ok) return false;
                }

                string confPath = Path.Combine(gameDir, "dxvk.conf");
                if (File.Exists(confPath))
                    File.Delete(confPath);

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
