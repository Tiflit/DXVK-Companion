using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Formats.Tar;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;

namespace DXVKCompanion.DXVK
{
    public class DxvkInstaller
    {
        private readonly FileUtils _files;
        private readonly HttpClient _httpClient;

        public DxvkInstaller(FileUtils files, HttpClient httpClient)
        {
            _files = files;
            _httpClient = httpClient;
        }

        private static string SanitizeVersion(string version)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                version = version.Replace(c, '_');
            return version;
        }

        private async Task<bool> DownloadAndExtractAsync(ReleaseInfo release)
        {
            if (string.IsNullOrWhiteSpace(release.DownloadUrl))
                return false;

            try
            {
                Paths.EnsureDirectories();

                var data = await _httpClient.GetByteArrayAsync(release.DownloadUrl);

                using var gzStream = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
                using var tar = new TarReader(gzStream);

                string versionDir = Path.Combine(Paths.DxvkDir, SanitizeVersion(release.Version));

                TarEntry? entry;
                while ((entry = tar.GetNextEntry()) != null)
                {
                    if (entry.EntryType != TarEntryType.RegularFile)
                        continue;

                    string name = entry.Name.Replace('\\', '/').ToLowerInvariant();

                    bool isDx9 = name.EndsWith("x32/d3d9.dll") || name.EndsWith("x64/d3d9.dll");
                    bool isDx11 = name.EndsWith("x32/d3d11.dll") || name.EndsWith("x64/d3d11.dll");
                    bool isDxgi = name.EndsWith("x32/dxgi.dll") || name.EndsWith("x64/dxgi.dll");

                    if (!isDx9 && !isDx11 && !isDxgi)
                        continue;

                    using var ms = new MemoryStream();
                    entry.DataStream?.CopyTo(ms);
                    var bytes = ms.ToArray();

                    string arch = name.Contains("/x32/") ? "x32" : "x64";
                    string dllName = Path.GetFileName(name);

                    string dllDir = Path.Combine(versionDir, arch);
                    Directory.CreateDirectory(dllDir);

                    _files.WriteBytes(Path.Combine(dllDir, dllName), bytes);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> ApplyToGameAsync(GameProfile profile, ReleaseInfo release)
        {
            try
            {
                string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDir))
                    return false;

                string arch = profile.Architecture == "x32" ? "x32" : "x64";
                string versionDir = Path.Combine(Paths.DxvkDir, SanitizeVersion(release.Version));
                string dxvkArchDir = Path.Combine(versionDir, arch);

                // Re-download whenever THIS SPECIFIC VERSION isn't already extracted locally.
                // Previously this checked only whether the shared DXVK folder existed at all,
                // which meant "Update" never actually fetched newer DLLs after the first install.
                if (!Directory.Exists(dxvkArchDir) || !Directory.EnumerateFiles(dxvkArchDir).Any())
                {
                    bool ok = await DownloadAndExtractAsync(release);
                    if (!ok)
                        return false;
                }

                if (profile.Api == GraphicsApi.DX9)
                {
                    bool replaced = await _files.SafeReplaceWithBackupAsync(
                        Path.Combine(gameDir, "d3d9.dll"),
                        Path.Combine(dxvkArchDir, "d3d9.dll"));
                    if (!replaced)
                        return false;
                }
                else if (profile.Api == GraphicsApi.DX11 || profile.Api == GraphicsApi.ModernAPI || profile.Api == GraphicsApi.DX10)
                {
                    bool r1 = await _files.SafeReplaceWithBackupAsync(
                        Path.Combine(gameDir, "d3d11.dll"),
                        Path.Combine(dxvkArchDir, "d3d11.dll"));
                    if (!r1)
                        return false;

                    bool r2 = await _files.SafeReplaceWithBackupAsync(
                        Path.Combine(gameDir, "dxgi.dll"),
                        Path.Combine(dxvkArchDir, "dxgi.dll"));
                    if (!r2)
                        return false;
                }
                else
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
