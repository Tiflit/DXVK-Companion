using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Formats.Tar;
using DXVKCompanion.Models;

namespace DXVKCompanion.DXVK
{
    public class DxvkGithubClient
    {
        private readonly HttpClient _http = new();

        public ReleaseInfo FetchLatestRelease()
        {
            var json = _http.GetStringAsync(
                "https://api.github.com/repos/doitsujin/dxvk/releases/latest"
            ).Result;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string version = root.GetProperty("tag_name").GetString()!;
            string assetUrl = root.GetProperty("assets")[0].GetProperty("browser_download_url").GetString()!;

            var stream = _http.GetStreamAsync(assetUrl).Result;

            // Extract in memory
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);

            var release = new ReleaseInfo(version);

            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                if (entry.EntryType != TarEntryType.RegularFile)
                    continue;

                string name = entry.Name.ToLower();

                if (name.Contains("/x32/") || name.Contains("/x64/"))
                {
                    release.AddDllFromTar(name, entry.DataStream);
                }
            }

            return release;
        }
    }
}
