using System;
using System.IO;
using System.IO.Compression;
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

                    string dllDir = Path.Combine(Paths.DxvkDir, arch);
                    Directory.CreateDirectory(dllDir);

                    string dllPath = Path.Combine(dllDir, dllName);
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
                string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDir))
                    return false;

                string arch = profile.Architecture == "x32" ? "x32" : "x64";
                string dxvkArchDir = Path.Combine(Paths.DxvkDir, arch);

                if (!Directory.Exists(dxvkArchDir))
                {
                    bool ok = await DownloadAndExtractAsync(release);
                    if (!ok)
                        return false;
                }

                if (profile.Api == GraphicsApi.DX9)
                {
                    string srcD3d9 = Path.Combine(dxvkArchDir, "d3d9.dll");
                    string dstD3d9 = Path.Combine(gameDir, "d3d9.dll");

                    bool replaced = await _files.SafeReplaceWithBackupAsync(dstD3d9, srcD3d9);
                    if (!replaced)
                        return false;
                }
                else if (profile.Api == GraphicsApi.DX11 || profile.Api == GraphicsApi.ModernAPI || profile.Api == GraphicsApi.DX10)
                {
                    string srcD3d11 = Path.Combine(dxvkArchDir, "d3d11.dll");
                    string srcDxgi = Path.Combine(dxvkArchDir, "dxgi.dll");

                    string dstD3d11 = Path.Combine(gameDir, "d3d11.dll");
                    string dstDxgi = Path.Combine(gameDir, "dxgi.dll");

                    bool r1 = await _files.SafeReplaceWithBackupAsync(dstD3d11, srcD3d11);
                    if (!r1)
                        return false;

                    bool r2 = await _files.SafeReplaceWithBackupAsync(dstDxgi, srcDxgi);
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
