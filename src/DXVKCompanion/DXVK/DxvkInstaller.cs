using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Formats.Tar;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;

namespace DXVKCompanion.DXVK
{
    public class DxvkInstaller
    {
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

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

                using var cts = new CancellationTokenSource(DownloadTimeout);
                var data = await _httpClient.GetByteArrayAsync(release.DownloadUrl, cts.Token);

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

                    string arch = name.Contains("x32/") ? "x32" : "x64";
                    string dllName = Path.GetFileName(name);

                    string dllDir = Path.Combine(versionDir, arch);
                    Directory.CreateDirectory(dllDir);

                    _files.WriteBytes(Path.Combine(dllDir, dllName), bytes);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                Logger.Log($"DxvkInstaller: download of {release.Version} timed out after {DownloadTimeout.TotalSeconds}s.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkInstaller: failed to download/extract {release.Version}: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ApplyToGameAsync(GameProfile profile, ReleaseInfo release)
        {
            try
            {
                string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDir))
                {
                    Logger.Log($"DxvkInstaller: could not determine game directory for {profile.ExePath}.");
                    return false;
                }

                string arch = profile.Architecture == "x32" ? "x32" : "x64";
                string versionDir = Path.Combine(Paths.DxvkDir, SanitizeVersion(release.Version));
                string dxvkArchDir = Path.Combine(versionDir, arch);

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
                    {
                        Logger.Log($"DxvkInstaller: failed to replace d3d9.dll for {profile.ExeName}.");
                        return false;
                    }
                }
                else if (profile.Api == GraphicsApi.DX11 || profile.Api == GraphicsApi.ModernAPI || profile.Api == GraphicsApi.DX10)
                {
                    bool r1 = await _files.SafeReplaceWithBackupAsync(
                        Path.Combine(gameDir, "d3d11.dll"),
                        Path.Combine(dxvkArchDir, "d3d11.dll"));
                    if (!r1)
                    {
                        Logger.Log($"DxvkInstaller: failed to replace d3d11.dll for {profile.ExeName}.");
                        return false;
                    }

                    bool r2 = await _files.SafeReplaceWithBackupAsync(
                        Path.Combine(gameDir, "dxgi.dll"),
                        Path.Combine(dxvkArchDir, "dxgi.dll"));
                    if (!r2)
                    {
                        Logger.Log($"DxvkInstaller: failed to replace dxgi.dll for {profile.ExeName}.");
                        return false;
                    }
                }
                else
                {
                    Logger.Log($"DxvkInstaller: {profile.ExeName} has an unsupported API ({profile.Api}); skipping.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkInstaller: unexpected error applying DXVK to {profile.ExeName}: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }
    }
}
