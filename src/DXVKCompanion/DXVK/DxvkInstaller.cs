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
            string? stagingDir = null;
            try
            {
                string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                {
                    Logger.Log($"DxvkInstaller: could not determine valid game directory for {profile.ExePath}.");
                    return false;
                }

                var existingInstallation = _gameLibraryStore.FindByInstallationPath(gameDir);
                if (existingInstallation != null && existingInstallation.ConflictFlags != InstallationConflictFlags.None)
                {
                    Logger.Log($"DxvkInstaller: refusing to deploy DXVK to {profile.ExeName}; installation has conflict flags: {existingInstallation.ConflictFlags}.");
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

                var config = new DxvkConfiguration
                {
                    HudEnabled = profile.HudEnabled,
                    FrameLimit = profile.FrameLimit,
                    FrameLimitEnabled = profile.FrameLimit > 0
                };
                installation.Configuration = config;

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

                // Invariant (Section 36): If configuration is needed, stage dxvk.conf atomically
                if (DxvkConfigManager.RequiresConfigFile(config))
                {
                    stagingDir = Path.Combine(GameLibraryPaths.BackupsDir, ".staging", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stagingDir);

                    string targetConfPath = Path.Combine(gameDir, DxvkConfigManager.ConfigFileName);
                    string? existingConf = File.Exists(targetConfPath) ? File.ReadAllText(targetConfPath) : null;
                    string? confContent = DxvkConfigManager.GenerateConfigContent(config, existingConf);

                    if (!string.IsNullOrEmpty(confContent))
                    {
                        string stagedConfPath = Path.Combine(stagingDir, DxvkConfigManager.ConfigFileName);
                        File.WriteAllText(stagedConfPath, confContent);
                        var stagedConfIdentity = FileIdentity.Capture(stagedConfPath);

                        var existingConfRecord = installation.FindManagedFile(DxvkConfigManager.ConfigFileName);
                        OriginalFileState confOriginalState;
                        string? confBackupRelativePath;
                        SafetyFileIdentity? expectedConfTargetIdentity = null;

                        if (existingConfRecord != null && existingConfRecord.OriginalState != FileOriginalState.Unknown)
                        {
                            confOriginalState = existingConfRecord.OriginalState switch
                            {
                                FileOriginalState.Existing => OriginalFileState.Existing,
                                FileOriginalState.Missing => OriginalFileState.DidNotExist,
                                _ => OriginalFileState.Unknown
                            };
                            confBackupRelativePath = existingConfRecord.BackupRelativePath;
                            if (File.Exists(targetConfPath))
                            {
                                expectedConfTargetIdentity = FileIdentity.Capture(targetConfPath);
                            }
                        }
                        else
                        {
                            if (File.Exists(targetConfPath))
                            {
                                confOriginalState = OriginalFileState.Existing;
                                expectedConfTargetIdentity = FileIdentity.Capture(targetConfPath);
                                confBackupRelativePath = Path.Combine(installation.Id, DxvkConfigManager.ConfigFileName);
                            }
                            else
                            {
                                confOriginalState = OriginalFileState.DidNotExist;
                                expectedConfTargetIdentity = null;
                                confBackupRelativePath = null;
                            }
                        }

                        filesToProcess.Add(new MultiFileTransactionFile
                        {
                            RelativePath = DxvkConfigManager.ConfigFileName,
                            SourceFilePath = stagedConfPath,
                            ExpectedSourceIdentity = stagedConfIdentity,
                            ExpectedTargetIdentity = expectedConfTargetIdentity,
                            OriginalState = confOriginalState,
                            BackupRelativePath = confBackupRelativePath
                        });
                    }
                }

                var isUpdate = dllsToDeploy.All(dll =>
                {
                    var r = installation.FindManagedFile(dll);
                    return r != null && r.CurrentState == ManagedFileState.Consistent && !string.IsNullOrEmpty(r.ManagedDxvkVersion);
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
                        record.ManagedDxvkVersion = filePlan.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? release.Version : null;
                        record.CurrentState = ManagedFileState.Consistent;
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
            finally
            {
                if (stagingDir != null)
                {
                    try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
                }
            }
        }

        public async Task<bool> ReapplyAsync(GameProfile profile, bool updateBaseline = false)
        {
            string? stagingDir = null;
            try
            {
                string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                {
                    Logger.Log($"DxvkInstaller: could not determine valid game directory for {profile.ExePath}.");
                    return false;
                }

                var installation = _gameLibraryStore.FindByInstallationPath(gameDir);
                if (installation == null || string.IsNullOrEmpty(installation.ManagedDxvkVersion))
                {
                    Logger.Log($"DxvkInstaller: installation not managed by Companion for {profile.ExeName}.");
                    return false;
                }

                if (installation.ConflictFlags != InstallationConflictFlags.None)
                {
                    Logger.Log($"DxvkInstaller: refusing reapply on {installation.DisplayName}; installation has conflict flags: {installation.ConflictFlags}.");
                    return false;
                }

                string version = installation.ManagedDxvkVersion;
                string arch = installation.ManagedDxvkArchitecture ?? (string.Equals(profile.Architecture, "x32", StringComparison.OrdinalIgnoreCase) ? "x32" : "x64");
                string versionDir = Path.Combine(_dxvkSourceDir, SanitizeVersion(version));
                string dxvkArchDir = Path.Combine(versionDir, arch);

                if (!Directory.Exists(dxvkArchDir))
                {
                    Logger.Log($"DxvkInstaller: source DXVK directory not found for version {version} ({arch}).");
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
                    Logger.Log($"DxvkInstaller: {profile.ExeName} has unsupported API ({profile.Api}); skipping reapply.");
                    return false;
                }

                foreach (var dllName in dllsToDeploy)
                {
                    var src = Path.Combine(dxvkArchDir, dllName);
                    if (!File.Exists(src))
                    {
                        Logger.Log($"DxvkInstaller: required source DXVK file missing: {src}");
                        return false;
                    }
                }

                var filesToProcess = new List<MultiFileTransactionFile>();

                foreach (var dllName in dllsToDeploy)
                {
                    string targetPath = Path.Combine(gameDir, dllName);
                    string sourcePath = Path.Combine(dxvkArchDir, dllName);
                    var sourceIdentity = FileIdentity.Capture(sourcePath);

                    var existingRecord = installation.FindManagedFile(dllName);
                    OriginalFileState originalState = existingRecord != null && existingRecord.OriginalState == FileOriginalState.Existing
                        ? OriginalFileState.Existing
                        : OriginalFileState.DidNotExist;

                    string? backupRelativePath = existingRecord?.BackupRelativePath ?? (originalState == OriginalFileState.Existing ? Path.Combine(installation.Id, dllName) : null);
                    SafetyFileIdentity? expectedTargetIdentity = File.Exists(targetPath) ? FileIdentity.Capture(targetPath) : null;

                    // Section 18.2: If updating baseline after an external change (e.g. game update),
                    // the newly observed game file becomes the new restoration baseline.
                    if (updateBaseline && File.Exists(targetPath))
                    {
                        var currentIdentity = FileIdentity.Capture(targetPath);
                        if (existingRecord != null && !string.Equals(currentIdentity.Sha256, existingRecord.ExpectedManagedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            backupRelativePath = Path.Combine(installation.Id, dllName);
                            string fullBackupPath = Path.Combine(GameLibraryPaths.BackupsDir, backupRelativePath);
                            Directory.CreateDirectory(Path.GetDirectoryName(fullBackupPath)!);
                            File.Copy(targetPath, fullBackupPath, overwrite: true);
                            originalState = OriginalFileState.Existing;
                            existingRecord.OriginalState = FileOriginalState.Existing;
                            existingRecord.OriginalSha256 = currentIdentity.Sha256;
                            existingRecord.BackupRelativePath = backupRelativePath;
                            Logger.Log($"DxvkInstaller: updated restoration baseline for {dllName} in {installation.DisplayName} to hash {currentIdentity.Sha256[..Math.Min(8, currentIdentity.Sha256.Length)]}.");
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

                var config = new DxvkConfiguration
                {
                    HudEnabled = profile.HudEnabled,
                    FrameLimit = profile.FrameLimit,
                    FrameLimitEnabled = profile.FrameLimit > 0
                };
                installation.Configuration = config;

                if (DxvkConfigManager.RequiresConfigFile(config))
                {
                    stagingDir = Path.Combine(GameLibraryPaths.BackupsDir, ".staging", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stagingDir);

                    string targetConfPath = Path.Combine(gameDir, DxvkConfigManager.ConfigFileName);
                    string? existingConf = File.Exists(targetConfPath) ? File.ReadAllText(targetConfPath) : null;
                    string? confContent = DxvkConfigManager.GenerateConfigContent(config, existingConf);

                    if (!string.IsNullOrEmpty(confContent))
                    {
                        string stagedConfPath = Path.Combine(stagingDir, DxvkConfigManager.ConfigFileName);
                        File.WriteAllText(stagedConfPath, confContent);
                        var stagedConfIdentity = FileIdentity.Capture(stagedConfPath);

                        var existingConfRecord = installation.FindManagedFile(DxvkConfigManager.ConfigFileName);
                        OriginalFileState confOriginalState = existingConfRecord != null && existingConfRecord.OriginalState == FileOriginalState.Existing
                            ? OriginalFileState.Existing
                            : OriginalFileState.DidNotExist;

                        string? confBackupRelativePath = existingConfRecord?.BackupRelativePath ?? (confOriginalState == OriginalFileState.Existing ? Path.Combine(installation.Id, DxvkConfigManager.ConfigFileName) : null);
                        SafetyFileIdentity? expectedConfTargetIdentity = File.Exists(targetConfPath) ? FileIdentity.Capture(targetConfPath) : null;

                        filesToProcess.Add(new MultiFileTransactionFile
                        {
                            RelativePath = DxvkConfigManager.ConfigFileName,
                            SourceFilePath = stagedConfPath,
                            ExpectedSourceIdentity = stagedConfIdentity,
                            ExpectedTargetIdentity = expectedConfTargetIdentity,
                            OriginalState = confOriginalState,
                            BackupRelativePath = confBackupRelativePath
                        });
                    }
                }

                var request = new MultiFileTransactionRequest
                {
                    InstallationRoot = gameDir,
                    Operation = TransactionOperation.Reapply,
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
                        record.ExpectedManagedSha256 = filePlan.ExpectedSourceIdentity?.Sha256;
                        record.ManagedDxvkVersion = filePlan.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? version : null;
                        record.CurrentState = ManagedFileState.Consistent;
                        record.LastVerifiedUtc = DateTime.UtcNow;
                    }

                    installation.RestorationState = RestorationState.Managed;
                    installation.ConflictFlags = InstallationConflictFlags.None;
                    installation.PendingAction = null;
                    installation.LastSeenUtc = DateTime.UtcNow;
                    _gameLibraryStore.Save(installation);

                    Logger.Log($"DxvkInstaller: successfully reapplied DXVK {version} to {profile.ExeName} via safe transaction {result.TransactionId}.");
                    return true;
                }
                else
                {
                    Logger.Log($"DxvkInstaller: reapply transaction failed ({result.State}/{result.Outcome}): {result.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkInstaller: unexpected error reapplying DXVK to {profile.ExeName}: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
            finally
            {
                if (stagingDir != null)
                {
                    try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
                }
            }
        }

        public bool AdoptExisting(GameProfile profile, ExistingDxvkAssessment assessment)
        {
            try
            {
                if (!assessment.CanBeAdopted || string.IsNullOrEmpty(assessment.MatchedVersion))
                {
                    Logger.Log($"DxvkInstaller: cannot adopt existing DXVK for {profile.ExeName}; status is {assessment.Status}.");
                    return false;
                }

                string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                {
                    Logger.Log($"DxvkInstaller: invalid game directory for {profile.ExePath}.");
                    return false;
                }

                var installation = _gameLibraryStore.GetOrCreateInstallation(gameDir, Path.GetFileNameWithoutExtension(profile.ExePath));

                if (installation.ConflictFlags != InstallationConflictFlags.None)
                {
                    Logger.Log($"DxvkInstaller: refusing to adopt existing DXVK for {profile.ExeName}; installation has conflicts: {installation.ConflictFlags}.");
                    return false;
                }

                string arch = string.Equals(profile.Architecture, "x32", StringComparison.OrdinalIgnoreCase) ? "x32" : "x64";

                foreach (var dllName in assessment.DetectedDlls)
                {
                    string targetPath = Path.Combine(gameDir, dllName);
                    if (!File.Exists(targetPath))
                    {
                        Logger.Log($"DxvkInstaller: detected DLL missing during adoption: {targetPath}");
                        return false;
                    }

                    var targetIdentity = FileIdentity.Capture(targetPath);
                    var record = installation.GetOrAddManagedFile(dllName);
                    record.OriginalState = FileOriginalState.Missing;
                    record.BackupRelativePath = null;
                    record.ExpectedManagedSha256 = targetIdentity.Sha256;
                    record.ManagedDxvkVersion = assessment.MatchedVersion;
                    record.CurrentState = ManagedFileState.Consistent;
                    record.LastVerifiedUtc = DateTime.UtcNow;
                }

                installation.ManagedDxvkVersion = assessment.MatchedVersion;
                installation.ManagedDxvkArchitecture = arch;
                installation.RestorationState = RestorationState.Managed;
                installation.ConflictFlags = InstallationConflictFlags.None;
                installation.LastSeenUtc = DateTime.UtcNow;

                var executable = installation.GetOrAddExecutable(Path.GetFileName(profile.ExePath), profile.ExeName);
                executable.LastKnownApi = profile.Api;
                executable.LastKnownArchitecture = arch;

                _gameLibraryStore.Save(installation);

                profile.DxvkEnabled = true;
                profile.DxvkVersion = assessment.MatchedVersion;

                Logger.Log($"DxvkInstaller: successfully adopted official DXVK {assessment.MatchedVersion} for {profile.ExeName}.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkInstaller: unexpected error adopting DXVK for {profile.ExeName}: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }
    }
}
