using System.Threading.Tasks;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;

namespace DXVKCompanion.DXVK
{
    public class DxvkManager
    {
        private readonly DxvkInstaller _installer;
        private readonly DxvkRollback _rollback;
        private readonly DxvkGithubClient _github;
        private readonly DxvkReleaseCache _cache;
        private readonly ProfileStore _profiles;
        private readonly DxvkConfigManager _config;

        public DxvkManager(
            DxvkInstaller installer,
            DxvkRollback rollback,
            DxvkGithubClient github,
            DxvkReleaseCache cache,
            ProfileStore profiles)
        {
            _installer = installer;
            _rollback = rollback;
            _github = github;
            _cache = cache;
            _profiles = profiles;
            _config = new DxvkConfigManager();
        }

        public async Task<ReleaseInfo?> GetLatestReleaseAsync()
        {
            var cached = _cache.GetLatestRelease();
            if (cached != null)
                return cached;

            var latest = await _github.FetchLatestReleaseAsync();
            if (latest != null)
                _cache.Save(latest);

            return latest;
        }

        public bool UpdateAvailable(GameProfile profile, ReleaseInfo latest)
        {
            if (string.IsNullOrWhiteSpace(profile.DxvkVersion))
                return true;

            return !string.Equals(profile.DxvkVersion, latest.Version);
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
