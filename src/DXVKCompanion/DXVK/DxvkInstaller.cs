using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;

namespace DXVKCompanion.DXVK
{
    public class DxvkInstaller
    {
        private readonly HttpClient _httpClient;
        private readonly MultiFileTransactionEngine _transactionEngine;
        private readonly GameLibraryStore _gameLibraryStore;
        private readonly string _dxvkSourceDir;

        internal IOperationLeaseCoordinator LeaseCoordinator { get; set; } = OperationLeaseCoordinator.Default;
        internal IProcessActivityInspector ActivityInspector { get; set; } = ProcessActivityInspector.Default;

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
        }

        internal DxvkInstaller(
            HttpClient httpClient,
            MultiFileTransactionEngine? transactionEngine,
            GameLibraryStore? gameLibraryStore,
            string? dxvkSourceDir,
            FileUtils? files,
            IOperationLeaseCoordinator? leaseCoordinator,
            IProcessActivityInspector? activityInspector = null)
            : this(httpClient, transactionEngine, gameLibraryStore, dxvkSourceDir, files)
        {
            if (leaseCoordinator != null) LeaseCoordinator = leaseCoordinator;
            if (activityInspector != null) ActivityInspector = activityInspector;
        }

        public DxvkInstaller(FileUtils files, HttpClient httpClient)
            : this(httpClient, null, null, null, files)
        {
        }

        public string DxvkSourceDir => _dxvkSourceDir;
        private DxvkReleaseAcquirer? _releaseAcquirer;
        private readonly object _releaseAcquirerGate = new();
        internal DxvkReleaseAcquirer GetReleaseAcquirer(DxvkGithubClient? github = null)
        {
            // CacheStore construction performs no IO. The fallback client is used
            // only for exact-tag metadata, which never reads/writes the Latest cache.
            lock (_releaseAcquirerGate) return _releaseAcquirer ??= new DxvkReleaseAcquirer(github ?? new DxvkGithubClient(_httpClient, new CacheStore()), _dxvkSourceDir);
        }
        public string? LastRefusalReason { get; private set; }
        internal ProfileStore? ProfileStore { get; set; }

        private IOperationLease? GetProfileStoreLease(IOperationLease leaseToken)
        {
            if (ProfileStore == null || string.IsNullOrWhiteSpace(ProfileStore.ProfilesFile))
                return null;
            if (leaseToken.IsValidForStore(ProfileStore.ProfilesFile))
                return leaseToken;
            if (leaseToken.StoreLease != null && leaseToken.StoreLease.IsValidForStore(ProfileStore.ProfilesFile))
                return leaseToken.StoreLease;
            return null;
        }

        private static string SanitizeVersion(string version)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                version = version.Replace(c, '_');
            return version;
        }

        private async Task<bool> DownloadAndExtractAsync(ReleaseInfo release)
        {
            try
            {
                // Exact-tag acquisition obtains official metadata itself; never use an asserted URL.
                await GetReleaseAcquirer().AcquireAsync(release.Version);
                return true;
            }
            catch (OperationCanceledException)
            {
                Logger.Log("DxvkInstaller: official release acquisition cancelled or timed out.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkInstaller: official release acquisition failed ({ex.GetType().Name}).");
                return false;
            }
        }

        public Task<bool> ApplyToGameAsync(GameProfile profile, ReleaseInfo release) =>
            ApplyToGameAsync(profile, release, null);

        internal async Task<bool> ApplyToGameAsync(GameProfile profile, ReleaseInfo release, IOperationLease? leaseToken)
        {
            LastRefusalReason = null;
            string? stagingDir = null;
            GameInstallation? installation = null;
            GameInstallation? preOperationSnapshot = null;
            string gameDir = string.Empty;
            IOperationLease? acquiredLease = null;
            try
            {
                var existingInstallation = _gameLibraryStore.FindInstallationForExecutable(profile.ExePath)
                    ?? _gameLibraryStore.FindByInstallationPath(Path.GetDirectoryName(profile.ExePath) ?? string.Empty);

                string rawDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawDir) || !Directory.Exists(rawDir))
                {
                    LastRefusalReason = $"Could not determine valid game directory for {profile.ExePath}.";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }

                // Resolve authoritative installation root
                string authoritativeRoot = existingInstallation != null && !string.IsNullOrWhiteSpace(existingInstallation.InstallationPath)
                    ? existingInstallation.InstallationPath
                    : GameInstallation.NormalizeInstallationPath(rawDir);
                gameDir = rawDir;

                if (leaseToken != null)
                {
                    if (leaseToken.IsDisposed || !leaseToken.IsAcquired ||
                        !leaseToken.IsValidForTarget(authoritativeRoot) ||
                        (!leaseToken.IsValidForStore(_gameLibraryStore.LibraryFilePath) && (leaseToken.StoreLease == null || !leaseToken.StoreLease.IsValidForStore(_gameLibraryStore.LibraryFilePath))) ||
                        (ProfileStore != null && !string.IsNullOrWhiteSpace(ProfileStore.ProfilesFile) && !leaseToken.IsValidForStore(ProfileStore.ProfilesFile) && (leaseToken.StoreLease == null || !leaseToken.StoreLease.IsValidForStore(ProfileStore.ProfilesFile))))
                    {
                        throw new ArgumentException("Invalid, mismatched, or incomplete lease token for installation and store scope.", nameof(leaseToken));
                    }
                }
                else
                {
                    var storePaths = new List<string> { _gameLibraryStore.LibraryFilePath };
                    if (ProfileStore != null && !string.IsNullOrWhiteSpace(ProfileStore.ProfilesFile))
                    {
                        storePaths.Add(ProfileStore.ProfilesFile);
                    }
                    acquiredLease = await LeaseCoordinator.TryAcquireOperationScopeAsync(
                        authoritativeRoot,
                        storePaths,
                        TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

                    if (acquiredLease == null)
                    {
                        LastRefusalReason = LeaseCoordinator.LastAcquisitionError ??
                            $"Operation in progress for installation: {authoritativeRoot}. Please retry shortly.";
                        Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                        return false;
                    }
                    leaseToken = acquiredLease;
                }

                var candidateExes = new List<string> { profile.ExeName };
                if (existingInstallation?.Executables != null)
                {
                    candidateExes.AddRange(existingInstallation.Executables.Select(e => e.RelativePath));
                }
                var activity = ActivityInspector.GetActivityEvidence(authoritativeRoot, candidateExes);
                if (activity == ProcessActivityEvidence.Running)
                {
                    LastRefusalReason = $"Game is currently running: {authoritativeRoot}. Close the game before modifying files.";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }
                if (activity == ProcessActivityEvidence.Unknown)
                {
                    LastRefusalReason = "Cannot verify game activity: candidate process access denied (protected or elevated).";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }

                if (!_gameLibraryStore.TryCheckCanModifyLibrary(out var storeRefusalReason, leaseToken.StoreLease))
                {
                    LastRefusalReason = storeRefusalReason;
                    if (existingInstallation != null)
                    {
                        existingInstallation.RestorationState = RestorationState.AttentionRequired;
                        existingInstallation.LastRefusalReason = storeRefusalReason;
                    }
                    Logger.Log($"DxvkInstaller: refusing deployment to {profile.ExeName}: {storeRefusalReason}");
                    return false;
                }

                if (ProfileStore != null && !ProfileStore.TryCheckCanModifyProfileStore(out var profileRefusalReason, GetProfileStoreLease(leaseToken)))
                {
                    LastRefusalReason = profileRefusalReason;
                    if (existingInstallation != null)
                    {
                        existingInstallation.RestorationState = RestorationState.AttentionRequired;
                        existingInstallation.LastRefusalReason = profileRefusalReason;
                    }
                    Logger.Log($"DxvkInstaller: refusing deployment to {profile.ExeName}: {profileRefusalReason}");
                    return false;
                }

                if (existingInstallation != null && existingInstallation.ConflictFlags != InstallationConflictFlags.None)
                {
                    LastRefusalReason = $"Refusing to deploy DXVK to {profile.ExeName}; installation has conflict flags: {existingInstallation.ConflictFlags}.";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }

                if (!DxvkCompatibility.IsInstallationSupported(existingInstallation, profile.Api, profile.ExeName, out var refusalReason))
                {
                    LastRefusalReason = refusalReason;
                    if (existingInstallation != null)
                    {
                        existingInstallation.LastRefusalReason = refusalReason;
                        _gameLibraryStore.Save(existingInstallation, leaseToken.StoreLease);
                    }
                    Logger.Log($"DxvkInstaller: refusing deployment to {profile.ExeName}: {refusalReason}");
                    return false;
                }

                string? arch = DxvkLocalReleaseLibrary.NormalizeArchitecture(profile.Architecture);
                if (arch == null)
                {
                    LastRefusalReason = "Cannot select DXVK source for an unknown architecture.";
                    return false;
                }
                string versionDir = Path.Combine(_dxvkSourceDir, SanitizeVersion(release.Version));
                string dxvkArchDir = Path.Combine(versionDir, arch);

                if (DxvkReleaseAcquirer.HasAcquisitionRecord(versionDir) &&
                    !DxvkLocalReleaseLibrary.IsVersionAvailable(_dxvkSourceDir, release.Version, profile.Api, profile.Architecture))
                {
                    LastRefusalReason = "Acquired DXVK release is invalid or unsupported for this game. Use Browse / Download official versions to inspect the source library.";
                    return false;
                }

                if (!DxvkLocalReleaseLibrary.IsVersionAvailable(_dxvkSourceDir, release.Version, profile.Api, profile.Architecture))
                {
                    // The shared acquirer rechecks existing publication under its tag gate.
                    // Do not turn a concurrent successful publication into a false refusal here.
                    bool ok = await DownloadAndExtractAsync(release);
                    if (!ok)
                    {
                        LastRefusalReason = $"DXVK {release.Version} is unavailable locally and could not be acquired. Keep the installed version. To retry offline when no work is queued, select a downloaded exact DXVK Version in Game Details and Save.";
                        return false;
                    }

                    if (!DxvkLocalReleaseLibrary.IsVersionAvailable(_dxvkSourceDir, release.Version, profile.Api, profile.Architecture))
                    {
                        LastRefusalReason = "Acquired official release does not contain the required architecture/API payload.";
                        return false;
                    }

                    // R4: Revalidate candidate activity and authoritative metadata baseline after awaited download
                    var postDownloadActivity = ActivityInspector.GetActivityEvidence(authoritativeRoot, candidateExes);
                    if (postDownloadActivity == ProcessActivityEvidence.Running)
                    {
                        LastRefusalReason = $"Game is currently running: {authoritativeRoot}. Close the game before modifying files.";
                        Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                        return false;
                    }
                    if (postDownloadActivity == ProcessActivityEvidence.Unknown)
                    {
                        LastRefusalReason = "Cannot verify game activity: candidate process access denied (protected or elevated).";
                        Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                        return false;
                    }

                    if (!_gameLibraryStore.TryCheckCanModifyLibrary(out var postDownloadStoreRefusal, leaseToken.StoreLease))
                    {
                        LastRefusalReason = postDownloadStoreRefusal;
                        Logger.Log($"DxvkInstaller: refusing deployment to {profile.ExeName} after download: {postDownloadStoreRefusal}");
                        return false;
                    }

                    if (ProfileStore != null && !ProfileStore.TryCheckCanModifyProfileStore(out var postDownloadProfileRefusal, GetProfileStoreLease(leaseToken)))
                    {
                        LastRefusalReason = postDownloadProfileRefusal;
                        Logger.Log($"DxvkInstaller: refusing deployment to {profile.ExeName} after download: {postDownloadProfileRefusal}");
                        return false;
                    }
                }

                string[] dllsToDeploy = DxvkCompatibility.GetRequiredDlls(profile.Api);
                if (dllsToDeploy.Length == 0)
                {
                    LastRefusalReason = $"{profile.ExeName} has an unsupported API ({profile.Api}).";
                    Logger.Log($"DxvkInstaller: {profile.ExeName} has an unsupported API ({profile.Api}); skipping.");
                    return false;
                }

                foreach (var dllName in dllsToDeploy)
                {
                    var src = Path.Combine(dxvkArchDir, dllName);
                    if (!File.Exists(src))
                    {
                        LastRefusalReason = $"Extracted DXVK source file missing: {dllName}.";
                        Logger.Log($"DxvkInstaller: extracted DXVK source file missing: {src}");
                        return false;
                    }
                }

                var preMutationActivity = ActivityInspector.GetActivityEvidence(authoritativeRoot, candidateExes);
                if (preMutationActivity == ProcessActivityEvidence.Running)
                {
                    LastRefusalReason = $"Game is currently running: {authoritativeRoot}. Close the game before modifying files.";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }
                if (preMutationActivity == ProcessActivityEvidence.Unknown)
                {
                    LastRefusalReason = "Cannot verify game activity: candidate process access denied (protected or elevated).";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }

                if (!_gameLibraryStore.TryCheckCanModifyLibrary(out var preMutationStoreRefusal, leaseToken.StoreLease))
                {
                    LastRefusalReason = preMutationStoreRefusal;
                    Logger.Log($"DxvkInstaller: refusing deployment to {profile.ExeName} before mutation: {preMutationStoreRefusal}");
                    return false;
                }

                if (ProfileStore != null && !ProfileStore.TryCheckCanModifyProfileStore(out var preMutationProfileRefusal, GetProfileStoreLease(leaseToken)))
                {
                    LastRefusalReason = preMutationProfileRefusal;
                    Logger.Log($"DxvkInstaller: refusing deployment to {profile.ExeName} before mutation: {preMutationProfileRefusal}");
                    return false;
                }

                installation = existingInstallation
                    ?? _gameLibraryStore.GetOrCreateInstallation(authoritativeRoot, Path.GetFileNameWithoutExtension(profile.ExePath), leaseToken.StoreLease);

                preOperationSnapshot = _gameLibraryStore.CreateSnapshot(installation);
                _gameLibraryStore.CaptureCurrentLibraryBaseline(out var preOperationLibraryBytes, out bool preOperationLibraryExisted);

                string relExe = Path.GetRelativePath(installation.InstallationPath, profile.ExePath);
                string relGameDir = Path.GetRelativePath(installation.InstallationPath, gameDir);
                string ResolveInstallationRelativeFilePath(string fileName) =>
                    string.IsNullOrEmpty(relGameDir) || relGameDir == "."
                        ? fileName
                        : GameInstallation.NormalizeRelativePath(Path.Combine(relGameDir, fileName));

                var config = new DxvkConfiguration
                {
                    HudEnabled = profile.HudEnabled,
                    FrameLimit = profile.FrameLimit,
                    FrameLimitEnabled = profile.FrameLimit > 0
                };

                var filesToProcess = new List<MultiFileTransactionFile>();

                foreach (var dllName in dllsToDeploy)
                {
                    string relFilePath = ResolveInstallationRelativeFilePath(dllName);
                    string targetPath = Path.Combine(installation.InstallationPath, relFilePath);
                    string sourcePath = Path.Combine(dxvkArchDir, dllName);
                    var sourceIdentity = FileIdentity.Capture(sourcePath);

                    var existingRecord = installation.FindManagedFile(relFilePath);
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
                            backupRelativePath = Path.Combine(installation.Id, relFilePath);
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
                        RelativePath = relFilePath,
                        SourceFilePath = sourcePath,
                        ExpectedSourceIdentity = sourceIdentity,
                        ExpectedTargetIdentity = expectedTargetIdentity,
                        OriginalState = originalState,
                        BackupRelativePath = backupRelativePath
                    });
                }

                // Invariant (Section 36): If configuration is needed, stage dxvk.conf atomically
                string relConfPath = ResolveInstallationRelativeFilePath(DxvkConfigManager.ConfigFileName);
                string targetConfPath = Path.Combine(installation.InstallationPath, relConfPath);
                var managedConf = installation.FindManagedFile(relConfPath);
                bool updateManagedConf = managedConf != null
                    && managedConf.OriginalState != FileOriginalState.Unknown
                    && !string.IsNullOrEmpty(managedConf.ManagedDxvkVersion)
                    && File.Exists(targetConfPath);
                if (DxvkConfigManager.RequiresConfigFile(config) || updateManagedConf)
                {
                    stagingDir = Path.Combine(_transactionEngine.TransactionStoreRoot, ".staging", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stagingDir);

                    string? existingConf = File.Exists(targetConfPath) ? File.ReadAllText(targetConfPath) : null;
                    string? confContent = updateManagedConf
                        ? DxvkConfigManager.GenerateManagedConfigContent(config, existingConf!)
                        : DxvkConfigManager.GenerateConfigContent(config, existingConf);

                    if (!string.IsNullOrEmpty(confContent))
                    {
                        string stagedConfPath = Path.Combine(stagingDir, DxvkConfigManager.ConfigFileName);
                        File.WriteAllText(stagedConfPath, confContent);
                        var stagedConfIdentity = FileIdentity.Capture(stagedConfPath);

                        var existingConfRecord = installation.FindManagedFile(relConfPath);
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
                                confBackupRelativePath = Path.Combine(installation.Id, relConfPath);
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
                            RelativePath = relConfPath,
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
                    string relDll = ResolveInstallationRelativeFilePath(dll);
                    var r = installation.FindManagedFile(relDll);
                    return r != null && r.CurrentState == ManagedFileState.Consistent && !string.IsNullOrEmpty(r.ManagedDxvkVersion);
                });

                var request = new MultiFileTransactionRequest
                {
                    InstallationRoot = installation.InstallationPath,
                    Operation = isUpdate ? TransactionOperation.Update : TransactionOperation.Install,
                    Files = filesToProcess
                };

                bool saveCallbackInvoked = false;
                OwnershipSaveResult lastSaveResult = new OwnershipSaveResult(
                    OwnershipPersistenceOutcome.Unknown,
                    DiagnosticMessage: "Ownership commit callback did not complete.");

                var result = _transactionEngine.ExecuteWithCommitScope(request, committedFiles =>
                {
                    saveCallbackInvoked = true;
                    try
                    {
                        var executable = installation.GetOrAddExecutable(relExe, profile.ExeName);
                        executable.LastKnownApi = profile.Api;
                        executable.LastKnownArchitecture = arch;
                        installation.Configuration = config;

                        foreach (var filePlan in committedFiles)
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
                        installation.LastRefusalReason = null;
                        installation.LastSeenUtc = DateTime.UtcNow;

                        lastSaveResult = _gameLibraryStore.TrySaveWithOutcome(installation, leaseToken.StoreLease);
                        return lastSaveResult;
                    }
                    catch (Exception callbackEx)
                    {
                        string reason = $"Unexpected exception in ownership commit callback: {callbackEx.GetType().Name} - {callbackEx.Message}";
                        _gameLibraryStore.RecordUnexpectedCommitCallbackException(reason, installation);
                        lastSaveResult = new OwnershipSaveResult(
                            OwnershipPersistenceOutcome.Unknown,
                            Exception: callbackEx,
                            DiagnosticMessage: reason,
                            PreWriteBytes: preOperationLibraryBytes,
                            PreWriteExisted: preOperationLibraryExisted);
                        throw;
                    }
                });

                if (result.State == TransactionState.Committed && result.Outcome == TransactionOutcome.Success)
                {
                    LastRefusalReason = null;
                    Logger.Log($"DxvkInstaller: successfully deployed DXVK {release.Version} ({arch}) to {profile.ExeName} via safe transaction {result.TransactionId}.");
                    return true;
                }

                string failureMessage = result.Message ?? $"Transaction failed ({result.State}/{result.Outcome}).";
                LastRefusalReason = failureMessage;

                if (result.State == TransactionState.AttentionRequired || result.Outcome == TransactionOutcome.UnresolvedRecovery)
                {
                    if (saveCallbackInvoked && lastSaveResult.Outcome == OwnershipPersistenceOutcome.Unknown)
                    {
                        installation.RestorationState = RestorationState.AttentionRequired;
                        installation.LastRefusalReason = failureMessage;
                    }
                    else
                    {
                        _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                        installation.RestorationState = RestorationState.AttentionRequired;
                        installation.LastRefusalReason = failureMessage;

                        byte[]? expectedPreWriteBytes = saveCallbackInvoked ? lastSaveResult.PreWriteBytes : preOperationLibraryBytes;
                        bool expectedPreWriteExisted = saveCallbackInvoked ? lastSaveResult.PreWriteExisted : preOperationLibraryExisted;
                        _gameLibraryStore.TrySaveHealthAfterFailedCompensation(installation, expectedPreWriteBytes, expectedPreWriteExisted, leaseToken.StoreLease);

                        installation.RestorationState = RestorationState.AttentionRequired;
                        installation.LastRefusalReason = failureMessage;
                    }
                }
                else
                {
                    _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                }

                Logger.Log($"DxvkInstaller: transaction failed ({result.State}/{result.Outcome}): {failureMessage}");
                return false;
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastRefusalReason = $"Unexpected error applying DXVK to {profile.ExeName}: {ex.GetType().Name} - {ex.Message}";
                if (installation != null && preOperationSnapshot != null)
                {
                    if (_gameLibraryStore.HasUncertainPersistence)
                    {
                        installation.RestorationState = RestorationState.AttentionRequired;
                        installation.LastRefusalReason = LastRefusalReason;
                    }
                    else
                    {
                        _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                    }
                }
                else if (_gameLibraryStore.HasUncertainPersistence && !string.IsNullOrWhiteSpace(gameDir))
                {
                    var tracked = _gameLibraryStore.FindInstallationForExecutable(profile.ExePath)
                        ?? _gameLibraryStore.FindByInstallationPath(gameDir);
                    if (tracked != null)
                    {
                        tracked.RestorationState = RestorationState.AttentionRequired;
                        tracked.LastRefusalReason ??= LastRefusalReason;
                    }
                }
                Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                return false;
            }
            finally
            {
                if (stagingDir != null)
                {
                    try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
                }
                if (acquiredLease != null)
                {
                    await acquiredLease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        public Task<bool> ReapplyAsync(GameProfile profile, bool updateBaseline = false) =>
            ReapplyAsync(profile, updateBaseline, null);

        internal async Task<bool> ReapplyAsync(GameProfile profile, bool updateBaseline, IOperationLease? leaseToken)
        {
            LastRefusalReason = null;
            string? stagingDir = null;
            GameInstallation? installation = null;
            GameInstallation? preOperationSnapshot = null;
            IOperationLease? acquiredLease = null;
            try
            {
                var existingInstallation = _gameLibraryStore.FindInstallationForExecutable(profile.ExePath)
                    ?? _gameLibraryStore.FindByInstallationPath(Path.GetDirectoryName(profile.ExePath) ?? string.Empty);

                string rawDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawDir) || !Directory.Exists(rawDir))
                {
                    LastRefusalReason = $"Could not determine valid game directory for {profile.ExePath}.";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }

                // Resolve authoritative installation root
                string authoritativeRoot = existingInstallation != null && !string.IsNullOrWhiteSpace(existingInstallation.InstallationPath)
                    ? existingInstallation.InstallationPath
                    : GameInstallation.NormalizeInstallationPath(rawDir);
                string gameDir = rawDir;

                if (leaseToken != null)
                {
                    if (leaseToken.IsDisposed || !leaseToken.IsAcquired ||
                        !leaseToken.IsValidForTarget(authoritativeRoot) ||
                        (!leaseToken.IsValidForStore(_gameLibraryStore.LibraryFilePath) && (leaseToken.StoreLease == null || !leaseToken.StoreLease.IsValidForStore(_gameLibraryStore.LibraryFilePath))) ||
                        (ProfileStore != null && !string.IsNullOrWhiteSpace(ProfileStore.ProfilesFile) && !leaseToken.IsValidForStore(ProfileStore.ProfilesFile) && (leaseToken.StoreLease == null || !leaseToken.StoreLease.IsValidForStore(ProfileStore.ProfilesFile))))
                    {
                        throw new ArgumentException("Invalid, mismatched, or incomplete lease token for installation and store scope.", nameof(leaseToken));
                    }
                }
                else
                {
                    var storePaths = new List<string> { _gameLibraryStore.LibraryFilePath };
                    if (ProfileStore != null && !string.IsNullOrWhiteSpace(ProfileStore.ProfilesFile))
                    {
                        storePaths.Add(ProfileStore.ProfilesFile);
                    }
                    acquiredLease = await LeaseCoordinator.TryAcquireOperationScopeAsync(
                        authoritativeRoot,
                        storePaths,
                        TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

                    if (acquiredLease == null)
                    {
                        LastRefusalReason = LeaseCoordinator.LastAcquisitionError ??
                            $"Operation in progress for installation: {authoritativeRoot}. Please retry shortly.";
                        Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                        return false;
                    }
                    leaseToken = acquiredLease;
                }

                installation = existingInstallation;
                if (installation == null || string.IsNullOrEmpty(installation.ManagedDxvkVersion))
                {
                    LastRefusalReason = $"Installation not managed by Companion for {profile.ExeName}.";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }

                var candidateExes = new List<string> { profile.ExeName };
                if (installation.Executables != null)
                {
                    candidateExes.AddRange(installation.Executables.Select(e => e.RelativePath));
                }
                var activity = ActivityInspector.GetActivityEvidence(authoritativeRoot, candidateExes);
                if (activity == ProcessActivityEvidence.Running)
                {
                    LastRefusalReason = $"Game is currently running: {authoritativeRoot}. Close the game before modifying files.";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }
                if (activity == ProcessActivityEvidence.Unknown)
                {
                    LastRefusalReason = "Cannot verify game activity: candidate process access denied (protected or elevated).";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }

                if (!_gameLibraryStore.TryCheckCanModifyLibrary(out var storeRefusalReason, leaseToken.StoreLease))
                {
                    LastRefusalReason = storeRefusalReason;
                    installation.RestorationState = RestorationState.AttentionRequired;
                    installation.LastRefusalReason = storeRefusalReason;
                    Logger.Log($"DxvkInstaller: refusing reapply on {installation.DisplayName}: {storeRefusalReason}");
                    return false;
                }

                if (ProfileStore != null && !ProfileStore.TryCheckCanModifyProfileStore(out var profileRefusalReason, GetProfileStoreLease(leaseToken)))
                {
                    LastRefusalReason = profileRefusalReason;
                    installation.RestorationState = RestorationState.AttentionRequired;
                    installation.LastRefusalReason = profileRefusalReason;
                    Logger.Log($"DxvkInstaller: refusing reapply on {installation.DisplayName}: {profileRefusalReason}");
                    return false;
                }

                if (installation.ConflictFlags != InstallationConflictFlags.None)
                {
                    LastRefusalReason = $"Refusing reapply on {installation.DisplayName}; installation has conflict flags: {installation.ConflictFlags}.";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }

                if (!DxvkCompatibility.IsInstallationSupported(installation, profile.Api, profile.ExeName, out var refusalReason))
                {
                    LastRefusalReason = refusalReason;
                    installation.LastRefusalReason = refusalReason;
                    _gameLibraryStore.Save(installation, leaseToken.StoreLease);
                    Logger.Log($"DxvkInstaller: refusing reapply on {installation.DisplayName}: {refusalReason}");
                    return false;
                }

                string version = installation.ManagedDxvkVersion;
                string arch = installation.ManagedDxvkArchitecture ?? (string.Equals(profile.Architecture, "x32", StringComparison.OrdinalIgnoreCase) ? "x32" : "x64");
                string versionDir = Path.Combine(_dxvkSourceDir, SanitizeVersion(version));
                string dxvkArchDir = Path.Combine(versionDir, arch);

                if (DxvkReleaseAcquirer.HasAcquisitionRecord(versionDir) &&
                    !DxvkLocalReleaseLibrary.IsVersionAvailable(_dxvkSourceDir, version, profile.Api, arch))
                {
                    LastRefusalReason = "Acquired DXVK source verification failed. Inspect it through Browse / Download official versions.";
                    return false;
                }

                if (!Directory.Exists(dxvkArchDir))
                {
                    LastRefusalReason = $"Source DXVK directory not found for version {version} ({arch}).";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }

                string[] dllsToDeploy = DxvkCompatibility.GetRequiredDlls(profile.Api);
                if (dllsToDeploy.Length == 0)
                {
                    LastRefusalReason = $"{profile.ExeName} has unsupported API ({profile.Api}).";
                    Logger.Log($"DxvkInstaller: {profile.ExeName} has unsupported API ({profile.Api}); skipping reapply.");
                    return false;
                }

                foreach (var dllName in dllsToDeploy)
                {
                    var src = Path.Combine(dxvkArchDir, dllName);
                    if (!File.Exists(src))
                    {
                        LastRefusalReason = $"Required source DXVK file missing: {dllName}.";
                        Logger.Log($"DxvkInstaller: required source DXVK file missing: {src}");
                        return false;
                    }
                }

                preOperationSnapshot = _gameLibraryStore.CreateSnapshot(installation);
                _gameLibraryStore.CaptureCurrentLibraryBaseline(out var preOperationLibraryBytes, out bool preOperationLibraryExisted);

                string relGameDir = Path.GetRelativePath(installation.InstallationPath, gameDir);
                string ResolveInstallationRelativeFilePath(string fileName) =>
                    string.IsNullOrEmpty(relGameDir) || relGameDir == "."
                        ? fileName
                        : GameInstallation.NormalizeRelativePath(Path.Combine(relGameDir, fileName));

                var filesToProcess = new List<MultiFileTransactionFile>();

                foreach (var dllName in dllsToDeploy)
                {
                    string relFilePath = ResolveInstallationRelativeFilePath(dllName);
                    string targetPath = Path.Combine(installation.InstallationPath, relFilePath);
                    string sourcePath = Path.Combine(dxvkArchDir, dllName);
                    var sourceIdentity = FileIdentity.Capture(sourcePath);

                    var existingRecord = installation.FindManagedFile(relFilePath);
                    OriginalFileState originalState;
                    string? backupRelativePath;
                    SafetyFileIdentity? expectedTargetIdentity = null;
                    bool replaceExistingBackup = false;
                    SafetyFileIdentity? expectedPriorBackupIdentity = null;
                    bool updateOriginalSha256OnCommit = false;

                    if (existingRecord != null && existingRecord.OriginalState != FileOriginalState.Unknown)
                    {
                        originalState = existingRecord.OriginalState switch
                        {
                            FileOriginalState.Existing => OriginalFileState.Existing,
                            FileOriginalState.Missing => OriginalFileState.DidNotExist,
                            _ => OriginalFileState.Unknown
                        };
                        backupRelativePath = existingRecord.BackupRelativePath ?? (originalState == OriginalFileState.Existing ? Path.Combine(installation.Id, relFilePath) : null);
                        if (File.Exists(targetPath))
                        {
                            expectedTargetIdentity = FileIdentity.Capture(targetPath);
                        }

                        if (originalState == OriginalFileState.Existing && !string.IsNullOrEmpty(backupRelativePath))
                        {
                            string fullExistingBackup = Path.Combine(_transactionEngine.TransactionStoreRoot, backupRelativePath);
                            if (File.Exists(fullExistingBackup) && !string.IsNullOrEmpty(existingRecord.OriginalSha256))
                            {
                                var existingBackupIdentity = FileIdentity.Capture(fullExistingBackup);
                                if (!string.Equals(existingBackupIdentity.Sha256, existingRecord.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                                {
                                    _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                                    LastRefusalReason = $"Refusing reapply on {installation.DisplayName}; existing backup for {relFilePath} at {backupRelativePath} was corrupted or modified.";
                                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                                    return false;
                                }
                            }
                        }
                    }
                    else
                    {
                        if (File.Exists(targetPath))
                        {
                            originalState = OriginalFileState.Existing;
                            expectedTargetIdentity = FileIdentity.Capture(targetPath);
                            backupRelativePath = ResolveSafeNewBackupRelativePath(installation.Id, relFilePath, expectedTargetIdentity);
                            if (backupRelativePath == null)
                            {
                                _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                                LastRefusalReason = $"Refusing reapply on {installation.DisplayName}; unresolvable backup collision for {relFilePath}.";
                                Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                                return false;
                            }
                            updateOriginalSha256OnCommit = true;
                        }
                        else
                        {
                            originalState = OriginalFileState.DidNotExist;
                            expectedTargetIdentity = null;
                            backupRelativePath = null;
                        }
                    }

                    // Section 18.2: If updating baseline after an external change (e.g. game update),
                    // the newly observed game file becomes the new restoration baseline inside the transaction engine.
                    if (updateBaseline && File.Exists(targetPath))
                    {
                        var currentIdentity = FileIdentity.Capture(targetPath);
                        if (existingRecord != null && existingRecord.OriginalState != FileOriginalState.Unknown)
                        {
                            if (!string.Equals(currentIdentity.Sha256, existingRecord.ExpectedManagedSha256, StringComparison.OrdinalIgnoreCase))
                            {
                                backupRelativePath = existingRecord.BackupRelativePath ?? ResolveSafeNewBackupRelativePath(installation.Id, relFilePath, currentIdentity);
                                if (backupRelativePath == null)
                                {
                                    _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                                    LastRefusalReason = $"Refusing reapply with updateBaseline on {installation.DisplayName}; unresolvable backup collision for {relFilePath}.";
                                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                                    return false;
                                }

                                string fullBackupPath = Path.Combine(_transactionEngine.TransactionStoreRoot, backupRelativePath);
                                if (File.Exists(fullBackupPath))
                                {
                                    replaceExistingBackup = true;
                                    expectedPriorBackupIdentity = FileIdentity.Capture(fullBackupPath);
                                }

                                originalState = OriginalFileState.Existing;
                                expectedTargetIdentity = currentIdentity;
                                updateOriginalSha256OnCommit = true;
                                Logger.Log($"DxvkInstaller: prepared transactional restoration baseline update for {relFilePath} in {installation.DisplayName} to hash {currentIdentity.Sha256[..Math.Min(8, currentIdentity.Sha256.Length)]}.");
                            }
                        }
                        else
                        {
                            backupRelativePath = ResolveSafeNewBackupRelativePath(installation.Id, relFilePath, currentIdentity);
                            if (backupRelativePath == null)
                            {
                                _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                                LastRefusalReason = $"Refusing reapply with updateBaseline on {installation.DisplayName}; unresolvable backup collision for {relFilePath}.";
                                Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                                return false;
                            }

                            originalState = OriginalFileState.Existing;
                            expectedTargetIdentity = currentIdentity;
                            updateOriginalSha256OnCommit = true;
                            Logger.Log($"DxvkInstaller: prepared initial restoration baseline for {relFilePath} in {installation.DisplayName} to hash {currentIdentity.Sha256[..Math.Min(8, currentIdentity.Sha256.Length)]}.");
                        }
                    }

                    filesToProcess.Add(new MultiFileTransactionFile
                    {
                        RelativePath = relFilePath,
                        SourceFilePath = sourcePath,
                        ExpectedSourceIdentity = sourceIdentity,
                        ExpectedTargetIdentity = expectedTargetIdentity,
                        OriginalState = originalState,
                        BackupRelativePath = backupRelativePath,
                        ReplaceExistingBackup = replaceExistingBackup,
                        ExpectedPriorBackupIdentity = expectedPriorBackupIdentity,
                        UpdateOriginalSha256OnCommit = updateOriginalSha256OnCommit
                    });
                }

                var config = new DxvkConfiguration
                {
                    HudEnabled = profile.HudEnabled,
                    FrameLimit = profile.FrameLimit,
                    FrameLimitEnabled = profile.FrameLimit > 0
                };

                string relConfPath = ResolveInstallationRelativeFilePath(DxvkConfigManager.ConfigFileName);
                string targetConfPath = Path.Combine(installation.InstallationPath, relConfPath);
                var managedConf = installation.FindManagedFile(relConfPath);
                bool updateManagedConf = managedConf != null
                    && managedConf.OriginalState != FileOriginalState.Unknown
                    && !string.IsNullOrEmpty(managedConf.ManagedDxvkVersion)
                    && File.Exists(targetConfPath);
                if (DxvkConfigManager.RequiresConfigFile(config) || updateManagedConf)
                {
                    stagingDir = Path.Combine(_transactionEngine.TransactionStoreRoot, ".staging", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stagingDir);

                    string? existingConf = File.Exists(targetConfPath) ? File.ReadAllText(targetConfPath) : null;
                    string? confContent = updateManagedConf
                        ? DxvkConfigManager.GenerateManagedConfigContent(config, existingConf!)
                        : DxvkConfigManager.GenerateConfigContent(config, existingConf);

                    if (!string.IsNullOrEmpty(confContent))
                    {
                        string stagedConfPath = Path.Combine(stagingDir, DxvkConfigManager.ConfigFileName);
                        File.WriteAllText(stagedConfPath, confContent);
                        var stagedConfIdentity = FileIdentity.Capture(stagedConfPath);

                        var existingConfRecord = installation.FindManagedFile(relConfPath);
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
                            confBackupRelativePath = existingConfRecord.BackupRelativePath ?? (confOriginalState == OriginalFileState.Existing ? Path.Combine(installation.Id, relConfPath) : null);
                            if (File.Exists(targetConfPath))
                            {
                                expectedConfTargetIdentity = FileIdentity.Capture(targetConfPath);
                            }

                            if (confOriginalState == OriginalFileState.Existing && !string.IsNullOrEmpty(confBackupRelativePath))
                            {
                                string fullExistingConfBackup = Path.Combine(_transactionEngine.TransactionStoreRoot, confBackupRelativePath);
                                if (File.Exists(fullExistingConfBackup) && !string.IsNullOrEmpty(existingConfRecord.OriginalSha256))
                                {
                                    var existingBackupIdentity = FileIdentity.Capture(fullExistingConfBackup);
                                    if (!string.Equals(existingBackupIdentity.Sha256, existingConfRecord.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                                    {
                                        _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                                        LastRefusalReason = $"Refusing reapply on {installation.DisplayName}; existing backup for {relConfPath} at {confBackupRelativePath} was corrupted or modified.";
                                        Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                                        return false;
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (File.Exists(targetConfPath))
                            {
                                confOriginalState = OriginalFileState.Existing;
                                expectedConfTargetIdentity = FileIdentity.Capture(targetConfPath);
                                confBackupRelativePath = ResolveSafeNewBackupRelativePath(installation.Id, relConfPath, expectedConfTargetIdentity);
                                if (confBackupRelativePath == null)
                                {
                                    _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                                    LastRefusalReason = $"Refusing reapply on {installation.DisplayName}; unresolvable backup collision for {relConfPath}.";
                                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                                    return false;
                                }
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
                            RelativePath = relConfPath,
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
                    InstallationRoot = installation.InstallationPath,
                    Operation = TransactionOperation.Reapply,
                    Files = filesToProcess
                };

                bool saveCallbackInvoked = false;
                OwnershipSaveResult lastSaveResult = new OwnershipSaveResult(
                    OwnershipPersistenceOutcome.Unknown,
                    DiagnosticMessage: "Ownership commit callback did not complete.");

                var result = _transactionEngine.ExecuteWithCommitScope(request, committedFiles =>
                {
                    saveCallbackInvoked = true;
                    try
                    {
                        installation.Configuration = config;

                        foreach (var filePlan in committedFiles)
                        {
                            var record = installation.GetOrAddManagedFile(filePlan.RelativePath);
                            var previousOriginalState = record.OriginalState;
                            record.OriginalState = filePlan.OriginalState switch
                            {
                                OriginalFileState.Existing => FileOriginalState.Existing,
                                OriginalFileState.DidNotExist => FileOriginalState.Missing,
                                _ => FileOriginalState.Unknown
                            };
                            record.BackupRelativePath = filePlan.BackupRelativePath;
                            if (filePlan.OriginalState == OriginalFileState.Existing &&
                                filePlan.ExpectedTargetIdentity != null &&
                                (filePlan.UpdateOriginalSha256OnCommit || string.IsNullOrEmpty(record.OriginalSha256) || previousOriginalState != FileOriginalState.Existing))
                            {
                                record.OriginalSha256 = filePlan.ExpectedTargetIdentity.Sha256;
                            }
                            record.ExpectedManagedSha256 = filePlan.ExpectedSourceIdentity?.Sha256;
                            record.ManagedDxvkVersion = filePlan.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? version : null;
                            record.CurrentState = ManagedFileState.Consistent;
                            record.LastVerifiedUtc = DateTime.UtcNow;
                        }

                        installation.RestorationState = RestorationState.Managed;
                        installation.ConflictFlags = InstallationConflictFlags.None;
                        installation.LastRefusalReason = null;
                        installation.PendingAction = null;
                        installation.LastSeenUtc = DateTime.UtcNow;

                        lastSaveResult = _gameLibraryStore.TrySaveWithOutcome(installation, leaseToken.StoreLease);
                        return lastSaveResult;
                    }
                    catch (Exception callbackEx)
                    {
                        string reason = $"Unexpected exception in ownership commit callback: {callbackEx.GetType().Name} - {callbackEx.Message}";
                        _gameLibraryStore.RecordUnexpectedCommitCallbackException(reason, installation);
                        lastSaveResult = new OwnershipSaveResult(
                            OwnershipPersistenceOutcome.Unknown,
                            Exception: callbackEx,
                            DiagnosticMessage: reason,
                            PreWriteBytes: preOperationLibraryBytes,
                            PreWriteExisted: preOperationLibraryExisted);
                        throw;
                    }
                });

                if (result.State == TransactionState.Committed && result.Outcome == TransactionOutcome.Success)
                {
                    LastRefusalReason = null;
                    Logger.Log($"DxvkInstaller: successfully reapplied DXVK {version} to {profile.ExeName} via safe transaction {result.TransactionId}.");
                    return true;
                }

                string failureMessage = result.Message ?? $"Reapply transaction failed ({result.State}/{result.Outcome}).";
                LastRefusalReason = failureMessage;

                if (result.State == TransactionState.AttentionRequired || result.Outcome == TransactionOutcome.UnresolvedRecovery)
                {
                    if (saveCallbackInvoked && lastSaveResult.Outcome == OwnershipPersistenceOutcome.Unknown)
                    {
                        installation.RestorationState = RestorationState.AttentionRequired;
                        installation.LastRefusalReason = failureMessage;
                    }
                    else
                    {
                        _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                        installation.RestorationState = RestorationState.AttentionRequired;
                        installation.LastRefusalReason = failureMessage;

                        byte[]? expectedPreWriteBytes = saveCallbackInvoked ? lastSaveResult.PreWriteBytes : preOperationLibraryBytes;
                        bool expectedPreWriteExisted = saveCallbackInvoked ? lastSaveResult.PreWriteExisted : preOperationLibraryExisted;
                        _gameLibraryStore.TrySaveHealthAfterFailedCompensation(installation, expectedPreWriteBytes, expectedPreWriteExisted, leaseToken.StoreLease);

                        installation.RestorationState = RestorationState.AttentionRequired;
                        installation.LastRefusalReason = failureMessage;
                    }
                }
                else
                {
                    _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                }

                Logger.Log($"DxvkInstaller: reapply transaction failed ({result.State}/{result.Outcome}): {failureMessage}");
                return false;
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastRefusalReason = $"Unexpected error reapplying DXVK to {profile.ExeName}: {ex.GetType().Name} - {ex.Message}";
                if (installation != null && preOperationSnapshot != null)
                {
                    if (_gameLibraryStore.HasUncertainPersistence)
                    {
                        installation.RestorationState = RestorationState.AttentionRequired;
                        installation.LastRefusalReason = LastRefusalReason;
                    }
                    else
                    {
                        _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                    }
                }
                Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                return false;
            }
            finally
            {
                if (stagingDir != null)
                {
                    try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
                }
                if (acquiredLease != null)
                {
                    await acquiredLease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        public bool AdoptExisting(GameProfile profile, ExistingDxvkAssessment assessment)
        {
            return AdoptExisting(profile, assessment, leaseToken: null);
        }

        internal bool AdoptExisting(GameProfile profile, ExistingDxvkAssessment assessment, IOperationLease? leaseToken)
        {
            LastRefusalReason = null;
            GameInstallation? installation = null;
            GameInstallation? preOperationSnapshot = null;
            IOperationLease? acquiredLease = null;
            try
            {
                if (!DxvkCompatibility.IsDxvkSupported(profile.Api))
                {
                    LastRefusalReason = $"Cannot adopt DXVK for {profile.ExeName}; API {profile.Api} is not supported for DXVK.";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }

                if (!assessment.CanBeAdopted || string.IsNullOrEmpty(assessment.MatchedVersion))
                {
                    LastRefusalReason = $"Existing DLLs cannot be adopted (status: {assessment.Status}).";
                    Logger.Log($"DxvkInstaller: cannot adopt existing DXVK for {profile.ExeName}; status is {assessment.Status}.");
                    return false;
                }

                if (!DxvkLocalReleaseLibrary.IsRecognizedSourceEligible(_dxvkSourceDir, assessment.MatchedVersion, profile.Api, profile.Architecture))
                {
                    LastRefusalReason = "Existing DXVK source is unpublished, invalid or unsupported; adoption refused.";
                    return false;
                }

                var existingInstallation = _gameLibraryStore.FindInstallationForExecutable(profile.ExePath)
                    ?? _gameLibraryStore.FindByInstallationPath(Path.GetDirectoryName(profile.ExePath) ?? string.Empty);

                string rawDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawDir) || !Directory.Exists(rawDir))
                {
                    LastRefusalReason = $"Invalid game directory for {profile.ExePath}.";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }

                // Resolve authoritative installation root
                string authoritativeRoot = existingInstallation != null && !string.IsNullOrWhiteSpace(existingInstallation.InstallationPath)
                    ? existingInstallation.InstallationPath
                    : GameInstallation.NormalizeInstallationPath(rawDir);
                string gameDir = rawDir;

                if (leaseToken != null)
                {
                    if (leaseToken.IsDisposed || !leaseToken.IsAcquired ||
                        !leaseToken.IsValidForTarget(authoritativeRoot) ||
                        (!leaseToken.IsValidForStore(_gameLibraryStore.LibraryFilePath) && (leaseToken.StoreLease == null || !leaseToken.StoreLease.IsValidForStore(_gameLibraryStore.LibraryFilePath))) ||
                        (ProfileStore != null && !string.IsNullOrWhiteSpace(ProfileStore.ProfilesFile) && !leaseToken.IsValidForStore(ProfileStore.ProfilesFile) && (leaseToken.StoreLease == null || !leaseToken.StoreLease.IsValidForStore(ProfileStore.ProfilesFile))))
                    {
                        throw new ArgumentException("Invalid, mismatched, or incomplete lease token for installation and store scope.", nameof(leaseToken));
                    }
                }
                else
                {
                    var storePaths = new List<string> { _gameLibraryStore.LibraryFilePath };
                    if (ProfileStore != null && !string.IsNullOrWhiteSpace(ProfileStore.ProfilesFile))
                    {
                        storePaths.Add(ProfileStore.ProfilesFile);
                    }
                    acquiredLease = LeaseCoordinator.TryAcquireOperationScope(
                        authoritativeRoot,
                        storePaths,
                        TimeSpan.FromMilliseconds(500));

                    if (acquiredLease == null)
                    {
                        LastRefusalReason = LeaseCoordinator.LastAcquisitionError ??
                            $"Operation in progress for installation: {authoritativeRoot}. Please retry shortly.";
                        Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                        return false;
                    }
                    leaseToken = acquiredLease;
                }

                var candidateExes = new List<string> { profile.ExeName };
                if (existingInstallation?.Executables != null)
                {
                    candidateExes.AddRange(existingInstallation.Executables.Select(e => e.RelativePath));
                }
                var activity = ActivityInspector.GetActivityEvidence(authoritativeRoot, candidateExes);
                if (activity == ProcessActivityEvidence.Running)
                {
                    LastRefusalReason = $"Game is currently running: {authoritativeRoot}. Close the game before modifying files.";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }
                if (activity == ProcessActivityEvidence.Unknown)
                {
                    LastRefusalReason = "Cannot verify game activity: candidate process access denied (protected or elevated).";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }

                if (!_gameLibraryStore.TryCheckCanModifyLibrary(out var storeRefusalReason, leaseToken.StoreLease))
                {
                    LastRefusalReason = storeRefusalReason;
                    if (existingInstallation != null)
                    {
                        existingInstallation.RestorationState = RestorationState.AttentionRequired;
                        existingInstallation.LastRefusalReason = storeRefusalReason;
                    }
                    Logger.Log($"DxvkInstaller: refusing to adopt existing DXVK for {profile.ExeName}: {storeRefusalReason}");
                    return false;
                }

                if (ProfileStore != null && !ProfileStore.TryCheckCanModifyProfileStore(out var profileRefusalReason, GetProfileStoreLease(leaseToken)))
                {
                    LastRefusalReason = profileRefusalReason;
                    if (existingInstallation != null)
                    {
                        existingInstallation.RestorationState = RestorationState.AttentionRequired;
                        existingInstallation.LastRefusalReason = profileRefusalReason;
                    }
                    Logger.Log($"DxvkInstaller: refusing to adopt existing DXVK for {profile.ExeName}: {profileRefusalReason}");
                    return false;
                }

                installation = existingInstallation
                    ?? _gameLibraryStore.GetOrCreateInstallation(authoritativeRoot, Path.GetFileNameWithoutExtension(profile.ExePath), leaseToken.StoreLease);

                if (installation.ConflictFlags != InstallationConflictFlags.None)
                {
                    LastRefusalReason = $"Refusing to adopt existing DXVK for {profile.ExeName}; installation has conflicts: {installation.ConflictFlags}.";
                    Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                    return false;
                }

                if (!DxvkCompatibility.IsInstallationSupported(installation, profile.Api, profile.ExeName, out var refusalReason))
                {
                    LastRefusalReason = refusalReason;
                    installation.LastRefusalReason = refusalReason;
                    _gameLibraryStore.Save(installation, leaseToken.StoreLease);
                    Logger.Log($"DxvkInstaller: refusing to adopt existing DXVK for {profile.ExeName}: {refusalReason}");
                    return false;
                }

                preOperationSnapshot = _gameLibraryStore.CreateSnapshot(installation);

                string relGameDir = Path.GetRelativePath(installation.InstallationPath, gameDir);
                string ResolveInstallationRelativeFilePath(string fileName) =>
                    string.IsNullOrEmpty(relGameDir) || relGameDir == "."
                        ? fileName
                        : GameInstallation.NormalizeRelativePath(Path.Combine(relGameDir, fileName));

                string arch = string.Equals(profile.Architecture, "x32", StringComparison.OrdinalIgnoreCase) ? "x32" : "x64";

                foreach (var dllName in assessment.DetectedDlls)
                {
                    string relFilePath = ResolveInstallationRelativeFilePath(dllName);
                    string targetPath = Path.Combine(installation.InstallationPath, relFilePath);
                    if (!File.Exists(targetPath))
                    {
                        _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                        LastRefusalReason = $"Detected DLL missing during adoption: {dllName}.";
                        Logger.Log($"DxvkInstaller: detected DLL missing during adoption: {targetPath}");
                        return false;
                    }

                    var targetIdentity = FileIdentity.Capture(targetPath);
                    var record = installation.GetOrAddManagedFile(relFilePath);
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
                installation.LastRefusalReason = null;
                installation.LastSeenUtc = DateTime.UtcNow;

                string relExe = Path.GetRelativePath(installation.InstallationPath, profile.ExePath);
                var executable = installation.GetOrAddExecutable(relExe, profile.ExeName);
                executable.LastKnownApi = profile.Api;
                executable.LastKnownArchitecture = arch;

                var saveResult = _gameLibraryStore.TrySaveWithOutcome(installation, leaseToken.StoreLease);
                if (saveResult.Outcome == OwnershipPersistenceOutcome.Committed)
                {
                    LastRefusalReason = null;
                    profile.DxvkEnabled = true;
                    profile.DxvkVersion = assessment.MatchedVersion;
                    Logger.Log($"DxvkInstaller: successfully adopted official DXVK {assessment.MatchedVersion} for {profile.ExeName}.");
                    return true;
                }

                LastRefusalReason = saveResult.DiagnosticMessage
                    ?? saveResult.Exception?.Message
                    ?? "Failed to persist adopted ownership metadata.";

                if (saveResult.Outcome == OwnershipPersistenceOutcome.ProvenNotCommitted)
                {
                    _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                }
                else
                {
                    installation.RestorationState = RestorationState.AttentionRequired;
                    installation.LastRefusalReason = LastRefusalReason;
                }

                Logger.Log($"DxvkInstaller: adoption persistence failed ({saveResult.Outcome}): {LastRefusalReason}");
                return false;
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastRefusalReason = $"Unexpected error adopting DXVK for {profile.ExeName}: {ex.GetType().Name} - {ex.Message}";
                if (installation != null && preOperationSnapshot != null)
                {
                    if (_gameLibraryStore.HasUncertainPersistence)
                    {
                        installation.RestorationState = RestorationState.AttentionRequired;
                        installation.LastRefusalReason = LastRefusalReason;
                    }
                    else
                    {
                        _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                    }
                }
                Logger.Log($"DxvkInstaller: {LastRefusalReason}");
                return false;
            }
            finally
            {
                acquiredLease?.Dispose();
            }
        }

        private string? ResolveSafeNewBackupRelativePath(string installationId, string fileName, SafetyFileIdentity expectedTargetIdentity)
        {
            string defaultRel = Path.Combine(installationId, fileName);
            string defaultFull = Path.Combine(_transactionEngine.TransactionStoreRoot, defaultRel);

            if (!File.Exists(defaultFull))
            {
                return defaultRel;
            }

            var defaultIdentity = FileIdentity.Capture(defaultFull);
            if (defaultIdentity == expectedTargetIdentity)
            {
                return defaultRel;
            }

            // A collision exists: defaultFull has different bytes.
            // Select a collision-safe path that preserves the pre-existing file and stores the new baseline safely.
            string ext = Path.GetExtension(fileName);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string dir = Path.GetDirectoryName(fileName) ?? string.Empty;
            string hashPrefix = expectedTargetIdentity.Sha256[..Math.Min(8, expectedTargetIdentity.Sha256.Length)];
            string safeRel = string.IsNullOrEmpty(dir)
                ? Path.Combine(installationId, $"{nameWithoutExt}.baseline-{hashPrefix}{ext}")
                : Path.Combine(installationId, dir, $"{nameWithoutExt}.baseline-{hashPrefix}{ext}");
            string safeFull = Path.Combine(_transactionEngine.TransactionStoreRoot, safeRel);

            if (!File.Exists(safeFull))
            {
                return safeRel;
            }

            var safeIdentity = FileIdentity.Capture(safeFull);
            if (safeIdentity == expectedTargetIdentity)
            {
                return safeRel;
            }

            // Both default and collision-safe paths exist with mismatched content; refuse rather than overwrite or corrupt.
            return null;
        }
    }
}
