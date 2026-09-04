using DXVKCompanion.Models;
using DXVKCompanion.Storage;

namespace DXVKCompanion.DXVK
{
    public class DxvkReleaseCache
    {
        private readonly CacheStore _cacheStore;

        public DxvkReleaseCache(CacheStore cacheStore)
        {
            _cacheStore = cacheStore;
        }

        public ReleaseInfo? GetLatestRelease()
        {
            var cached = _cacheStore.LoadCachedRelease();
            if (cached == null || cached.IsExpired())
                return null;

            return cached.Release;
        }

        public void Save(ReleaseInfo release, string? etag = null)
        {
            _cacheStore.SaveCachedRelease(release, etag);
        }
    }
}
