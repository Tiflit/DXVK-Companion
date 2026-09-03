using System;
using System.Diagnostics;
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
        }

        public void EnableDXVK(Process game, GameProfile profile)
        {
            var release = _cache.GetLatestRelease() ?? _github.FetchLatestRelease();
            _cache.Save(release);

            _installer.Install(game, profile, release);
            profile.DxvkEnabled = true;
            profile.LastVersion = release.Version;

            _profiles.Save(profile);
        }

        public void DisableDXVK(Process game, GameProfile profile)
        {
            _rollback.Restore(game, profile);
            profile.DxvkEnabled = false;

            _profiles.Save(profile);
        }

        public bool UpdateAvailable(GameProfile profile)
        {
            var release = _cache.GetLatestRelease() ?? _github.FetchLatestRelease();
            return release.Version != profile.LastVersion;
        }
    }
}
