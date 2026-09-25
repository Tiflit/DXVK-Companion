using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;

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
        private enum PendingAction { Enable, Disable }

        private readonly DxvkInstaller _installer;
        private readonly DxvkRollback _rollback;
        private readonly DxvkGithubClient _github;
        private readonly ProfileStore _profiles;
        private readonly GameLibraryStore _gameLibraryStore;
        private readonly DxvkConfigManager _config;
        private readonly ConcurrentDictionary<string, PendingAction> _pending = new(StringComparer.OrdinalIgnoreCase);

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
        }

        public Task<ReleaseInfo?> GetLatestReleaseAsync() => _github.FetchLatestReleaseAsync();

        public bool UpdateAvailable(GameProfile profile, ReleaseInfo latest)
        {
            if (string.IsNullOrWhiteSpace(profile.DxvkVersion))
                return true;

            return !string.Equals(profile.DxvkVersion, latest.Version);
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

        private static bool IsPathCurrentlyRunning(string exePath)
        {
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return false; }

            bool found = false;
            foreach (var p in processes)
            {
                if (!found)
                {
                    try
                    {
                        if (string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                            found = true;
                    }
                    catch
                    {
                        // Access denied or process exited mid-enumeration
                    }
                }
                p.Dispose();
            }
            return found;
        }

        private async Task<DxvkActionResult> QueueOrApplyAsync(GameProfile profile, bool isRunning, PendingAction action)
        {
            if (isRunning)
            {
                string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(gameDir))
                {
                    var installation = _gameLibraryStore.GetOrCreateInstallation(gameDir, Path.GetFileNameWithoutExtension(profile.ExePath));
                    installation.PendingAction = action == PendingAction.Enable
                        ? Models.PendingAction.Install(profile.DxvkVersion ?? "latest", "Queued while game running")
                        : Models.PendingAction.Restore("Queued while game running");
                    _gameLibraryStore.Save(installation);
                }

                _pending[profile.ExePath] = action;
                return DxvkActionResult.Queued;
            }

            bool ok = action == PendingAction.Enable
                ? await EnableDxvkAsync(profile)
                : await DisableDxvkAsync(profile);

            return ok ? DxvkActionResult.Applied : DxvkActionResult.Failed;
        }

        /// <summary>Use when you already have a live Process reference (e.g. from the tray's detected-game menu).</summary>
        public Task<DxvkActionResult> RequestEnableAsync(GameProfile profile, Process process)
            => QueueOrApplyAsync(profile, IsStillRunning(process), PendingAction.Enable);

        public Task<DxvkActionResult> RequestDisableAsync(GameProfile profile, Process process)
            => QueueOrApplyAsync(profile, IsStillRunning(process), PendingAction.Disable);

        public Task<DxvkActionResult> RequestUpdateAsync(GameProfile profile, Process process)
            => RequestEnableAsync(profile, process);

        /// <summary>Use from the Manage Games window, where you have a profile but no live Process handle.</summary>
        public Task<DxvkActionResult> RequestEnableByPathAsync(GameProfile profile)
            => QueueOrApplyAsync(profile, IsPathCurrentlyRunning(profile.ExePath), PendingAction.Enable);

        public Task<DxvkActionResult> RequestDisableByPathAsync(GameProfile profile)
            => QueueOrApplyAsync(profile, IsPathCurrentlyRunning(profile.ExePath), PendingAction.Disable);

        public Task<DxvkActionResult> RequestUpdateByPathAsync(GameProfile profile)
            => RequestEnableByPathAsync(profile);

        public Task<DxvkActionResult> RequestReapplyAsync(GameProfile profile, Process process, bool updateBaseline = true)
            => QueueOrApplyReapplyAsync(profile, IsStillRunning(process), updateBaseline);

        public Task<DxvkActionResult> RequestReapplyByPathAsync(GameProfile profile, bool updateBaseline = true)
            => QueueOrApplyReapplyAsync(profile, IsPathCurrentlyRunning(profile.ExePath), updateBaseline);

        private async Task<DxvkActionResult> QueueOrApplyReapplyAsync(GameProfile profile, bool isRunning, bool updateBaseline)
        {
            if (isRunning)
            {
                string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(gameDir))
                {
                    var installation = _gameLibraryStore.GetOrCreateInstallation(gameDir, Path.GetFileNameWithoutExtension(profile.ExePath));
                    installation.PendingAction = Models.PendingAction.Reapply(installation.ManagedDxvkVersion ?? "latest", "Queued reapply while game running");
                    _gameLibraryStore.Save(installation);
                }
                return DxvkActionResult.Queued;
            }

            bool ok = await ReapplyAsync(profile, updateBaseline);
            return ok ? DxvkActionResult.Applied : DxvkActionResult.Failed;
        }

        /// <summary>Updates every currently-enabled profile that isn't already on the latest version.</summary>
        public async Task<Dictionary<string, DxvkActionResult>> UpdateAllEnabledAsync()
        {
            var results = new Dictionary<string, DxvkActionResult>();

            var latest = await GetLatestReleaseAsync();
            if (latest == null)
                return results;

            foreach (var profile in _profiles.GetAll())
            {
                if (!profile.DxvkEnabled) continue;
                if (!UpdateAvailable(profile, latest)) continue;

                results[profile.ExePath] = await RequestUpdateByPathAsync(profile);
            }

            return results;
        }

        public async Task ApplyPendingAsync(string exePath)
        {
            _pending.TryRemove(exePath, out var transientAction);

            string gameDir = Path.GetDirectoryName(exePath) ?? string.Empty;
            var installation = !string.IsNullOrWhiteSpace(gameDir) ? _gameLibraryStore.FindByInstallationPath(gameDir) : null;
            var profile = _profiles.GetOrCreate(exePath);

            if (installation?.PendingAction != null && installation.PendingAction.IsPending)
            {
                var pendingType = installation.PendingAction.Type;
                bool success = false;

                switch (pendingType)
                {
                    case PendingActionType.Install:
                    case PendingActionType.Update:
                        success = await EnableDxvkAsync(profile, installation.PendingAction.TargetDxvkVersion);
                        break;
                    case PendingActionType.Reapply:
                        success = await ReapplyAsync(profile, updateBaseline: true);
                        break;
                    case PendingActionType.Restore:
                        success = await DisableDxvkAsync(profile);
                        break;
                }

                if (success)
                {
                    installation.PendingAction = null;
                    _gameLibraryStore.Save(installation);
                }
                return;
            }

            switch (transientAction)
            {
                case PendingAction.Enable:
                    await EnableDxvkAsync(profile);
                    break;
                case PendingAction.Disable:
                    await DisableDxvkAsync(profile);
                    break;
            }
        }

        public async Task<int> ProcessAllPendingActionsAsync()
        {
            int processed = 0;
            foreach (var installation in _gameLibraryStore.GetAll())
            {
                if (installation.PendingAction == null || !installation.PendingAction.IsPending)
                    continue;

                bool anyRunning = false;
                foreach (var exe in installation.Executables)
                {
                    string fullPath = Path.Combine(installation.InstallationPath, exe.RelativePath);
                    if (IsPathCurrentlyRunning(fullPath))
                    {
                        anyRunning = true;
                        break;
                    }
                }

                if (anyRunning)
                    continue;

                string? primaryExeRel = installation.Executables.FirstOrDefault()?.RelativePath;
                if (string.IsNullOrEmpty(primaryExeRel) && Directory.Exists(installation.InstallationPath))
                {
                    var firstExe = Directory.EnumerateFiles(installation.InstallationPath, "*.exe").FirstOrDefault();
                    if (firstExe != null) primaryExeRel = Path.GetFileName(firstExe);
                }

                if (string.IsNullOrEmpty(primaryExeRel))
                    continue;

                string primaryExePath = Path.Combine(installation.InstallationPath, primaryExeRel);
                var profile = _profiles.GetOrCreate(primaryExePath);

                bool success = false;
                switch (installation.PendingAction.Type)
                {
                    case PendingActionType.Install:
                    case PendingActionType.Update:
                        success = await EnableDxvkAsync(profile, installation.PendingAction.TargetDxvkVersion);
                        break;
                    case PendingActionType.Reapply:
                        success = await ReapplyAsync(profile, updateBaseline: true);
                        break;
                    case PendingActionType.Restore:
                        success = await DisableDxvkAsync(profile);
                        break;
                }

                if (success)
                {
                    installation.PendingAction = null;
                    _gameLibraryStore.Save(installation);
                    processed++;
                }
            }
            return processed;
        }

        public async Task<bool> EnableDxvkAsync(GameProfile profile, string? targetVersion = null)
        {
            ReleaseInfo? release;
            if (!string.IsNullOrWhiteSpace(targetVersion))
            {
                release = new ReleaseInfo { Version = targetVersion, DownloadUrl = "" };
            }
            else
            {
                release = await GetLatestReleaseAsync();
                if (release == null)
                    return false;
            }

            bool ok = await _installer.ApplyToGameAsync(profile, release);
            if (!ok)
                return false;

            profile.DxvkEnabled = true;
            profile.DxvkVersion = release.Version;
            _profiles.Save(profile);

            return true;
        }

        public async Task<bool> DisableDxvkAsync(GameProfile profile)
        {
            bool ok = await _rollback.RestoreOriginalDllsAsync(profile);
            if (!ok)
                return false;

            profile.DxvkEnabled = false;
            _profiles.Save(profile);

            return true;
        }

        public ExistingDxvkAssessment AssessExistingDxvk(GameProfile profile)
        {
            string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
            var detector = new ExistingDxvkDetector(_installer.DxvkSourceDir);
            return detector.AssessDirectory(gameDir, profile.Architecture);
        }

        public async Task<bool> AdoptExistingAsync(GameProfile profile)
        {
            string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
            var detector = new ExistingDxvkDetector(_installer.DxvkSourceDir);
            var assessment = detector.AssessDirectory(gameDir, profile.Architecture);
            if (!assessment.CanBeAdopted)
                return false;

            bool ok = _installer.AdoptExisting(profile, assessment);
            if (ok)
            {
                _profiles.Save(profile);
            }
            return ok;
        }

        public async Task<bool> ReapplyAsync(GameProfile profile, bool updateBaseline = true)
        {
            bool ok = await _installer.ReapplyAsync(profile, updateBaseline);
            if (ok)
            {
                profile.DxvkEnabled = true;
                _profiles.Save(profile);
            }
            return ok;
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
                string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                var installation = !string.IsNullOrWhiteSpace(gameDir) ? _gameLibraryStore.FindByInstallationPath(gameDir) : null;

                bool isManaged = profile.DxvkEnabled || (installation != null && installation.RestorationState == RestorationState.Managed);
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
                        summary.Messages.Add($"{profile.ExeName}: Restore operation failed.");
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
