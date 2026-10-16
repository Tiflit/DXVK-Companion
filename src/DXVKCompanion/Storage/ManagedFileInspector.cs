using System;
using System.IO;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Utils;

namespace DXVKCompanion.Storage
{
    public class ManagedFileInspector
    {
        private readonly GameLibraryStore _store;

        public ManagedFileInspector(GameLibraryStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public virtual bool InspectInstallation(GameInstallation installation)
        {
            if (installation == null) return false;

            if (string.IsNullOrWhiteSpace(installation.InstallationPath) || !Directory.Exists(installation.InstallationPath))
            {
                return false;
            }

            bool anyExternalChange = false;
            bool allFilesVerifiedConsistent = installation.ManagedFiles.Count > 0;

            foreach (var record in installation.ManagedFiles)
            {
                string targetPath = Path.Combine(installation.InstallationPath, record.RelativePath);

                if (!File.Exists(targetPath))
                {
                    // Managed file is missing from disk
                    if (record.CurrentState != ManagedFileState.ExternallyChanged)
                    {
                        record.CurrentState = ManagedFileState.ExternallyChanged;
                        anyExternalChange = true;
                        Logger.Log($"ManagedFileInspector: {record.RelativePath} is missing on disk for {installation.DisplayName}.");
                    }
                    record.LastVerifiedUtc = DateTime.UtcNow;
                    allFilesVerifiedConsistent = false;
                    continue;
                }

                if (!IsValidSha256(record.ExpectedManagedSha256))
                {
                    // Managed file has missing or unusable baseline evidence
                    if (record.CurrentState != ManagedFileState.ExternallyChanged)
                    {
                        record.CurrentState = ManagedFileState.ExternallyChanged;
                        anyExternalChange = true;
                        Logger.Log($"ManagedFileInspector: {record.RelativePath} has missing or unusable managed hash baseline for {installation.DisplayName}.");
                    }
                    record.LastVerifiedUtc = DateTime.UtcNow;
                    allFilesVerifiedConsistent = false;
                    continue;
                }

                var currentIdentity = FileIdentity.Capture(targetPath);

                if (string.Equals(currentIdentity.Sha256, record.ExpectedManagedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    record.CurrentState = ManagedFileState.Consistent;
                }
                else
                {
                    allFilesVerifiedConsistent = false;
                    if (record.CurrentState != ManagedFileState.ExternallyChanged)
                    {
                        record.CurrentState = ManagedFileState.ExternallyChanged;
                        anyExternalChange = true;
                        Logger.Log($"ManagedFileInspector: external change detected on {record.RelativePath} for {installation.DisplayName} (Expected: {record.ExpectedManagedSha256[..Math.Min(8, record.ExpectedManagedSha256.Length)]}, Found: {currentIdentity.Sha256[..Math.Min(8, currentIdentity.Sha256.Length)]}).");
                    }
                }

                record.LastVerifiedUtc = DateTime.UtcNow;
            }

            // Section 20: Pending-Action Conflict Rule
            // A new external change supersedes an older pending action that refers to the same managed file.
            if (anyExternalChange && installation.PendingAction != null && installation.PendingAction.IsPending)
            {
                Logger.Log($"ManagedFileInspector: invalidating stale pending action ({installation.PendingAction.Type}) for {installation.DisplayName} due to external file changes.");
                installation.PendingAction = null;
            }

            if (anyExternalChange)
            {
                installation.RestorationState = RestorationState.AttentionRequired;
            }
            else if (allFilesVerifiedConsistent &&
                     installation.ConflictFlags == InstallationConflictFlags.None &&
                     installation.RestorationState == RestorationState.AttentionRequired)
            {
                // All managed files verified consistent and no conflict flags
                installation.RestorationState = installation.ManagedDxvkVersion != null
                    ? RestorationState.Managed
                    : RestorationState.Restored;
            }

            _store.Save(installation);
            return anyExternalChange;
        }

        private static bool IsValidSha256(string? sha256) =>
            !string.IsNullOrWhiteSpace(sha256) && sha256.Length == 64 && sha256.All(Uri.IsHexDigit);

        public virtual int InspectAll()
        {
            int changesDetected = 0;
            foreach (var installation in _store.GetAll())
            {
                if (InspectInstallation(installation))
                    changesDetected++;
            }
            return changesDetected;
        }
    }
}
