using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Formats.Tar;
using DXVKCompanion.Models;
using DXVKCompanion.Utils;

namespace DXVKCompanion.DXVK
{
    public class DxvkInstaller
    {
        private readonly FileUtils _files;

        public DxvkInstaller(FileUtils files)
        {
            _files = files;
        }

        public async Task<bool> DownloadAndExtractAsync(ReleaseInfo release, string targetDir)
        {
            if (string.IsNullOrWhiteSpace(release.DownloadUrl))
                return false;

            try
            {
                using var client = new HttpClient();
                var data = await client.GetByteArrayAsync(release.DownloadUrl);

                using var gzStream = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
                using var tar = new TarReader(gzStream);

                TarEntry? entry;
                while ((entry = tar.GetNextEntry()) != null)
                {
                    if (entry.EntryType != TarEntryType.RegularFile)
                        continue;

                    string name = entry.Name.Replace('\\', '/');

                    if (!name.EndsWith("x64/d3d11.dll", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith("x64/dxgi.dll", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith("x32/d3d11.dll", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith("x32/dxgi.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using var ms = new MemoryStream();
                    entry.DataStream?.CopyTo(ms);
                    var bytes = ms.ToArray();

                    string arch = name.Contains("/x32/") ? "x32" : "x64";
                    string dllName = name.EndsWith("d3d11.dll", StringComparison.OrdinalIgnoreCase) ? "d3d11.dll" : "dxgi.dll";

                    string dllPath = Path.Combine(targetDir, arch, dllName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);

                    _files.WriteBytes(dllPath, bytes);
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
                string gameDir = Path.GetDirectoryName(profile.ExecutablePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDir))
                    return false;

                string arch = profile.Architecture == "x32" ? "x32" : "x64";
                string dxvkDir = Path.Combine(gameDir, "DXVK", arch);

                if (!Directory.Exists(dxvkDir))
                {
                    bool ok = await DownloadAndExtractAsync(release, Path.Combine(gameDir, "DXVK"));
                    if (!ok)
                        return false;
                }

                string srcD3d11 = Path.Combine(dxvkDir, "d3d11.dll");
                string srcDxgi = Path.Combine(dxvkDir, "dxgi.dll");

                string dstD3d11 = Path.Combine(gameDir, "d3d11.dll");
                string dstDxgi = Path.Combine(gameDir, "dxgi.dll");

                await _files.SafeReplaceWithBackupAsync(dstD3d11, srcD3d11);
                await _files.SafeReplaceWithBackupAsync(dstDxgi, srcDxgi);

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
