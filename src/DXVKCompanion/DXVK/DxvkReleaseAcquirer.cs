using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.Models;

namespace DXVKCompanion.DXVK;

/// <summary>User-triggered acquisition only. This service never reads or mutates game state.</summary>
public sealed class DxvkReleaseAcquirer
{
    public const int MaximumArchiveBytes = 128 * 1024 * 1024;
    internal const long MaximumExtractedBytes = 512L * 1024 * 1024;
    internal const long MaximumDllBytes = 128L * 1024 * 1024;
    internal const string ManifestName = "companion-release.json";
    internal const string MarkerName = ".companion-acquired";
    private static readonly string[] KnownDlls = { "d3d8.dll", "d3d9.dll", "d3d10.dll", "d3d10_1.dll", "d3d10core.dll", "d3d11.dll", "dxgi.dll" };
    private readonly DxvkGithubClient _github;
    private readonly string _root;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    internal Action<string>? BeforePublication { get; set; }

    public DxvkReleaseAcquirer(DxvkGithubClient github, string sourceRoot)
    {
        _github = github;
        _root = Path.GetFullPath(sourceRoot);
    }

    public sealed class FileIdentity
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
    }

    public sealed class Manifest
    {
        public int SchemaVersion { get; set; } = 1;
        public ReleaseInfo Origin { get; set; } = new();
        public string ArchiveSha256 { get; set; } = "";
        public List<FileIdentity> Files { get; set; } = new();
    }

    public sealed record AcquisitionResult(string Version, bool Reused, bool UpstreamDigestVerified, bool HasAcquisitionManifest = true);

    public Task<AcquisitionResult> AcquireAsync(string tag, CancellationToken cancellationToken = default) =>
        Task.Run(() => AcquireCoreAsync(tag, cancellationToken), cancellationToken);

    private async Task<AcquisitionResult> AcquireCoreAsync(string tag, CancellationToken cancellationToken)
    {
        if (!DxvkGithubClient.IsOfficialTag(tag)) throw new InvalidDataException("Invalid official release tag.");
        var gate = _gates.GetOrAdd(tag, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        string? staging = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRoot();
            string destination = Path.Combine(_root, tag);
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                if (IsCompleteLocalRelease(destination, tag, out bool upstream)) return new(tag, true, upstream, HasAcquisitionRecord(destination));
                throw new InvalidDataException("Existing release is incomplete or invalid. Retained files were not changed.");
            }
            // Callers supply a tag, never an asserted URL or provenance record.
            var release = await _github.FetchOfficialReleaseAsync(tag, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var token = timeout.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
            request.Headers.UserAgent.ParseAdd("DXVK-Companion/1.0");
            using var response = await _github.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long advertised && advertised != release.AssetSize)
                throw new InvalidDataException("Official asset size does not match metadata.");
            await using var source = await response.Content.ReadAsStreamAsync(token);
            byte[] archive = await DxvkGithubClient.ReadBoundedAsync(source, MaximumArchiveBytes, token);
            if (archive.LongLength != release.AssetSize) throw new InvalidDataException("Official asset is incomplete.");
            string archiveHash = Convert.ToHexString(SHA256.HashData(archive));
            if (release.AssetDigest != null && !string.Equals(release.AssetDigest[7..], archiveHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Official asset checksum mismatch.");

            EnsureRoot();
            // Recognition also scans immediate release directories. Keep all staging payloads one level deeper.
            string stagingRoot = Path.Combine(_root, ".companion-staging");
            Directory.CreateDirectory(stagingRoot);
            if (!IsPlain(stagingRoot)) throw new InvalidDataException("Acquisition staging links are not supported.");
            staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, MarkerName), "1");
            var manifest = new Manifest { Origin = release, ArchiveSha256 = archiveHash };
            using var compressed = new MemoryStream(archive, writable: false);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            byte[] tarBytes = await DxvkGithubClient.ReadBoundedAsync(gzip, (int)MaximumExtractedBytes, token);
            // GZipStream can tolerate a missing trailer. Check its CRC and length explicitly as well.
            if (archive.Length < 18 || BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(archive.Length - 4)) != (uint)tarBytes.Length ||
                BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(archive.Length - 8)) != Crc32(tarBytes, token))
                throw new InvalidDataException("Invalid or truncated gzip trailer.");
            using var tarSource = new MemoryStream(tarBytes, writable: false);
            using var reader = new TarReader(tarSource);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var architectures = new HashSet<string>(StringComparer.Ordinal);
            long extracted = 0;
            int entries = 0;
            TarEntry? entry;
            string prefix = $"dxvk-{tag[1..]}";
            while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken: token)) != null)
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 256) throw new InvalidDataException("Too many archive entries.");
                var parts = entry.Name.TrimEnd('/').Split('/');
                if (entry.Name.Contains('\\') || parts.Length < 1 || parts[0] != prefix ||
                    parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.Contains(':')))
                    throw new InvalidDataException("Unsafe archive path.");
                if (!seen.Add(entry.Name.TrimEnd('/'))) throw new InvalidDataException("Duplicate archive entry.");
                if (entry.EntryType == TarEntryType.Directory)
                {
                    if (parts.Length > 2 || (parts.Length == 2 && parts[1] != "x32" && parts[1] != "x64"))
                        throw new InvalidDataException("Unexpected archive directory.");
                    if (parts.Length == 2) architectures.Add(parts[1]);
                    continue;
                }
                if (entry.EntryType != TarEntryType.RegularFile && entry.EntryType != TarEntryType.V7RegularFile)
                    throw new InvalidDataException("Links and special archive entries are forbidden.");
                if (entry.Length < 0 || entry.Length > MaximumDllBytes || (extracted += entry.Length) > MaximumExtractedBytes)
                    throw new InvalidDataException("Extracted payload exceeds size limit.");
                // Known non-payload files are read/discarded, never written; consume through EOF to validate gzip.
                bool dll = parts.Length == 3 && (parts[1] == "x32" || parts[1] == "x64") && KnownDlls.Contains(parts[2], StringComparer.Ordinal);
                if (!dll && !(parts.Length == 2 && (parts[1] == "setup_dxvk.sh" || parts[1] == "LICENSE" || parts[1] == "README.md")))
                    throw new InvalidDataException("Unexpected archive file.");
                if (entry.DataStream == null || (dll && entry.Length == 0)) throw new InvalidDataException("Empty Windows payload.");
                byte[] data = await DxvkGithubClient.ReadBoundedAsync(entry.DataStream, (int)MaximumDllBytes, token);
                if (data.LongLength != entry.Length) throw new InvalidDataException("Truncated Windows payload.");
                if (!dll) continue;
                architectures.Add(parts[1]);
                Directory.CreateDirectory(Path.Combine(staging, parts[1]));
                string relative = parts[1] + "/" + parts[2];
                await File.WriteAllBytesAsync(Path.Combine(staging, parts[1], parts[2]), data, token);
                manifest.Files.Add(new FileIdentity { Path = relative, Size = data.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(data)) });
            }
            if (!HasCompleteArchitectures(manifest.Files.Select(f => f.Path), architectures))
                throw new InvalidDataException("Missing required Windows payload files.");
            manifest.Files = manifest.Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            await File.WriteAllTextAsync(Path.Combine(staging, ManifestName), JsonSerializer.Serialize(manifest), token);
            if (!ValidateManifest(staging, tag, out _)) throw new InvalidDataException("Staged release verification failed.");
            BeforePublication?.Invoke(staging);
            token.ThrowIfCancellationRequested();
            EnsureRoot();
            if (!IsPlain(stagingRoot) || !IsPlain(staging)) throw new InvalidDataException("Acquisition staging changed before publication.");
            // Same-volume rename publishes marker, manifest and DLLs together. Never overwrite an existing version.
            try { Directory.Move(staging, destination); }
            catch (IOException) when (IsCompleteLocalRelease(destination, tag, out _))
            {
                return new(tag, true, ValidateManifest(destination, tag, out bool verified) && verified, HasAcquisitionRecord(destination));
            }
            staging = null;
            return new(tag, false, release.AssetDigest != null);
        }
        finally
        {
            if (staging != null) TryDeleteOwnedStaging(staging);
            gate.Release();
        }
    }

    private void EnsureRoot()
    {
        Directory.CreateDirectory(_root);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Source library links are not supported for acquisition.");
    }

    private static bool IsPlain(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static bool HasCompleteArchitectures(IEnumerable<string> paths, IEnumerable<string> architectures)
    {
        var files = paths.ToHashSet(StringComparer.Ordinal);
        var arches = architectures.ToArray();
        return arches.Length > 0 && arches.All(arch =>
            // Older releases may predate D3D9. A D3D11 payload always requires its DXGI partner.
            (files.Contains(arch + "/d3d11.dll") && files.Contains(arch + "/dxgi.dll") ||
             files.Contains(arch + "/d3d9.dll") && !files.Contains(arch + "/d3d11.dll") && !files.Contains(arch + "/dxgi.dll")) &&
            (!files.Contains(arch + "/d3d10core.dll") || files.Contains(arch + "/dxgi.dll")));
    }

    internal static bool HasAcquisitionRecord(string directory) =>
        EntryExists(Path.Combine(directory, MarkerName)) || EntryExists(Path.Combine(directory, ManifestName));

    private static bool EntryExists(string path)
    {
        // File.Exists suppresses access failures and can miss dangling links. Neither implies a legacy source.
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static bool ValidateManifest(string directory, string tag, out bool upstreamVerified)
    {
        upstreamVerified = false;
        try
        {
            string marker = Path.Combine(directory, MarkerName);
            string path = Path.Combine(directory, ManifestName);
            if (!IsPlain(directory) || !File.Exists(marker) || !IsPlain(marker) || new FileInfo(marker).Length != 1 ||
                File.ReadAllText(marker) != "1" || !File.Exists(path) || !IsPlain(path) || new FileInfo(path).Length > 32768) return false;
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(path));
            if (manifest == null || manifest.SchemaVersion != 1 || manifest.Origin == null ||
                manifest.Origin.Version != tag || !DxvkGithubClient.IsOfficialAsset(manifest.Origin) ||
                !IsHash(manifest.ArchiveSha256) || manifest.Files == null || manifest.Files.Count is < 1 or > 14) return false;
            if (manifest.Origin.AssetDigest != null && !string.Equals(manifest.Origin.AssetDigest[7..], manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase)) return false;
            var paths = new HashSet<string>(StringComparer.Ordinal);
            long totalSize = 0;
            foreach (var file in manifest.Files)
            {
                if (file == null || file.Path == null) return false;
                string[] parts = file.Path.Split('/');
                if (parts.Length != 2 || (parts[0] != "x32" && parts[0] != "x64") || !KnownDlls.Contains(parts[1], StringComparer.Ordinal) ||
                    !paths.Add(file.Path) || file.Size <= 0 || file.Size > MaximumDllBytes ||
                    (totalSize += file.Size) > MaximumExtractedBytes || !IsHash(file.Sha256)) return false;
                string archDir = Path.Combine(directory, parts[0]);
                string dll = Path.Combine(archDir, parts[1]);
                if (!IsPlain(archDir) || !File.Exists(dll) || !IsPlain(dll) || new FileInfo(dll).Length != file.Size) return false;
                using var stream = File.OpenRead(dll);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            var arches = paths.Select(p => p.Split('/')[0]).Distinct().ToArray();
            if (!HasCompleteArchitectures(paths, arches)) return false;
            // Additional or missing DLLs cannot be hidden by a narrower manifest.
            foreach (var arch in new[] { "x32", "x64" })
            {
                string archDir = Path.Combine(directory, arch);
                if (!Directory.Exists(archDir)) continue;
                if (!IsPlain(archDir)) return false;
                foreach (string file in Directory.EnumerateFileSystemEntries(archDir))
                    if (!paths.Contains(arch + "/" + Path.GetFileName(file)) || !File.Exists(file)) return false;
            }
            upstreamVerified = manifest.Origin.AssetDigest != null;
            return true;
        }
        catch { return false; }
    }

    private static bool IsHash(string? value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static uint Crc32(byte[] data, CancellationToken token)
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint value = n;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0u : 0xedb88320u);
            table[n] = value;
        }
        uint crc = 0xffffffff;
        for (int n = 0; n < data.Length; n++)
        {
            if ((n & 0xfffff) == 0) token.ThrowIfCancellationRequested();
            crc = table[(crc ^ data[n]) & 255] ^ (crc >> 8);
        }
        return ~crc;
    }

    private static bool IsCompleteLocalRelease(string directory, string tag, out bool upstreamVerified)
    {
        upstreamVerified = false;
        try
        {
            if (!Directory.Exists(directory) || !IsPlain(directory)) return false;
            if (HasAcquisitionRecord(directory)) return ValidateManifest(directory, tag, out upstreamVerified);
            // Legacy manual libraries remain usable, without claiming authenticated acquisition.
            var files = new List<string>();
            var arches = new List<string>();
            foreach (string arch in new[] { "x32", "x64" })
            {
                string archDir = Path.Combine(directory, arch);
                if (!Directory.Exists(archDir)) continue;
                if (!IsPlain(archDir)) return false;
                arches.Add(arch);
                foreach (string dll in KnownDlls)
                {
                    string path = Path.Combine(archDir, dll);
                    if (!File.Exists(path)) continue;
                    if (!IsPlain(path) || new FileInfo(path).Length == 0) return false;
                    files.Add(arch + "/" + dll);
                }
            }
            return HasCompleteArchitectures(files, arches);
        }
        catch { return false; }
    }

    private void TryDeleteOwnedStaging(string staging)
    {
        // Only this operation's random staging directory is eligible. Never traverse a substituted link.
        try
        {
            if (!IsPlain(_root) || !IsPlain(Path.GetDirectoryName(staging)!) || !IsPlain(staging)) return;
            foreach (string child in Directory.EnumerateFileSystemEntries(staging))
            {
                if (!IsPlain(child)) return;
                if (Directory.Exists(child) && Directory.EnumerateFileSystemEntries(child).Any(p => !IsPlain(p) || Directory.Exists(p))) return;
            }
            Directory.Delete(staging, recursive: true);
        }
        catch { /* A failed cleanup stays hidden and cannot qualify as a release. */ }
    }
}
