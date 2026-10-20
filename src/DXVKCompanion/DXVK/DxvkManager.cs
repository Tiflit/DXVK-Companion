using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;

namespace DXVKCompanion.DXVK
{
    public enum DxvkActionResult
    {
        Applied,
        Queued,
        Failed
    }

    public class DxvkManager
    {
        private enum PendingAction { None = 0, Enable = 1, Disable = 2 }

        private readonly DxvkInstaller _installer;
        private readonly DxvkRollback _rollback;
        private readonly DxvkGithubClient _github;
        private readonly ProfileStore _profiles;
        private readonly GameLibraryStore _gameLibraryStore;
        private readonly DxvkConfigManager _config;
        private readonly ConcurrentDictionary<string, PendingAction> _pending = new(StringComparer.OrdinalIgnoreCase);
        public string? LastRefusalReason { get; private set; }
        public string DxvkSourceDir => _installer.DxvkSourceDir;
        public Task<DxvkGithubClient.OfficialReleasePage> BrowseOfficialReleasesAsync(int page, CancellationToken token = default) =>
            _github.FetchOfficialReleasePageAsync(page, token);
        public Task<DxvkReleaseAcquirer.AcquisitionResult> AcquireOfficialReleaseAsync(string tag, CancellationToken token = default) =>
            _installer.GetReleaseAcquirer(_github).AcquireAsync(tag, token);
        public event Action<GameInstallation, Models.PendingAction, string>? OnPendingActionCancelled;

        internal IOperationLeaseCoordinator LeaseCoordinator { get; set; } = OperationLeaseCoordinator.Default;
        internal IProcessActivityInspector ActivityInspector { get; set; } = ProcessActivityInspector.Default;

        public bool HasTransientPendingAction(string exePath) =>
            _pending.TryGetValue(exePath, out var a) && a != PendingAction.None;

        public bool CanAutomaticallyDeploy(GameInstallation? installation)
        {
            if (installation == null) return true;
            return installation.LastCancelledAction == null;
        }

        public bool CanAutomaticallyDeploy(string exePath)
        {
            var installation = ResolveInstallation(exePath);
            return CanAutomaticallyDeploy(installation);
        }

        public void ClearCancelledState(GameInstallation installation)
        {
            using var lease = LeaseCoordinator.TryAcquireStoreLease(_gameLibraryStore.LibraryFilePath, TimeSpan.FromMilliseconds(500));
            ClearCancelledState(installation, lease);
        }

        internal void ClearCancelledState(GameInstallation installation, IOperationLease? storeLease)
        {
            if (storeLease == null || !storeLease.IsAcquired || storeLease.IsDisposed || !storeLease.IsValidForStore(_gameLibraryStore.LibraryFilePath))
            {
                return;
            }

            // Authoritative preflight check before modifying in-memory state
            if (!_gameLibraryStore.TryCheckCanModifyLibrary(out _, storeLease))
            {
                return;
            }

            if (installation.LastCancelledAction != null)
            {
                var prevAction = installation.LastCancelledAction;
                var prevReason = installation.LastCancellationReason;
                var prevOutcome = installation.LastCancellationOutcome;
                var prevRefusal = installation.LastRefusalReason;

                installation.LastCancelledAction = null;
                installation.LastCancellationReason = null;
                installation.LastCancellationOutcome = null;
                if (installation.LastRefusalReason?.StartsWith("Cancelled pending", StringComparison.OrdinalIgnoreCase) == true)
                {
                    installation.LastRefusalReason = null;
                }

                try
                {
                    _gameLibraryStore.Save(installation, storeLease);
                }
                catch
                {
                    installation.LastCancelledAction = prevAction;
                    installation.LastCancellationReason = prevReason;
                    installation.LastCancellationOutcome = prevOutcome;
                    if (!_gameLibraryStore.HasUncertainPersistence)
                    {
                        installation.LastRefusalReason = prevRefusal;
                    }
                    throw;
                }
            }
        }

        private int _processingPendingBatchDepth;

        private void TrySaveProfileNonFatal(GameProfile profile, string context, IOperationLease? storeLease = null)
        {
            try
            {
                _profiles.Save(profile, storeLease);
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkManager: non-fatal ProfileStore.Save failure during {context} for {profile.ExeName}: {ex.GetType().Name} - {ex.Message}");
                if (_processingPendingBatchDepth > 0 && !string.Equals(context, "reconciliation", StringComparison.Ordinal))
                {
                    throw;
                }
            }
        }

        public DxvkManager(
            DxvkInstaller installer,
            DxvkRollback rollback,
            DxvkGithubClient github,
            ProfileStore profiles,
            GameLibraryStore? gameLibraryStore = null)
        {
            _installer = installer;
            _rollback = rollback;
            _github = github;
            _profiles = profiles;
            _gameLibraryStore = gameLibraryStore ?? new GameLibraryStore();
            _config = new DxvkConfigManager();
            if (_installer != null)
            {
                _installer.ProfileStore = _profiles;
            }
        }

        internal DxvkManager(
            DxvkInstaller installer,
            DxvkRollback rollback,
            DxvkGithubClient github,
            ProfileStore profiles,
            GameLibraryStore? gameLibraryStore = null,
            IOperationLeaseCoordinator? leaseCoordinator = null,
            IProcessActivityInspector? activityInspector = null)
            : this(installer, rollback, github, profiles, gameLibraryStore)
        {
            if (leaseCoordinator != null)
            {
                LeaseCoordinator = leaseCoordinator;
                if (_installer != null) _installer.LeaseCoordinator = leaseCoordinator;
                if (_rollback != null) _rollback.LeaseCoordinator = leaseCoordinator;
                if (_profiles != null) _profiles.LeaseCoordinator = leaseCoordinator;
                if (_gameLibraryStore != null) _gameLibraryStore.LeaseCoordinator = leaseCoordinator;
            }
            if (activityInspector != null)
            {
                ActivityInspector = activityInspector;
                if (_installer != null) _installer.ActivityInspector = activityInspector;
                if (_rollback != null) _rollback.ActivityInspector = activityInspector;
            }
        }

        private GameInstallation? ResolveInstallation(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return null;
            string gameDir = Path.GetDirectoryName(exePath) ?? string.Empty;
            return _gameLibraryStore.FindInstallationForExecutable(exePath)
                ?? (!string.IsNullOrWhiteSpace(gameDir) ? _gameLibraryStore.FindByInstallationPath(gameDir) : null);
        }

        /// <summary>
        /// Reassesses the effective API and architecture of an executable and its installation
        /// using the latest recorded evidence in both ProfileStore and GameLibraryStore.
        /// When evidence contains both translatable (DX9/10/11) and unsupported (DX12/Vulkan/Unknown) records,
        /// unsupported evidence conservatively takes precedence to prevent catastrophic DXVK deployment into native modern engines.
        /// Supported library records never erase unsupported profile evidence without established freshness,
        /// and observed Unknown classifications are never promoted to supported from a stale caller profile.
        /// </summary>
        internal void ReassessEffectiveApi(GameInstallation? installation, GameProfile profile, IOperationLease? storeLease = null)
        {
            if (storeLease == null || !storeLease.IsAcquired || storeLease.IsDisposed)
            {
                return;
            }

            // Authoritative preflight checks before modifying in-memory state:
            // 1. Profile store must be modifiable if configured
            if (!_profiles.TryCheckCanModifyProfileStore(out _, storeLease))
            {
                return;
            }

            // 2. Library store must be modifiable if installation is present
            if (installation != null && !_gameLibraryStore.TryCheckCanModifyLibrary(out _, storeLease))
            {
                return;
            }

            // Snapshot entry state to preserve on error/failure
            string? prevProfileArch = profile.Architecture;
            bool prevProfileDxvkEnabled = profile.DxvkEnabled;
            string? prevProfileDxvkVersion = profile.DxvkVersion;
            GraphicsApi prevProfileApi = profile.Api;

            string? prevManagedArch = installation?.ManagedDxvkArchitecture;
            var prevExeArchitectures = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var prevExeApis = new Dictionary<string, GraphicsApi>(StringComparer.OrdinalIgnoreCase);
            if (installation?.Executables != null)
            {
                foreach (var exe in installation.Executables)
                {
                    prevExeArchitectures[exe.RelativePath] = exe.LastKnownArchitecture;
                    prevExeApis[exe.RelativePath] = exe.LastKnownApi;
                }
            }

            Action revertProfile = () =>
            {
                profile.Architecture = prevProfileArch;
                profile.DxvkEnabled = prevProfileDxvkEnabled;
                profile.DxvkVersion = prevProfileDxvkVersion;
                profile.Api = prevProfileApi;
            };

            Action revertInstallation = () =>
            {
                if (installation != null)
                {
                    installation.ManagedDxvkArchitecture = prevManagedArch;
                    if (installation.Executables != null)
                    {
                        foreach (var exe in installation.Executables)
                        {
                            if (prevExeArchitectures.TryGetValue(exe.RelativePath, out var a)) exe.LastKnownArchitecture = a;
                            if (prevExeApis.TryGetValue(exe.RelativePath, out var g)) exe.LastKnownApi = g;
                        }
                    }
                }
            };

            bool profileChanged = false;
            bool installationChanged = false;

            // Normalize profile architecture alias if recognized (e.g. x86/win32 -> x32, amd64/win64 -> x64)
            string? normProfileArch = DxvkLocalReleaseLibrary.NormalizeArchitecture(profile.Architecture);
            if (normProfileArch != null && !string.Equals(profile.Architecture, normProfileArch, StringComparison.Ordinal))
            {
                profile.Architecture = normProfileArch;
                profileChanged = true;
            }

            if (installation == null)
            {
                if (profileChanged || _profiles.HasPendingWriteRetry)
                {
                    try
                    {
                        _profiles.Save(profile, storeLease);
                    }
                    catch (Exception ex)
                    {
                        revertProfile();
                        Logger.Log($"DxvkManager: failed to save profile during reconciliation for {profile.ExeName}: {ex.GetType().Name} - {ex.Message}");
                    }
                }
                return;
            }

            // Reconcile secondary ProfileStore state from authoritative GameLibraryStore ownership state
            if (installation.RestorationState == RestorationState.Managed && !string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion))
            {
                string managedVersion = installation.ManagedDxvkVersion.Trim();
                if (!profile.DxvkEnabled)
                {
                    profile.DxvkEnabled = true;
                    profileChanged = true;
                }
                if (!string.Equals(profile.DxvkVersion, managedVersion, StringComparison.Ordinal))
                {
                    profile.DxvkVersion = managedVersion;
                    profileChanged = true;
                }
            }
            else if (installation.RestorationState == RestorationState.Restored && string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion))
            {
                if (profile.DxvkEnabled)
                {
                    profile.DxvkEnabled = false;
                    profileChanged = true;
                }
            }

            string relExe = Path.GetRelativePath(installation.InstallationPath, profile.ExePath);
            var recordedExe = installation.FindExecutable(relExe);

            string? normManagedArch = DxvkLocalReleaseLibrary.NormalizeArchitecture(installation.ManagedDxvkArchitecture);
            if (normManagedArch != null && !string.Equals(installation.ManagedDxvkArchitecture, normManagedArch, StringComparison.Ordinal))
            {
                installation.ManagedDxvkArchitecture = normManagedArch;
                installationChanged = true;
            }

            if (recordedExe != null)
            {
                string? normExeArch = DxvkLocalReleaseLibrary.NormalizeArchitecture(recordedExe.LastKnownArchitecture);
                if (normExeArch != null && !string.Equals(recordedExe.LastKnownArchitecture, normExeArch, StringComparison.Ordinal))
                {
                    recordedExe.LastKnownArchitecture = normExeArch;
                    installationChanged = true;
                }

                // Synchronize architecture if unknown
                if (string.Equals(profile.Architecture, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(recordedExe.LastKnownArchitecture) &&
                    !string.Equals(recordedExe.LastKnownArchitecture, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    profile.Architecture = recordedExe.LastKnownArchitecture;
                    profileChanged = true;
                }
                else if (string.Equals(recordedExe.LastKnownArchitecture, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(profile.Architecture, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    recordedExe.LastKnownArchitecture = profile.Architecture;
                    installationChanged = true;
                }

                // Startup hydration: if profile is unhydrated (Unknown), hydrate from known library record
                if (profile.Api == GraphicsApi.Unknown && recordedExe.LastKnownApi != GraphicsApi.Unknown)
                {
                    profile.Api = recordedExe.LastKnownApi;
                    profileChanged = true;
                }
                // Both supported: synchronize profile to latest library evidence
                else if (DxvkCompatibility.IsDxvkSupported(profile.Api) && DxvkCompatibility.IsDxvkSupported(recordedExe.LastKnownApi))
                {
                    if (profile.Api != recordedExe.LastKnownApi)
                    {
                        profile.Api = recordedExe.LastKnownApi;
                        profileChanged = true;
                    }
                }
            }

            if (installation.Executables != null)
            {
                var allProfiles = _profiles.GetAll().ToList();
                foreach (var exe in installation.Executables)
                {
                    if (exe == recordedExe) continue;
                    string siblingPath = Path.Combine(installation.InstallationPath, exe.RelativePath);
                    var siblingProfile = allProfiles.FirstOrDefault(p =>
                        string.Equals(p.ExePath, siblingPath, StringComparison.OrdinalIgnoreCase));

                    if (siblingProfile != null)
                    {
                        // Propagate unsupported evidence from siblingProfile only if sibling record is not already unsupported
                        if (!DxvkCompatibility.IsDxvkSupported(siblingProfile.Api) && siblingProfile.Api != GraphicsApi.Unknown)
                        {
                            if (exe.LastKnownApi != siblingProfile.Api)
                            {
                                exe.LastKnownApi = siblingProfile.Api;
                                installationChanged = true;
                            }
                        }
                    }
                }
            }

            if (storeLease != null)
            {
                bool profileSaved = false;
                try
                {
                    if (profileChanged || _profiles.HasPendingWriteRetry)
                    {
                        _profiles.Save(profile, storeLease);
                        profileSaved = true;
                    }
                    if (installationChanged && !_gameLibraryStore.HasUncertainPersistence)
                    {
                        _gameLibraryStore.Save(installation, storeLease);
                    }
                }
                catch (Exception ex)
                {
                    if (!profileSaved)
                    {
                        revertProfile();
                    }
                    if (!_gameLibraryStore.HasUncertainPersistence)
                    {
                        revertInstallation();
                    }
                    Logger.Log($"DxvkManager: failed to save during reconciliation for {profile.ExeName}: {ex.GetType().Name} - {ex.Message}");
                }
            }
        }

        public Task<ReleaseInfo?> GetLatestReleaseAsync() => _github.FetchLatestReleaseAsync();

        public bool UpdateAvailable(GameProfile profile, ReleaseInfo? latest = null)
        {
            var installation = ResolveInstallation(profile.ExePath);
            string authoritativeRoot = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(installation?.InstallationPath, profile.ExePath);
            if (!string.IsNullOrWhiteSpace(authoritativeRoot))
            {
                var storePaths = new List<string> { _gameLibraryStore.LibraryFilePath };
                if (!string.IsNullOrWhiteSpace(_profiles.ProfilesFile))
                {
                    storePaths.Add(_profiles.ProfilesFile);
                }

                using var scopeLease = LeaseCoordinator.TryAcquireOperationScope(
                    authoritativeRoot,
                    storePaths,
                    TimeSpan.FromMilliseconds(500));
                if (scopeLease != null && scopeLease.IsAcquired && !scopeLease.IsDisposed)
                {
                    ReassessEffectiveApi(installation, profile, scopeLease.StoreLease ?? scopeLease);
                }
            }

            if (!DxvkCompatibility.IsDxvkSupported(profile.Api))
                return false;

            if (!DxvkCompatibility.IsInstallationSupported(installation, profile.Api, profile.ExeName, out _))
                return false;

            string? currentVersion = (installation != null && !string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion))
                ? installation.ManagedDxvkVersion.Trim()
                : profile.DxvkVersion;

            string? pinned = installation?.ManagementPolicy?.PinnedDxvkVersion;
            if (!string.IsNullOrWhiteSpace(pinned))
            {
                return !string.Equals(currentVersion, pinned.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            if (latest == null)
                return false;

            if (string.IsNullOrWhiteSpace(currentVersion))
                return true;

            return !string.Equals(currentVersion, latest.Version, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStillRunning(Process process)
        {
            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Win32Exception)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        private bool IsPathCurrentlyRunning(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return false;
            var installation = ResolveInstallation(exePath);
            string authoritativeRoot = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(installation?.InstallationPath, exePath);
            if (string.IsNullOrWhiteSpace(authoritativeRoot) || !Directory.Exists(authoritativeRoot)) return false;
            var candidateExes = new List<string> { Path.GetFileName(exePath) };
            if (installation?.Executables != null)
            {
                candidateExes.AddRange(installation.Executables.Select(e => e.RelativePath));
            }
            var evidence = ActivityInspector.GetActivityEvidence(authoritativeRoot, candidateExes);
            return evidence == ProcessActivityEvidence.Running;
        }

        private async Task<DxvkActionResult> QueueOrApplyAsync(GameProfile profile, bool isRunning, PendingAction action, string? targetVersion = null)
        {
            LastRefusalReason = null;

            if (isRunning)
            {
                var existingInstall = ResolveInstallation(profile.ExePath);
                string authoritativeRoot = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(existingInstall?.InstallationPath, profile.ExePath);

                var storePaths = new List<string> { _gameLibraryStore.LibraryFilePath };
                if (!string.IsNullOrWhiteSpace(_profiles.ProfilesFile))
                {
                    storePaths.Add(_profiles.ProfilesFile);
                }

                using var scopeLease = await LeaseCoordinator.TryAcquireOperationScopeAsync(
                    authoritativeRoot,
                    storePaths,
                    TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

                if (scopeLease == null)
                {
                    LastRefusalReason = LeaseCoordinator.LastAcquisitionError ??
                        "Store operation in progress. Please retry shortly.";
                    return DxvkActionResult.Failed;
                }

                var storeLease = scopeLease.StoreLease ?? scopeLease;

                if (!_gameLibraryStore.TryCheckCanModifyLibrary(out var storeRefusalReason, storeLease))
                {
                    LastRefusalReason = storeRefusalReason;
                    return DxvkActionResult.Failed;
                }

                if (!_profiles.TryCheckCanModifyProfileStore(out var profileRefusalReason, storeLease))
                {
                    LastRefusalReason = profileRefusalReason;
                    return DxvkActionResult.Failed;
                }

                var installation = ResolveInstallation(profile.ExePath);
                if (installation != null)
                {
                    ClearCancelledState(installation, storeLease);
                }
                ReassessEffectiveApi(installation, profile, storeLease);

                if (action == PendingAction.Enable)
                {
                    if (!DxvkCompatibility.IsInstallationSupported(installation, profile.Api, profile.ExeName, out var refusalReason))
                    {
                        LastRefusalReason = refusalReason;
                        if (installation != null)
                        {
                            installation.LastRefusalReason = refusalReason;
                            if (!_gameLibraryStore.HasUncertainPersistence)
                            {
                                _gameLibraryStore.Save(installation, storeLease);
                            }
                        }
                        Logger.Log($"DxvkManager: refusing deployment for {profile.ExeName}: {refusalReason}");
                        return DxvkActionResult.Failed;
                    }
                }

                string effectiveTarget = !string.IsNullOrWhiteSpace(targetVersion)
                    ? targetVersion.Trim()
                    : (!string.IsNullOrWhiteSpace(installation?.ManagementPolicy?.PinnedDxvkVersion)
                        ? installation.ManagementPolicy.PinnedDxvkVersion.Trim()
                        : "latest");

                authoritativeRoot = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(installation?.InstallationPath, profile.ExePath);
                if (!string.IsNullOrWhiteSpace(authoritativeRoot))
                {
                    installation ??= _gameLibraryStore.GetOrCreateInstallation(authoritativeRoot, Path.GetFileNameWithoutExtension(profile.ExePath), storeLease);
                    string relPath = Path.GetRelativePath(installation.InstallationPath, profile.ExePath);
                    var existingExe = installation.FindExecutable(relPath);
                    var recordedExe = existingExe ?? installation.GetOrAddExecutable(relPath, profile.ExeName);
                    if (existingExe == null)
                    {
                        if (profile.Api != GraphicsApi.Unknown)
                        {
                            recordedExe.LastKnownApi = profile.Api;
                        }
                        if (!string.Equals(profile.Architecture, "Unknown", StringComparison.OrdinalIgnoreCase))
                        {
                            recordedExe.LastKnownArchitecture = profile.Architecture;
                        }
                    }
                    installation.PendingAction = action == PendingAction.Enable
                        ? Models.PendingAction.Install(effectiveTarget, "Queued while game running")
                        : Models.PendingAction.Restore("Queued while game running");
                    _gameLibraryStore.Save(installation, storeLease);
                }

                _pending[profile.ExePath] = action;
                return DxvkActionResult.Queued;
            }

            var install = ResolveInstallation(profile.ExePath);
            string effTarget = !string.IsNullOrWhiteSpace(targetVersion)
                ? targetVersion.Trim()
                : (!string.IsNullOrWhiteSpace(install?.ManagementPolicy?.PinnedDxvkVersion)
                    ? install.ManagementPolicy.PinnedDxvkVersion.Trim()
                    : "latest");

            bool ok = action == PendingAction.Enable
                ? await EnableDxvkAsync(profile, effTarget)
                : await DisableDxvkAsync(profile);

            return ok ? DxvkActionResult.Applied : DxvkActionResult.Failed;
        }

        /// <summary>Use when you already have a live Process reference (e.g. from the tray's detected-game menu).</summary>
        public Task<DxvkActionResult> RequestEnableAsync(GameProfile profile, Process process, string? targetVersion = null)
            => QueueOrApplyAsync(profile, IsStillRunning(process), PendingAction.Enable, targetVersion);

        public Task<DxvkActionResult> RequestDisableAsync(GameProfile profile, Process process)
            => QueueOrApplyAsync(profile, IsStillRunning(process), PendingAction.Disable);

        public Task<DxvkActionResult> RequestUpdateAsync(GameProfile profile, Process process, string? targetVersion = null)
            => RequestEnableAsync(profile, process, targetVersion);

        /// <summary>Use from the Manage Games window, where you have a profile but no live Process handle.</summary>
        public Task<DxvkActionResult> RequestEnableByPathAsync(GameProfile profile, string? targetVersion = null)
        {
            return QueueOrApplyAsync(profile, IsPathCurrentlyRunning(profile.ExePath), PendingAction.Enable, targetVersion);
        }

        public Task<DxvkActionResult> RequestDisableByPathAsync(GameProfile profile)
            => QueueOrApplyAsync(profile, IsPathCurrentlyRunning(profile.ExePath), PendingAction.Disable);

        public Task<DxvkActionResult> RequestUpdateByPathAsync(GameProfile profile, string? targetVersion = null)
            => RequestEnableByPathAsync(profile, targetVersion);

        public Task<DxvkActionResult> RequestReapplyAsync(GameProfile profile, Process process, bool updateBaseline = true)
            => QueueOrApplyReapplyAsync(profile, IsStillRunning(process), updateBaseline);

        public Task<DxvkActionResult> RequestReapplyByPathAsync(GameProfile profile, bool updateBaseline = true)
        {
            return QueueOrApplyReapplyAsync(profile, IsPathCurrentlyRunning(profile.ExePath), updateBaseline);
        }

        private async Task<DxvkActionResult> QueueOrApplyReapplyAsync(GameProfile profile, bool isRunning, bool updateBaseline)
        {
            LastRefusalReason = null;

            if (isRunning)
            {
                var existingInstall = ResolveInstallation(profile.ExePath);
                string authoritativeRoot = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(existingInstall?.InstallationPath, profile.ExePath);

                var storePaths = new List<string> { _gameLibraryStore.LibraryFilePath };
                if (!string.IsNullOrWhiteSpace(_profiles.ProfilesFile))
                {
                    storePaths.Add(_profiles.ProfilesFile);
                }

                using var scopeLease = await LeaseCoordinator.TryAcquireOperationScopeAsync(
                    authoritativeRoot,
                    storePaths,
                    TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

                if (scopeLease == null)
                {
                    LastRefusalReason = LeaseCoordinator.LastAcquisitionError ??
                        "Store operation in progress. Please retry shortly.";
                    return DxvkActionResult.Failed;
                }

                var storeLease = scopeLease.StoreLease ?? scopeLease;

                if (!_gameLibraryStore.TryCheckCanModifyLibrary(out var storeRefusalReason, storeLease))
                {
                    LastRefusalReason = storeRefusalReason;
                    return DxvkActionResult.Failed;
                }

                if (!_profiles.TryCheckCanModifyProfileStore(out var profileRefusalReason, storeLease))
                {
                    LastRefusalReason = profileRefusalReason;
                    return DxvkActionResult.Failed;
                }

                var installation = ResolveInstallation(profile.ExePath);
                if (installation != null)
                {
                    ClearCancelledState(installation, storeLease);
                }
                ReassessEffectiveApi(installation, profile, storeLease);

                if (!DxvkCompatibility.IsInstallationSupported(installation, profile.Api, profile.ExeName, out var refusalReason))
                {
                    LastRefusalReason = refusalReason;
                    if (installation != null)
                    {
                        installation.LastRefusalReason = refusalReason;
                        if (!_gameLibraryStore.HasUncertainPersistence)
                        {
                            _gameLibraryStore.Save(installation, storeLease);
                        }
                    }
                    Logger.Log($"DxvkManager: refusing reapply for {profile.ExeName}: {refusalReason}");
                    return DxvkActionResult.Failed;
                }

                authoritativeRoot = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(installation?.InstallationPath, profile.ExePath);
                if (!string.IsNullOrWhiteSpace(authoritativeRoot))
                {
                    var targetInst = installation ?? _gameLibraryStore.GetOrCreateInstallation(authoritativeRoot, Path.GetFileNameWithoutExtension(profile.ExePath), storeLease);
                    string relPath = Path.GetRelativePath(targetInst.InstallationPath, profile.ExePath);
                    var existingExe = targetInst.FindExecutable(relPath);
                    var recordedExe = existingExe ?? targetInst.GetOrAddExecutable(relPath, profile.ExeName);
                    if (existingExe == null)
                    {
                        if (profile.Api != GraphicsApi.Unknown)
                        {
                            recordedExe.LastKnownApi = profile.Api;
                        }
                        if (!string.Equals(profile.Architecture, "Unknown", StringComparison.OrdinalIgnoreCase))
                        {
                            recordedExe.LastKnownArchitecture = profile.Architecture;
                        }
                    }
                    targetInst.PendingAction = Models.PendingAction.Reapply(targetInst.ManagedDxvkVersion ?? "latest", "Queued reapply while game running");
                    _gameLibraryStore.Save(targetInst, storeLease);
                }
                return DxvkActionResult.Queued;
            }

            bool ok = await ReapplyAsync(profile, updateBaseline);
            return ok ? DxvkActionResult.Applied : DxvkActionResult.Failed;
        }

        /// <summary>Updates every currently-enabled profile that isn't already on its target version.</summary>
        public async Task<Dictionary<string, DxvkActionResult>> UpdateAllEnabledAsync()
        {
            var results = new Dictionary<string, DxvkActionResult>();
            var allProfiles = _profiles.GetAll().ToList();
            var enabledProfiles = allProfiles.Where(p =>
            {
                var inst = ResolveInstallation(p.ExePath);
                if (inst != null && inst.IsHidden)
                    return false;
                if (inst != null && inst.RestorationState == RestorationState.Restored && string.IsNullOrWhiteSpace(inst.ManagedDxvkVersion))
                    return false;
                return p.DxvkEnabled
                    || (inst != null && (
                        inst.RestorationState == RestorationState.Managed
                        || (inst.RestorationState == RestorationState.AttentionRequired && !string.IsNullOrWhiteSpace(inst.ManagedDxvkVersion))));
            }).ToList();
            if (enabledProfiles.Count == 0)
                return results;

            bool anyNeedsLatest = false;
            foreach (var profile in enabledProfiles)
            {
                var installation = ResolveInstallation(profile.ExePath);
                if (string.IsNullOrWhiteSpace(installation?.ManagementPolicy?.PinnedDxvkVersion))
                {
                    anyNeedsLatest = true;
                    break;
                }
            }

            ReleaseInfo? latest = null;
            if (anyNeedsLatest)
            {
                try
                {
                    latest = await GetLatestReleaseAsync();
                }
                catch
                {
                    latest = null;
                }
            }

            foreach (var profile in enabledProfiles)
            {
                var installation = ResolveInstallation(profile.ExePath);
                string? pinned = installation?.ManagementPolicy?.PinnedDxvkVersion;
                if (!string.IsNullOrWhiteSpace(pinned))
                {
                    if (UpdateAvailable(profile, null))
                    {
                        results[profile.ExePath] = await RequestUpdateByPathAsync(profile, pinned.Trim());
                    }
                }
                else
                {
                    if (latest != null && UpdateAvailable(profile, latest))
                    {
                        results[profile.ExePath] = await RequestUpdateByPathAsync(profile, "latest");
                    }
                }
            }

            return results;
        }

        public async Task<bool> ApplyPendingAsync(string exePath, DetectionSnapshot? snapshot = null)
        {
            LastRefusalReason = null;
            var installation = ResolveInstallation(exePath);

            string authoritativeRoot = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(installation?.InstallationPath, exePath);
            if (string.IsNullOrWhiteSpace(authoritativeRoot) || !Directory.Exists(authoritativeRoot))
                return false;

            using var lease = await LeaseCoordinator.TryAcquireOperationScopeAsync(
                authoritativeRoot,
                new[] { _gameLibraryStore.LibraryFilePath, _profiles.ProfilesFile },
                TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

            if (lease == null)
            {
                LastRefusalReason = LeaseCoordinator.LastAcquisitionError ??
                    $"Operation in progress for installation: {authoritativeRoot}. Please retry shortly.";
                Logger.Log($"DxvkManager: {LastRefusalReason}");
                return false;
            }

            if (!_gameLibraryStore.TryCheckCanModifyLibrary(out var storeRefusalReason, lease.StoreLease))
            {
                LastRefusalReason = storeRefusalReason;
                return false;
            }

            if (!_profiles.TryCheckCanModifyProfileStore(out var profileRefusalReason, lease.StoreLease))
            {
                LastRefusalReason = profileRefusalReason;
                return false;
            }

            if (snapshot != null && !_gameLibraryStore.HasUncertainPersistence)
            {
                _gameLibraryStore.RecordDetectionSnapshot(snapshot, lease.StoreLease);
            }

            // Re-resolve state and profile under the acquired lease
            installation = ResolveInstallation(exePath);
            var profile = _profiles.GetOrCreate(exePath, lease.StoreLease);
            ReassessEffectiveApi(installation, profile, lease.StoreLease);

            // Re-read current transient intent under protection
            bool hasTransient = _pending.TryGetValue(exePath, out var transientAction);

            var candidateExes = new List<string> { profile.ExeName };
            if (installation?.Executables != null)
            {
                candidateExes.AddRange(installation.Executables.Select(e => e.RelativePath));
            }
            var activity = ActivityInspector.GetActivityEvidence(authoritativeRoot, candidateExes);
            if (activity == ProcessActivityEvidence.Running)
            {
                LastRefusalReason = $"Game is currently running: {authoritativeRoot}. Close the game before modifying files.";
                Logger.Log($"DxvkManager: {LastRefusalReason}");
                return false;
            }
            if (activity == ProcessActivityEvidence.Unknown)
            {
                LastRefusalReason = "Cannot verify game activity: candidate process access denied (protected or elevated).";
                Logger.Log($"DxvkManager: {LastRefusalReason}");
                return false;
            }

            if (installation?.PendingAction != null && installation.PendingAction.IsPending)
            {
                var pendingType = installation.PendingAction.Type;
                if (pendingType != PendingActionType.Restore && !DxvkCompatibility.IsInstallationSupported(installation, profile.Api, profile.ExeName, out var refusalReason))
                {
                    CancelIncompatiblePendingAction(installation, profile, refusalReason, lease.StoreLease);
                    return false;
                }

                bool success = false;

                switch (pendingType)
                {
                    case PendingActionType.Install:
                    case PendingActionType.Update:
                        success = await EnableDxvkAsync(profile, installation.PendingAction.TargetDxvkVersion, lease);
                        break;
                    case PendingActionType.Reapply:
                        success = await ReapplyAsync(profile, updateBaseline: true, lease);
                        break;
                    case PendingActionType.Restore:
                        success = await DisableDxvkAsync(profile, lease);
                        break;
                }

                if (success)
                {
                    if (_pending.TryGetValue(exePath, out var currentTransient))
                    {
                        if ((pendingType == PendingActionType.Restore && currentTransient == PendingAction.Disable) ||
                            ((pendingType == PendingActionType.Install || pendingType == PendingActionType.Update) && currentTransient == PendingAction.Enable))
                        {
                            _pending.TryRemove(new KeyValuePair<string, PendingAction>(exePath, currentTransient));
                        }
                    }
                    installation.PendingAction = null;
                    installation.LastRefusalReason = null;
                    if (!_gameLibraryStore.HasUncertainPersistence)
                    {
                        _gameLibraryStore.Save(installation, lease.StoreLease);
                    }
                    LastRefusalReason = null;
                }
                return success;
            }

            if (hasTransient)
            {
                bool success = false;
                switch (transientAction)
                {
                    case PendingAction.Enable:
                        success = await EnableDxvkAsync(profile, targetVersion: null, lease);
                        break;
                    case PendingAction.Disable:
                        success = await DisableDxvkAsync(profile, lease);
                        break;
                }

                if (success)
                {
                    _pending.TryRemove(new KeyValuePair<string, PendingAction>(exePath, transientAction));
                }
                return success;
            }

            return false;
        }

        private void CancelIncompatiblePendingAction(GameInstallation installation, GameProfile profile, string? refusalReason, IOperationLease? storeLease = null)
        {
            if (installation.PendingAction == null || !installation.PendingAction.IsPending)
                return;

            var cancelledAction = installation.PendingAction;
            string effectiveReason = refusalReason ?? "DXVK is not supported for this installation.";

            // 1. Clear persisted pending action and record durable cancellation context
            installation.PendingAction = null;
            installation.LastCancelledAction = cancelledAction;
            installation.LastCancellationReason = effectiveReason;
            string outcome = $"Cancelled pending {cancelledAction.Type} (v{cancelledAction.TargetDxvkVersion ?? "unknown"}): {effectiveReason}";
            installation.LastCancellationOutcome = outcome;
            installation.LastRefusalReason = outcome;
            LastRefusalReason = outcome;

            // 2. Clear transient queued intent for all executables in this installation (preserving Restore)
            if (installation.Executables != null)
            {
                foreach (var exe in installation.Executables)
                {
                    string fullExePath = Path.Combine(installation.InstallationPath, exe.RelativePath);
                    if (_pending.TryGetValue(fullExePath, out var a) && a != PendingAction.Disable)
                    {
                        _pending.TryRemove(fullExePath, out _);
                    }
                }
            }
            if (!string.IsNullOrEmpty(profile.ExePath))
            {
                if (_pending.TryGetValue(profile.ExePath, out var a) && a != PendingAction.Disable)
                {
                    _pending.TryRemove(profile.ExePath, out _);
                }
            }

            // 3. Persist the outcome before treating cancellation as completed
            if (!_gameLibraryStore.HasUncertainPersistence)
            {
                _gameLibraryStore.Save(installation, storeLease);
            }

            Logger.Log($"DxvkManager: cancelled incompatible pending action {cancelledAction.Type} for {profile.ExeName}: {outcome}");

            // 4. Raise notification event for the cancellation transition
            OnPendingActionCancelled?.Invoke(installation, cancelledAction, outcome);
        }

        public async Task<int> ProcessAllPendingActionsAsync()
        {
            int processed = 0;
            foreach (var installation in _gameLibraryStore.GetAll())
            {
                try
                {
                    if (installation.PendingAction == null || !installation.PendingAction.IsPending)
                        continue;

                    var candidateExes = new List<string>();
                    if (installation.Executables != null)
                    {
                        candidateExes.AddRange(installation.Executables.Select(e => e.RelativePath));
                    }
                    var activity = ActivityInspector.GetActivityEvidence(installation.InstallationPath, candidateExes);
                    if (activity == ProcessActivityEvidence.Running || activity == ProcessActivityEvidence.Unknown)
                        continue;

                    string? primaryExeRel = null;
                    if (installation.Executables != null && installation.Executables.Count > 0)
                    {
                        var allProfiles = _profiles.GetAll().ToList();
                        var matchingExe = installation.Executables.FirstOrDefault(e =>
                        {
                            string p = Path.Combine(installation.InstallationPath, e.RelativePath);
                            var prof = allProfiles.FirstOrDefault(pr => string.Equals(pr.ExePath, p, StringComparison.OrdinalIgnoreCase));
                            return prof != null && (prof.DxvkEnabled || prof.Api != GraphicsApi.Unknown);
                        }) ?? installation.Executables.FirstOrDefault();
                        primaryExeRel = matchingExe?.RelativePath;
                    }
                    if (string.IsNullOrEmpty(primaryExeRel) && Directory.Exists(installation.InstallationPath))
                    {
                        var firstExe = Directory.EnumerateFiles(installation.InstallationPath, "*.exe").FirstOrDefault();
                        if (firstExe != null) primaryExeRel = Path.GetFileName(firstExe);
                    }

                    if (string.IsNullOrEmpty(primaryExeRel))
                        continue;

                    string primaryExePath = Path.Combine(installation.InstallationPath, primaryExeRel);

                    using var lease = await LeaseCoordinator.TryAcquireOperationScopeAsync(
                        installation.InstallationPath,
                        new[] { _gameLibraryStore.LibraryFilePath, _profiles.ProfilesFile },
                        TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

                    if (lease == null)
                        continue;

                    if (!_gameLibraryStore.TryCheckCanModifyLibrary(out _, lease.StoreLease) ||
                        !_profiles.TryCheckCanModifyProfileStore(out _, lease.StoreLease))
                    {
                        continue;
                    }

                    var profile = _profiles.GetOrCreate(primaryExePath, lease.StoreLease);
                    ReassessEffectiveApi(installation, profile, lease.StoreLease);

                    if (installation.PendingAction.Type != PendingActionType.Restore && !DxvkCompatibility.IsInstallationSupported(installation, profile.Api, profile.ExeName, out var refusalReason))
                    {
                        CancelIncompatiblePendingAction(installation, profile, refusalReason, lease.StoreLease);
                        continue;
                    }

                    var activityUnderLease = ActivityInspector.GetActivityEvidence(installation.InstallationPath, candidateExes);
                    if (activityUnderLease == ProcessActivityEvidence.Running || activityUnderLease == ProcessActivityEvidence.Unknown)
                        continue;

                    bool success = false;
                    _processingPendingBatchDepth++;
                    try
                    {
                        switch (installation.PendingAction.Type)
                        {
                            case PendingActionType.Install:
                            case PendingActionType.Update:
                                success = await EnableDxvkAsync(profile, installation.PendingAction.TargetDxvkVersion, lease);
                                break;
                            case PendingActionType.Reapply:
                                success = await ReapplyAsync(profile, updateBaseline: true, lease);
                                break;
                            case PendingActionType.Restore:
                                success = await DisableDxvkAsync(profile, lease);
                                break;
                        }
                    }
                    finally
                    {
                        _processingPendingBatchDepth--;
                    }

                    if (success)
                    {
                        if (installation.Executables != null)
                        {
                            foreach (var exe in installation.Executables)
                            {
                                string p = Path.Combine(installation.InstallationPath, exe.RelativePath);
                                _pending.TryRemove(p, out _);
                            }
                        }
                        if (!string.IsNullOrEmpty(primaryExePath))
                        {
                            _pending.TryRemove(primaryExePath, out _);
                        }
                        installation.PendingAction = null;
                        installation.LastRefusalReason = null;
                        if (!_gameLibraryStore.HasUncertainPersistence)
                        {
                            _gameLibraryStore.Save(installation, lease.StoreLease);
                        }
                        LastRefusalReason = null;
                        processed++;
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        string target = !string.IsNullOrEmpty(installation?.DisplayName)
                            ? installation.DisplayName
                            : (installation?.InstallationPath ?? "unknown installation");
                        string actionType = installation?.PendingAction?.Type.ToString()
                            ?? installation?.LastCancelledAction?.Type.ToString()
                            ?? "Unknown";

                        Logger.Log($"DxvkManager: failed processing pending action {actionType} for {target}: {ex.Message}");
                    }
                    catch
                    {
                        // Failure reporting must not prevent continuation
                    }
                }
            }
            return processed;
        }

        public Task<bool> EnableDxvkAsync(GameProfile profile, string? targetVersion = null)
        {
            return EnableDxvkAsync(profile, targetVersion, leaseToken: null);
        }

        internal async Task<bool> EnableDxvkAsync(GameProfile profile, string? targetVersion, IOperationLease? leaseToken)
        {
            LastRefusalReason = null;
            var installation = ResolveInstallation(profile.ExePath);

            string authoritativeRoot = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(installation?.InstallationPath, profile.ExePath);
            if (string.IsNullOrWhiteSpace(authoritativeRoot) || !Directory.Exists(authoritativeRoot))
                return false;

            IOperationLease? acquiredLease = null;
            try
            {
                if (leaseToken != null)
                {
                    if (leaseToken.IsDisposed || !leaseToken.IsAcquired || !leaseToken.IsValidForTarget(authoritativeRoot))
                    {
                        throw new ArgumentException("Invalid, mismatched, or disposed lease token.", nameof(leaseToken));
                    }
                }
                else
                {
                    acquiredLease = await LeaseCoordinator.TryAcquireOperationScopeAsync(
                        authoritativeRoot,
                        new[] { _gameLibraryStore.LibraryFilePath, _profiles.ProfilesFile },
                        TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

                    if (acquiredLease == null)
                    {
                        LastRefusalReason = LeaseCoordinator.LastAcquisitionError ??
                            $"Operation in progress for installation: {authoritativeRoot}. Please retry shortly.";
                        Logger.Log($"DxvkManager: {LastRefusalReason}");
                        return false;
                    }
                    leaseToken = acquiredLease;
                }

                if (!_gameLibraryStore.TryCheckCanModifyLibrary(out var storeRefusalReason, leaseToken.StoreLease))
                {
                    LastRefusalReason = storeRefusalReason;
                    return false;
                }

                if (!_profiles.TryCheckCanModifyProfileStore(out var profileRefusalReason, leaseToken.StoreLease))
                {
                    LastRefusalReason = profileRefusalReason;
                    return false;
                }

                if (installation != null)
                {
                    ClearCancelledState(installation, leaseToken.StoreLease);
                }

                ReassessEffectiveApi(installation, profile, leaseToken.StoreLease);

                if (!DxvkCompatibility.IsInstallationSupported(installation, profile.Api, profile.ExeName, out var refusalReason))
                {
                    LastRefusalReason = refusalReason;
                    if (installation != null)
                    {
                        installation.LastRefusalReason = refusalReason;
                        if (!_gameLibraryStore.HasUncertainPersistence)
                        {
                            _gameLibraryStore.Save(installation, leaseToken.StoreLease);
                        }
                    }
                    Logger.Log($"DxvkManager: refusing EnableDxvkAsync for {profile.ExeName}: {refusalReason}");
                    return false;
                }

                var candidateExes = new List<string> { profile.ExeName };
                if (installation?.Executables != null)
                {
                    candidateExes.AddRange(installation.Executables.Select(e => e.RelativePath));
                }
                var activity = ActivityInspector.GetActivityEvidence(authoritativeRoot, candidateExes);
                if (activity == ProcessActivityEvidence.Running)
                {
                    LastRefusalReason = $"Game is currently running: {authoritativeRoot}. Close the game before modifying files.";
                    Logger.Log($"DxvkManager: {LastRefusalReason}");
                    return false;
                }
                if (activity == ProcessActivityEvidence.Unknown)
                {
                    LastRefusalReason = "Cannot verify game activity: candidate process access denied (protected or elevated).";
                    Logger.Log($"DxvkManager: {LastRefusalReason}");
                    return false;
                }

                string? effectiveTarget = !string.IsNullOrWhiteSpace(targetVersion)
                    ? targetVersion.Trim()
                    : (!string.IsNullOrWhiteSpace(installation?.ManagementPolicy?.PinnedDxvkVersion)
                        ? installation.ManagementPolicy.PinnedDxvkVersion.Trim()
                        : null);

                ReleaseInfo? release;
                if (!string.IsNullOrWhiteSpace(effectiveTarget) && !string.Equals(effectiveTarget, "latest", StringComparison.OrdinalIgnoreCase))
                {
                    if (!DxvkLocalReleaseLibrary.IsVersionAvailable(_installer.DxvkSourceDir, effectiveTarget, profile.Api, profile.Architecture))
                    {
                        var unavailableReason = $"Requested DXVK version '{effectiveTarget}' is not available locally. Use Browse / Download official versions to inspect or acquire it.";
                        LastRefusalReason = unavailableReason;
                        if (installation != null)
                        {
                            installation.LastRefusalReason = unavailableReason;
                            if (!_gameLibraryStore.HasUncertainPersistence)
                            {
                                _gameLibraryStore.Save(installation, leaseToken.StoreLease);
                            }
                        }
                        Logger.Log($"DxvkManager: {unavailableReason}");
                        return false;
                    }

                    release = new ReleaseInfo { Version = effectiveTarget, DownloadUrl = "" };
                }
                else
                {
                    release = await GetLatestReleaseAsync();
                    if (release == null)
                        return false;
                }

                bool ok = await _installer.ApplyToGameAsync(profile, release, leaseToken).ConfigureAwait(false);
                if (!ok)
                {
                    if (_installer.LastRefusalReason != null)
                    {
                        LastRefusalReason = _installer.LastRefusalReason;
                    }
                    return false;
                }

                LastRefusalReason = null;
                profile.DxvkEnabled = true;
                profile.DxvkVersion = release.Version;
                TrySaveProfileNonFatal(profile, nameof(EnableDxvkAsync), leaseToken.StoreLease);

                return true;
            }
            finally
            {
                if (acquiredLease != null)
                {
                    await acquiredLease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        public Task<bool> DisableDxvkAsync(GameProfile profile)
        {
            return DisableDxvkAsync(profile, leaseToken: null);
        }

        internal async Task<bool> DisableDxvkAsync(GameProfile profile, IOperationLease? leaseToken)
        {
            LastRefusalReason = null;
            var installation = ResolveInstallation(profile.ExePath);
            string authoritativeRoot = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(installation?.InstallationPath, profile.ExePath);
            if (string.IsNullOrWhiteSpace(authoritativeRoot) || !Directory.Exists(authoritativeRoot))
                return false;

            IOperationLease? acquiredLease = null;
            try
            {
                if (leaseToken != null)
                {
                    if (leaseToken.IsDisposed || !leaseToken.IsAcquired || !leaseToken.IsValidForTarget(authoritativeRoot))
                    {
                        throw new ArgumentException("Invalid, mismatched, or disposed lease token.", nameof(leaseToken));
                    }
                }
                else
                {
                    acquiredLease = await LeaseCoordinator.TryAcquireOperationScopeAsync(
                        authoritativeRoot,
                        new[] { _gameLibraryStore.LibraryFilePath, _profiles.ProfilesFile },
                        TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

                    if (acquiredLease == null)
                    {
                        LastRefusalReason = LeaseCoordinator.LastAcquisitionError ??
                            $"Operation in progress for installation: {authoritativeRoot}. Please retry shortly.";
                        Logger.Log($"DxvkManager: {LastRefusalReason}");
                        return false;
                    }
                    leaseToken = acquiredLease;
                }

                if (!_gameLibraryStore.TryCheckCanModifyLibrary(out var storeRefusalReason, leaseToken.StoreLease))
                {
                    LastRefusalReason = storeRefusalReason;
                    return false;
                }

                if (!_profiles.TryCheckCanModifyProfileStore(out var profileRefusalReason, leaseToken.StoreLease))
                {
                    LastRefusalReason = profileRefusalReason;
                    return false;
                }

                var candidateExes = new List<string> { profile.ExeName };
                if (installation?.Executables != null)
                {
                    candidateExes.AddRange(installation.Executables.Select(e => e.RelativePath));
                }
                var activity = ActivityInspector.GetActivityEvidence(authoritativeRoot, candidateExes);
                if (activity == ProcessActivityEvidence.Running)
                {
                    LastRefusalReason = $"Game is currently running: {authoritativeRoot}. Close the game before modifying files.";
                    Logger.Log($"DxvkManager: {LastRefusalReason}");
                    return false;
                }
                if (activity == ProcessActivityEvidence.Unknown)
                {
                    LastRefusalReason = "Cannot verify game activity: candidate process access denied (protected or elevated).";
                    Logger.Log($"DxvkManager: {LastRefusalReason}");
                    return false;
                }

                if (installation != null &&
                    (!string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion) || installation.RestorationState == RestorationState.Managed) &&
                    (installation.ManagedFiles == null || installation.ManagedFiles.Count == 0))
                {
                    var reason = $"Refusing rollback on {installation.DisplayName ?? profile.ExeName}; missing ownership evidence for actively managed installation.";
                    LastRefusalReason = reason;
                    installation.RestorationState = RestorationState.AttentionRequired;
                    installation.LastRefusalReason = reason;
                    if (!_gameLibraryStore.HasUncertainPersistence)
                    {
                        _gameLibraryStore.Save(installation, leaseToken.StoreLease);
                    }
                    Logger.Log($"DxvkManager: {reason}");
                    return false;
                }

                bool ok = await _rollback.RestoreOriginalDllsAsync(profile, leaseToken).ConfigureAwait(false);
                if (!ok)
                {
                    LastRefusalReason = _rollback.LastRefusalReason;
                    return false;
                }

                LastRefusalReason = null;
                profile.DxvkEnabled = false;
                TrySaveProfileNonFatal(profile, nameof(DisableDxvkAsync), leaseToken.StoreLease);

                return true;
            }
            finally
            {
                if (acquiredLease != null)
                {
                    await acquiredLease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        public ExistingDxvkAssessment AssessExistingDxvk(GameProfile profile)
        {
            string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
            var detector = new ExistingDxvkDetector(_installer.DxvkSourceDir);
            var assessment = detector.AssessDirectory(gameDir, profile.Architecture);
            if (assessment.CanBeAdopted && !DxvkLocalReleaseLibrary.IsRecognizedSourceEligible(
                DxvkSourceDir, assessment.MatchedVersion!, profile.Api, profile.Architecture))
                return new ExistingDxvkAssessment
                {
                    Status = ExistingDxvkStatus.RecognizedUnknownVersion, DetectedDlls = assessment.DetectedDlls,
                    Evidence = new[] { "Local acquired source verification failed; no verified official identity is available." }
                };
            return assessment;
        }

        public Task<bool> AdoptExistingAsync(GameProfile profile)
        {
            return AdoptExistingAsync(profile, leaseToken: null);
        }

        internal async Task<bool> AdoptExistingAsync(GameProfile profile, IOperationLease? leaseToken)
        {
            LastRefusalReason = null;
            var installation = ResolveInstallation(profile.ExePath);

            string authoritativeRoot = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(installation?.InstallationPath, profile.ExePath);
            if (string.IsNullOrWhiteSpace(authoritativeRoot) || !Directory.Exists(authoritativeRoot))
                return false;

            IOperationLease? acquiredLease = null;
            try
            {
                if (leaseToken != null)
                {
                    if (leaseToken.IsDisposed || !leaseToken.IsAcquired || !leaseToken.IsValidForTarget(authoritativeRoot))
                    {
                        throw new ArgumentException("Invalid, mismatched, or disposed lease token.", nameof(leaseToken));
                    }
                }
                else
                {
                    acquiredLease = await LeaseCoordinator.TryAcquireOperationScopeAsync(
                        authoritativeRoot,
                        new[] { _gameLibraryStore.LibraryFilePath, _profiles.ProfilesFile },
                        TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

                    if (acquiredLease == null)
                    {
                        LastRefusalReason = LeaseCoordinator.LastAcquisitionError ??
                            $"Operation in progress for installation: {authoritativeRoot}. Please retry shortly.";
                        Logger.Log($"DxvkManager: {LastRefusalReason}");
                        return false;
                    }
                    leaseToken = acquiredLease;
                }

                if (!_gameLibraryStore.TryCheckCanModifyLibrary(out var storeRefusalReason, leaseToken.StoreLease))
                {
                    LastRefusalReason = storeRefusalReason;
                    return false;
                }

                if (!_profiles.TryCheckCanModifyProfileStore(out var profileRefusalReason, leaseToken.StoreLease))
                {
                    LastRefusalReason = profileRefusalReason;
                    return false;
                }

                if (installation != null)
                {
                    ClearCancelledState(installation, leaseToken.StoreLease);
                }

                ReassessEffectiveApi(installation, profile, leaseToken.StoreLease);

                if (!DxvkCompatibility.IsInstallationSupported(installation, profile.Api, profile.ExeName, out var refusalReason))
                {
                    LastRefusalReason = refusalReason;
                    if (installation != null)
                    {
                        installation.LastRefusalReason = refusalReason;
                        if (!_gameLibraryStore.HasUncertainPersistence)
                        {
                            _gameLibraryStore.Save(installation, leaseToken.StoreLease);
                        }
                    }
                    Logger.Log($"DxvkManager: refusing AdoptExistingAsync for {profile.ExeName}: {refusalReason}");
                    return false;
                }

                var candidateExes = new List<string> { profile.ExeName };
                if (installation?.Executables != null)
                {
                    candidateExes.AddRange(installation.Executables.Select(e => e.RelativePath));
                }
                var activity = ActivityInspector.GetActivityEvidence(authoritativeRoot, candidateExes);
                if (activity == ProcessActivityEvidence.Running)
                {
                    LastRefusalReason = $"Game is currently running: {authoritativeRoot}. Close the game before modifying files.";
                    Logger.Log($"DxvkManager: {LastRefusalReason}");
                    return false;
                }
                if (activity == ProcessActivityEvidence.Unknown)
                {
                    LastRefusalReason = "Cannot verify game activity: candidate process access denied (protected or elevated).";
                    Logger.Log($"DxvkManager: {LastRefusalReason}");
                    return false;
                }

                var assessment = AssessExistingDxvk(profile);
                if (!assessment.CanBeAdopted)
                {
                    LastRefusalReason = $"Existing DLLs cannot be adopted (status: {assessment.Status}).";
                    return false;
                }

                bool ok = _installer.AdoptExisting(profile, assessment, leaseToken);
                if (ok)
                {
                    LastRefusalReason = null;
                    TrySaveProfileNonFatal(profile, nameof(AdoptExistingAsync), leaseToken.StoreLease);
                }
                else
                {
                    if (_installer.LastRefusalReason != null)
                    {
                        LastRefusalReason = _installer.LastRefusalReason;
                    }
                }
                return ok;
            }
            finally
            {
                if (acquiredLease != null)
                {
                    await acquiredLease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        public Task<bool> ReapplyAsync(GameProfile profile, bool updateBaseline = true)
        {
            return ReapplyAsync(profile, updateBaseline, leaseToken: null);
        }

        internal async Task<bool> ReapplyAsync(GameProfile profile, bool updateBaseline, IOperationLease? leaseToken)
        {
            LastRefusalReason = null;
            var installation = ResolveInstallation(profile.ExePath);

            string authoritativeRoot = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(installation?.InstallationPath, profile.ExePath);
            if (string.IsNullOrWhiteSpace(authoritativeRoot) || !Directory.Exists(authoritativeRoot))
                return false;

            IOperationLease? acquiredLease = null;
            try
            {
                if (leaseToken != null)
                {
                    if (leaseToken.IsDisposed || !leaseToken.IsAcquired || !leaseToken.IsValidForTarget(authoritativeRoot))
                    {
                        throw new ArgumentException("Invalid, mismatched, or disposed lease token.", nameof(leaseToken));
                    }
                }
                else
                {
                    acquiredLease = await LeaseCoordinator.TryAcquireOperationScopeAsync(
                        authoritativeRoot,
                        new[] { _gameLibraryStore.LibraryFilePath, _profiles.ProfilesFile },
                        TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

                    if (acquiredLease == null)
                    {
                        LastRefusalReason = LeaseCoordinator.LastAcquisitionError ??
                            $"Operation in progress for installation: {authoritativeRoot}. Please retry shortly.";
                        Logger.Log($"DxvkManager: {LastRefusalReason}");
                        return false;
                    }
                    leaseToken = acquiredLease;
                }

                if (!_gameLibraryStore.TryCheckCanModifyLibrary(out var storeRefusalReason, leaseToken.StoreLease))
                {
                    LastRefusalReason = storeRefusalReason;
                    return false;
                }

                if (!_profiles.TryCheckCanModifyProfileStore(out var profileRefusalReason, leaseToken.StoreLease))
                {
                    LastRefusalReason = profileRefusalReason;
                    return false;
                }

                if (installation != null)
                {
                    ClearCancelledState(installation, leaseToken.StoreLease);
                }

                ReassessEffectiveApi(installation, profile, leaseToken.StoreLease);

                if (!DxvkCompatibility.IsInstallationSupported(installation, profile.Api, profile.ExeName, out var refusalReason))
                {
                    LastRefusalReason = refusalReason;
                    if (installation != null)
                    {
                        installation.LastRefusalReason = refusalReason;
                        if (!_gameLibraryStore.HasUncertainPersistence)
                        {
                            _gameLibraryStore.Save(installation, leaseToken.StoreLease);
                        }
                    }
                    Logger.Log($"DxvkManager: refusing ReapplyAsync for {profile.ExeName}: {refusalReason}");
                    return false;
                }

                var candidateExes = new List<string> { profile.ExeName };
                if (installation?.Executables != null)
                {
                    candidateExes.AddRange(installation.Executables.Select(e => e.RelativePath));
                }
                var activity = ActivityInspector.GetActivityEvidence(authoritativeRoot, candidateExes);
                if (activity == ProcessActivityEvidence.Running)
                {
                    LastRefusalReason = $"Game is currently running: {authoritativeRoot}. Close the game before modifying files.";
                    Logger.Log($"DxvkManager: {LastRefusalReason}");
                    return false;
                }
                if (activity == ProcessActivityEvidence.Unknown)
                {
                    LastRefusalReason = "Cannot verify game activity: candidate process access denied (protected or elevated).";
                    Logger.Log($"DxvkManager: {LastRefusalReason}");
                    return false;
                }

                bool ok = await _installer.ReapplyAsync(profile, updateBaseline, leaseToken).ConfigureAwait(false);
                if (ok)
                {
                    LastRefusalReason = null;
                    profile.DxvkEnabled = true;
                    var committedInstallation = ResolveInstallation(profile.ExePath);
                    if (!string.IsNullOrWhiteSpace(committedInstallation?.ManagedDxvkVersion))
                    {
                        profile.DxvkVersion = committedInstallation.ManagedDxvkVersion.Trim();
                    }
                    TrySaveProfileNonFatal(profile, nameof(ReapplyAsync), leaseToken.StoreLease);
                }
                else
                {
                    if (_installer.LastRefusalReason != null)
                    {
                        LastRefusalReason = _installer.LastRefusalReason;
                    }
                }
                return ok;
            }
            finally
            {
                if (acquiredLease != null)
                {
                    await acquiredLease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Global operation to restore all games managed by DXVK Companion to their baseline.
        /// Each game is handled independently with error isolation (Section 23).
        /// </summary>
        public async Task<RestoreAllSummary> RestoreAllAsync()
        {
            var summary = new RestoreAllSummary();
            var allProfiles = _profiles.GetAll().ToList();

            foreach (var profile in allProfiles)
            {
                var installation = ResolveInstallation(profile.ExePath);

                bool isManaged = profile.DxvkEnabled
                    || (installation != null && (
                        installation.RestorationState == RestorationState.Managed
                        || (installation.RestorationState == RestorationState.AttentionRequired && !string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion))));
                if (!isManaged)
                {
                    summary.AlreadyRestored++;
                    continue;
                }

                summary.TotalManaged++;

                if (IsPathCurrentlyRunning(profile.ExePath))
                {
                    await RequestDisableByPathAsync(profile);
                    summary.QueuedRunning++;
                    summary.Messages.Add($"{profile.ExeName}: Game running; restore queued for exit.");
                    continue;
                }

                try
                {
                    bool ok = await DisableDxvkAsync(profile);
                    if (ok)
                    {
                        summary.Restored++;
                    }
                    else
                    {
                        summary.FailedOrAttentionRequired++;
                        summary.Messages.Add(LastRefusalReason != null
                            ? $"{profile.ExeName}: {LastRefusalReason}"
                            : $"{profile.ExeName}: Restore operation failed.");
                    }
                }
                catch (Exception ex)
                {
                    summary.FailedOrAttentionRequired++;
                    summary.Messages.Add($"{profile.ExeName}: Exception: {ex.Message}");
                }
            }

            return summary;
        }
    }
}
