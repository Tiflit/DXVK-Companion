using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace DXVKCompanion.DXVK
{
    public static class DxvkReleaseCatalog
    {
        private sealed record CatalogEntry(string Version, string Architecture, string DllName);

        // Key: SHA-256 (lowercase) -> CatalogEntry
        private static readonly ConcurrentDictionary<string, CatalogEntry> KnownHashes =
            new(StringComparer.OrdinalIgnoreCase);

        static DxvkReleaseCatalog()
        {
            // Register known official DXVK hashes (e.g. 2.5, 2.4, etc.)
            // Custom or dynamic releases can be registered at runtime or via file cache
        }

        public static void RegisterKnownHash(string sha256, string version, string architecture, string dllName)
        {
            if (string.IsNullOrWhiteSpace(sha256) || string.IsNullOrWhiteSpace(version))
                return;

            KnownHashes[sha256.Trim()] = new CatalogEntry(version, architecture, dllName);
        }

        public static string? FindVersionBySha256(string dllName, string targetSha256, string? architecture = null)
        {
            if (string.IsNullOrWhiteSpace(targetSha256))
                return null;

            if (KnownHashes.TryGetValue(targetSha256.Trim(), out var entry))
            {
                if (!string.Equals(entry.DllName, dllName, StringComparison.OrdinalIgnoreCase))
                    return null;

                if (!string.IsNullOrEmpty(architecture) &&
                    !string.Equals(entry.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
                    return null;

                return entry.Version;
            }

            return null;
        }

        public static void ClearKnownHashes()
        {
            KnownHashes.Clear();
        }
    }
}
