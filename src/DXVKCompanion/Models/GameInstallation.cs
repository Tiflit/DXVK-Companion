using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DXVKCompanion.Models
{
    public sealed class GameInstallation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string InstallationPath { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IsHidden { get; set; }
        public ManagementPolicy ManagementPolicy { get; set; } = ManagementPolicy.UseGlobal();
        public string? ManagedDxvkVersion { get; set; }
        public string? ManagedDxvkArchitecture { get; set; }
        public RestorationState RestorationState { get; set; } = RestorationState.None;
        public InstallationConflictFlags ConflictFlags { get; set; } = InstallationConflictFlags.None;
        public DxvkConfiguration Configuration { get; set; } = new();
        public List<ExecutableProfile> Executables { get; set; } = new();
        public List<ManagedFileRecord> ManagedFiles { get; set; } = new();
        public PendingAction? PendingAction { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastSeenUtc { get; set; }

        public ExecutableProfile? FindExecutable(string relativePath) =>
            Executables.FirstOrDefault(x => string.Equals(
                NormalizeRelativePath(x.RelativePath), NormalizeRelativePath(relativePath),
                StringComparison.OrdinalIgnoreCase));

        public ExecutableProfile GetOrAddExecutable(string relativePath, string? displayName = null)
        {
            var normalized = NormalizeRelativePath(relativePath);
            var existing = FindExecutable(normalized);
            if (existing != null) return existing;
            var profile = new ExecutableProfile
            {
                RelativePath = normalized,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(normalized) : displayName
            };
            Executables.Add(profile);
            return profile;
        }

        public ManagedFileRecord? FindManagedFile(string relativePath) =>
            ManagedFiles.FirstOrDefault(x => string.Equals(
                NormalizeRelativePath(x.RelativePath), NormalizeRelativePath(relativePath),
                StringComparison.OrdinalIgnoreCase));

        public ManagedFileRecord GetOrAddManagedFile(string relativePath)
        {
            var normalized = NormalizeRelativePath(relativePath);
            var existing = FindManagedFile(normalized);
            if (existing != null) return existing;
            var record = new ManagedFileRecord { RelativePath = normalized };
            ManagedFiles.Add(record);
            return record;
        }

        public static string NormalizeInstallationPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Installation path is required.", nameof(path));
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                return root;
            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static string NormalizeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Relative path is required.", nameof(path));
            return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    public sealed class DxvkConfiguration
    {
        public bool HudEnabled { get; set; }
        public int FrameLimit { get; set; } = 120;
        public bool FrameLimitEnabled { get; set; }
    }
}
