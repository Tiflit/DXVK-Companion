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
                string gameDir = Path.GetDirectoryName(profile.ExecutablePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDir))
                    return false;

                string d3d11 = Path.Combine(gameDir, "d3d11.dll");
                string dxgi = Path.Combine(gameDir, "dxgi.dll");

                string d3d11Bak = d3d11 + ".bak";
                string dxgiBak = dxgi + ".bak";

                if (File.Exists(d3d11Bak))
                    await _files.SafeReplaceAsync(d3d11, d3d11Bak);

                if (File.Exists(dxgiBak))
                    await _files.SafeReplaceAsync(dxgi, dxgiBak);

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
