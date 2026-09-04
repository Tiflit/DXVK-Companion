using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
        private readonly DxvkConfigManager _config;
        private readonly ConcurrentDictionary<string, PendingAction> _pending = new(StringComparer.OrdinalIgnoreCase);

        public DxvkManager(DxvkInstaller installer, DxvkRollback rollback, DxvkGithubClient github, ProfileStore profiles)
        {
            _installer = installer;
            _rollback = rollback;
            _github = github;
            _profiles = profiles;
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
                // Covers ObjectDisposedException too (it derives from InvalidOperationException).
                // No process is associated with this object anymore — treat as exited, not running.
                return false;
            }
            catch (Win32Exception)
            {
                // Couldn't ask the OS about this process (it's already gone) — treat as exited.
                return false;
            }
            catch
            {
                // Any other unexpected failure: default to "not running". An indefinite silent
                // queue (the old behavior) is worse UX than just applying immediately.
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
                        // Access denied or process exited mid-enumeration — skip it.
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
            if (!_pending.TryRemove(exePath, out var action))
                return;

            var profile = _profiles.GetOrCreate(exePath);

            switch (action)
            {
                case PendingAction.Enable:
                    await EnableDxvkAsync(profile);
                    break;
                case PendingAction.Disable:
                    await DisableDxvkAsync(profile);
                    break;
            }
        }

        public async Task<bool> EnableDxvkAsync(GameProfile profile)
        {
            var latest = await GetLatestReleaseAsync();
            if (latest == null)
                return false;

            bool ok = await _installer.ApplyToGameAsync(profile, latest);
            if (!ok)
                return false;

            profile.DxvkEnabled = true;
            profile.DxvkVersion = latest.Version;
            _config.WriteConfig(profile);
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
    }
}
