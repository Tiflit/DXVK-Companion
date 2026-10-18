using System;
using System.IO;
using System.Linq;
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

            if (_store.HasUncertainPersistence)
            {
                Logger.Log($"ManagedFileInspector: skipping inspection for {installation.DisplayName} because GameLibraryStore persistence outcome is uncertain for this session.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(installation.InstallationPath) || !Directory.Exists(installation.InstallationPath))
            {
                return false;
            }

            bool anyExternalChange = false;
            bool allFilesVerifiedConsistent = installation.ManagedFiles.Count > 0;
            bool isRestoredLifecycle = installation.RestorationState == RestorationState.Restored
                || (installation.RestorationState != RestorationState.Managed && string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion));

            foreach (var record in installation.ManagedFiles)
            {
                string targetPath = Path.Combine(installation.InstallationPath, record.RelativePath);

                if (isRestoredLifecycle)
                {
                    if (record.OriginalState == FileOriginalState.Missing)
                    {
                        if (File.Exists(targetPath))
                        {
                            allFilesVerifiedConsistent = false;
                            if (record.CurrentState != ManagedFileState.ExternallyChanged)
                            {
                                record.CurrentState = ManagedFileState.ExternallyChanged;
                                anyExternalChange = true;
                                Logger.Log($"ManagedFileInspector: unexpected file present at originally absent path {record.RelativePath} for {installation.DisplayName}.");
                            }
                        }
                        else
                        {
                            record.CurrentState = ManagedFileState.Consistent;
                        }
                    }
                    else if (record.OriginalState == FileOriginalState.Existing)
                    {
                        if (!IsValidSha256(record.OriginalSha256))
                        {
                            allFilesVerifiedConsistent = false;
                            if (record.CurrentState != ManagedFileState.ExternallyChanged)
                            {
                                record.CurrentState = ManagedFileState.ExternallyChanged;
                                anyExternalChange = true;
                                Logger.Log($"ManagedFileInspector: {record.RelativePath} has missing or unusable original hash baseline for {installation.DisplayName}.");
                            }
                        }
                        else if (!File.Exists(targetPath))
                        {
                            allFilesVerifiedConsistent = false;
                            if (record.CurrentState != ManagedFileState.ExternallyChanged)
                            {
                                record.CurrentState = ManagedFileState.ExternallyChanged;
                                anyExternalChange = true;
                                Logger.Log($"ManagedFileInspector: original file {record.RelativePath} is missing on disk for {installation.DisplayName}.");
                            }
                        }
                        else
                        {
                            var currentIdentity = FileIdentity.Capture(targetPath);
                            if (string.Equals(currentIdentity.Sha256, record.OriginalSha256, StringComparison.OrdinalIgnoreCase))
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
                                    var origExpected = record.OriginalSha256!;
                                    Logger.Log($"ManagedFileInspector: external change detected on restored original {record.RelativePath} for {installation.DisplayName} (Expected original: {origExpected[..Math.Min(8, origExpected.Length)]}, Found: {currentIdentity.Sha256[..Math.Min(8, currentIdentity.Sha256.Length)]}).");
                                }
                            }
                        }
                    }
                    else
                    {
                        allFilesVerifiedConsistent = false;
                        if (record.CurrentState != ManagedFileState.ExternallyChanged)
                        {
                            record.CurrentState = ManagedFileState.ExternallyChanged;
                            anyExternalChange = true;
                            Logger.Log($"ManagedFileInspector: original state is unknown or unsupported for {record.RelativePath} for {installation.DisplayName}.");
                        }
                    }

                    record.LastVerifiedUtc = DateTime.UtcNow;
                    continue;
                }

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

                var currentManagedIdentity = FileIdentity.Capture(targetPath);

                if (string.Equals(currentManagedIdentity.Sha256, record.ExpectedManagedSha256, StringComparison.OrdinalIgnoreCase))
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
                        var managedExpected = record.ExpectedManagedSha256!;
                        Logger.Log($"ManagedFileInspector: external change detected on {record.RelativePath} for {installation.DisplayName} (Expected: {managedExpected[..Math.Min(8, managedExpected.Length)]}, Found: {currentManagedIdentity.Sha256[..Math.Min(8, currentManagedIdentity.Sha256.Length)]}).");
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

            if (!allFilesVerifiedConsistent && installation.ManagedFiles.Count > 0)
            {
                installation.RestorationState = RestorationState.AttentionRequired;
            }
            else if (allFilesVerifiedConsistent &&
                     installation.ConflictFlags == InstallationConflictFlags.None)
            {
                if (installation.RestorationState == RestorationState.AttentionRequired)
                {
                    // All managed files verified consistent and no conflict flags
                    installation.RestorationState = installation.ManagedDxvkVersion != null
                        ? RestorationState.Managed
                        : RestorationState.Restored;
                }

                if (installation.LastRefusalReason?.StartsWith("Inspection failed", StringComparison.OrdinalIgnoreCase) == true)
                {
                    Logger.Log($"ManagedFileInspector: retiring resolved inspection failure reason for {installation.DisplayName}.");
                    installation.LastRefusalReason = null;
                }
            }

            _store.Save(installation);
            return anyExternalChange;
        }

        private static bool IsValidSha256(string? sha256) =>
            !string.IsNullOrWhiteSpace(sha256) && sha256.Length == 64 && sha256.All(Uri.IsHexDigit);

        public virtual int InspectAll()
        {
            if (_store.HasUncertainPersistence)
            {
                Logger.Log("ManagedFileInspector: skipping batch inspection because GameLibraryStore persistence outcome is uncertain for this session.");
                return 0;
            }

            int changesDetected = 0;
            foreach (var installation in _store.GetAll())
            {
                try
                {
                    if (InspectInstallation(installation))
                        changesDetected++;
                }
                catch (Exception ex)
                {
                    string targetName = installation?.DisplayName ?? installation?.InstallationPath ?? "unknown installation";
                    try
                    {
                        Logger.Log($"ManagedFileInspector: inspection failed for {targetName}: {ex.Message}");
                    }
                    catch
                    {
                        // Best-effort logging must not prevent continuation
                    }

                    if (installation != null && !_store.HasUncertainPersistence)
                    {
                        installation.RestorationState = RestorationState.AttentionRequired;
                        installation.LastRefusalReason = string.IsNullOrWhiteSpace(ex.Message)
                            ? "Inspection failed."
                            : $"Inspection failed: {ex.Message}";

                        try
                        {
                            _store.Save(installation);
                        }
                        catch (Exception saveEx)
                        {
                            try
                            {
                                Logger.Log($"ManagedFileInspector: failed to persist inspection failure state for {targetName}: {saveEx.Message}");
                            }
                            catch
                            {
                                // Best-effort persistence must not prevent batch continuation
                            }
                        }
                    }
                }
            }
            return changesDetected;
        }
    }
}
