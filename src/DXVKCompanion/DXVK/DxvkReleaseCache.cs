using System;
using System.IO;
using System.Text.Json;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;

namespace DXVKCompanion.DXVK
{
    public class DxvkReleaseCache
    {
        private readonly CacheStore _store;

        public DxvkReleaseCache(CacheStore store)
        {
            _store = store;
        }

        public ReleaseInfo? GetLatestRelease()
        {
            var cached = _store.Load();

            if (cached == null)
                return null;

            if (DateTime.UtcNow - cached.Timestamp > TimeSpan.FromHours(24))
                return null;

            return cached.Release;
        }

        public void Save(ReleaseInfo release)
        {
            _store.Save(new CachedRelease
            {
                Release = release,
                Timestamp = DateTime.UtcNow
            });
        }
    }
}
