using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Formats.Tar;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;

namespace DXVKCompanion.DXVK
{
    public class DxvkInstaller
    {
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

        private readonly FileUtils _files;
        private readonly HttpClient _httpClient;
        private readonly MultiFileTransactionEngine _transactionEngine;
        private readonly GameLibraryStore _gameLibraryStore;
        private readonly string _dxvkSourceDir;

        public DxvkInstaller(
            HttpClient httpClient,
            MultiFileTransactionEngine? transactionEngine = null,
            GameLibraryStore? gameLibraryStore = null,
            string? dxvkSourceDir = null,
            FileUtils? files = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _transactionEngine = transactionEngine ?? new MultiFileTransactionEngine(GameLibraryPaths.BackupsDir);
            _gameLibraryStore = gameLibraryStore ?? new GameLibraryStore();
            _dxvkSourceDir = dxvkSourceDir ?? Paths.DxvkDir;
            _files = files ?? new FileUtils();
        }

        public DxvkInstaller(FileUtils files, HttpClient httpClient)
            : this(httpClient, null, null, null, files)
        {
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

                string versionDir = Path.Combine(_dxvkSourceDir, SanitizeVersion(release.Version));

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
                if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                {
                    Logger.Log($"DxvkInstaller: could not determine valid game directory for {profile.ExePath}.");
                    return false;
                }

                string arch = string.Equals(profile.Architecture, "x32", StringComparison.OrdinalIgnoreCase) ? "x32" : "x64";
                string versionDir = Path.Combine(_dxvkSourceDir, SanitizeVersion(release.Version));
                string dxvkArchDir = Path.Combine(versionDir, arch);

                if (!Directory.Exists(dxvkArchDir) || !Directory.EnumerateFiles(dxvkArchDir).Any())
                {
                    bool ok = await DownloadAndExtractAsync(release);
                    if (!ok)
                        return false;
                }

                string[] dllsToDeploy;
                if (profile.Api == GraphicsApi.DX9)
                {
                    dllsToDeploy = new[] { "d3d9.dll" };
                }
                else if (profile.Api == GraphicsApi.DX11 || profile.Api == GraphicsApi.ModernAPI || profile.Api == GraphicsApi.DX10)
                {
                    dllsToDeploy = new[] { "d3d11.dll", "dxgi.dll" };
                }
                else
                {
                    Logger.Log($"DxvkInstaller: {profile.ExeName} has an unsupported API ({profile.Api}); skipping.");
                    return false;
                }

                foreach (var dllName in dllsToDeploy)
                {
                    var src = Path.Combine(dxvkArchDir, dllName);
                    if (!File.Exists(src))
                    {
                        Logger.Log($"DxvkInstaller: extracted DXVK source file missing: {src}");
                        return false;
                    }
                }

                var installation = _gameLibraryStore.GetOrCreateInstallation(gameDir, Path.GetFileNameWithoutExtension(profile.ExePath));
                var executable = installation.GetOrAddExecutable(Path.GetFileName(profile.ExePath), profile.ExeName);
                executable.LastKnownApi = profile.Api;
                executable.LastKnownArchitecture = arch;

                var filesToProcess = new List<MultiFileTransactionFile>();

                foreach (var dllName in dllsToDeploy)
                {
                    string targetPath = Path.Combine(gameDir, dllName);
                    string sourcePath = Path.Combine(dxvkArchDir, dllName);
                    var sourceIdentity = FileIdentity.Capture(sourcePath);

                    var existingRecord = installation.FindManagedFile(dllName);
                    OriginalFileState originalState;
                    string? backupRelativePath;
                    SafetyFileIdentity? expectedTargetIdentity = null;

                    if (existingRecord != null && existingRecord.OriginalState != FileOriginalState.Unknown)
                    {
                        originalState = existingRecord.OriginalState switch
                        {
                            FileOriginalState.Existing => OriginalFileState.Existing,
                            FileOriginalState.Missing => OriginalFileState.DidNotExist,
                            _ => OriginalFileState.Unknown
                        };
                        backupRelativePath = existingRecord.BackupRelativePath;
                        if (File.Exists(targetPath))
                        {
                            expectedTargetIdentity = FileIdentity.Capture(targetPath);
                        }
                    }
                    else
                    {
                        if (File.Exists(targetPath))
                        {
                            originalState = OriginalFileState.Existing;
                            expectedTargetIdentity = FileIdentity.Capture(targetPath);
                            backupRelativePath = Path.Combine(installation.Id, dllName);
                        }
                        else
                        {
                            originalState = OriginalFileState.DidNotExist;
                            expectedTargetIdentity = null;
                            backupRelativePath = null;
                        }
                    }

                    filesToProcess.Add(new MultiFileTransactionFile
                    {
                        RelativePath = dllName,
                        SourceFilePath = sourcePath,
                        ExpectedSourceIdentity = sourceIdentity,
                        ExpectedTargetIdentity = expectedTargetIdentity,
                        OriginalState = originalState,
                        BackupRelativePath = backupRelativePath
                    });
                }

                var isUpdate = dllsToDeploy.All(dll =>
                {
                    var r = installation.FindManagedFile(dll);
                    return r != null && r.CurrentState == ManagedFileState.Managed;
                });

                var request = new MultiFileTransactionRequest
                {
                    InstallationRoot = gameDir,
                    Operation = isUpdate ? TransactionOperation.Update : TransactionOperation.Install,
                    Files = filesToProcess
                };

                var result = _transactionEngine.Execute(request);
                if (result.State == TransactionState.Committed && result.Outcome == TransactionOutcome.Success)
                {
                    foreach (var filePlan in filesToProcess)
                    {
                        var record = installation.GetOrAddManagedFile(filePlan.RelativePath);
                        record.OriginalState = filePlan.OriginalState switch
                        {
                            OriginalFileState.Existing => FileOriginalState.Existing,
                            OriginalFileState.DidNotExist => FileOriginalState.Missing,
                            _ => FileOriginalState.Unknown
                        };
                        record.BackupRelativePath = filePlan.BackupRelativePath;
                        if (filePlan.OriginalState == OriginalFileState.Existing && filePlan.ExpectedTargetIdentity != null && string.IsNullOrEmpty(record.OriginalSha256))
                        {
                            record.OriginalSha256 = filePlan.ExpectedTargetIdentity.Sha256;
                        }
                        record.ExpectedManagedSha256 = filePlan.ExpectedSourceIdentity?.Sha256;
                        record.ManagedDxvkVersion = release.Version;
                        record.CurrentState = ManagedFileState.Managed;
                        record.LastVerifiedUtc = DateTime.UtcNow;
                    }

                    installation.ManagedDxvkVersion = release.Version;
                    installation.ManagedDxvkArchitecture = arch;
                    installation.RestorationState = RestorationState.Managed;
                    installation.ConflictFlags = InstallationConflictFlags.None;
                    installation.LastSeenUtc = DateTime.UtcNow;
                    _gameLibraryStore.Save(installation);

                    Logger.Log($"DxvkInstaller: successfully deployed DXVK {release.Version} ({arch}) to {profile.ExeName} via safe transaction {result.TransactionId}.");
                    return true;
                }
                else
                {
                    Logger.Log($"DxvkInstaller: transaction failed ({result.State}/{result.Outcome}): {result.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkInstaller: unexpected error applying DXVK to {profile.ExeName}: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }
    }
}
