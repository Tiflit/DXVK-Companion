using System;
using System.Collections.Concurrent;
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
            catch
            {
                // If we genuinely can't tell, assume it's still running rather than
                // risk touching files that might still be in use.
                return true;
            }
        }

        /// <summary>
        /// Enables DXVK immediately if the game isn't running, or queues it to run
        /// automatically once the game exits. This is what actually implements the
        /// "never touch a running game's files" behavior described in the README.
        /// </summary>
        public async Task<DxvkActionResult> RequestEnableAsync(GameProfile profile, Process process)
        {
            if (IsStillRunning(process))
            {
                _pending[profile.ExePath] = PendingAction.Enable;
                return DxvkActionResult.Queued;
            }

            bool ok = await EnableDxvkAsync(profile);
            return ok ? DxvkActionResult.Applied : DxvkActionResult.Failed;
        }

        public async Task<DxvkActionResult> RequestDisableAsync(GameProfile profile, Process process)
        {
            if (IsStillRunning(process))
            {
                _pending[profile.ExePath] = PendingAction.Disable;
                return DxvkActionResult.Queued;
            }

            bool ok = await DisableDxvkAsync(profile);
            return ok ? DxvkActionResult.Applied : DxvkActionResult.Failed;
        }

        /// <summary>Update reuses the enable path — enabling always applies whatever's currently latest.</summary>
        public Task<DxvkActionResult> RequestUpdateAsync(GameProfile profile, Process process)
            => RequestEnableAsync(profile, process);

        /// <summary>Called when a game with a pending action exits — applies whatever was queued.</summary>
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
