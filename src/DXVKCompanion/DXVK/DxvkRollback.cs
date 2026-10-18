using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;

namespace DXVKCompanion.DXVK
{
    public class DxvkRollback
    {
        private readonly FileUtils _files;
        private readonly MultiFileTransactionEngine _transactionEngine;
        private readonly GameLibraryStore _gameLibraryStore;

        public string? LastRefusalReason { get; private set; }

        public DxvkRollback(
            MultiFileTransactionEngine? transactionEngine = null,
            GameLibraryStore? gameLibraryStore = null,
            FileUtils? files = null)
        {
            _transactionEngine = transactionEngine ?? new MultiFileTransactionEngine(GameLibraryPaths.BackupsDir);
            _gameLibraryStore = gameLibraryStore ?? new GameLibraryStore();
            _files = files ?? new FileUtils();
        }

        public DxvkRollback(FileUtils files)
            : this(null, null, files)
        {
        }

        public async Task<bool> RestoreOriginalDllsAsync(GameProfile profile)
        {
            LastRefusalReason = null;
            return await Task.Run(() =>
            {
                    GameInstallation? installation = null;
                    try
                    {
                        string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                            return false;

                        installation = _gameLibraryStore.FindInstallationForExecutable(profile.ExePath)
                            ?? _gameLibraryStore.FindByInstallationPath(gameDir);

                        if (!_gameLibraryStore.TryCheckCanModifyLibrary(out var storeRefusalReason))
                        {
                            LastRefusalReason = storeRefusalReason;
                            if (installation != null)
                            {
                                installation.RestorationState = RestorationState.AttentionRequired;
                                installation.LastRefusalReason = storeRefusalReason;
                            }
                            Logger.Log($"DxvkRollback: {storeRefusalReason}");
                            return false;
                        }

                        if (installation == null)
                        {
                            var reason = $"Refusing rollback on {profile.ExeName}; missing ownership records for game directory '{gameDir}'.";
                            LastRefusalReason = reason;
                            Logger.Log($"DxvkRollback: {reason}");
                            return false;
                        }

                        if (installation.ConflictFlags != InstallationConflictFlags.None)
                        {
                            var conflictReason = $"Refusing rollback on {installation.DisplayName}; installation has conflict flags: {installation.ConflictFlags}.";
                            return RefuseRollback(installation, conflictReason);
                        }

                        if (installation.ManagedFiles == null || installation.ManagedFiles.Count == 0)
                        {
                            string reason = (installation.RestorationState == RestorationState.Managed || !string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion))
                                ? $"Refusing rollback on {installation.DisplayName ?? profile.ExeName}; missing ownership evidence for actively managed installation."
                                : $"Refusing rollback on {installation.DisplayName ?? profile.ExeName}; missing ownership evidence for managed installation.";
                            return RefuseRollback(installation, reason);
                        }
                            bool isAlreadyRestoredCandidate = installation.RestorationState == RestorationState.Restored
                                || string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion);

                            if (isAlreadyRestoredCandidate)
                            {
                                foreach (var mf in installation.ManagedFiles)
                                {
                                    if (mf.OriginalState == FileOriginalState.Unknown)
                                    {
                                        return RefuseRollback(installation, $"Original state is unknown for managed file: {mf.RelativePath}");
                                    }

                                    var targetPath = Path.Combine(installation.InstallationPath, mf.RelativePath);

                                    if (mf.OriginalState == FileOriginalState.Missing)
                                    {
                                        if (File.Exists(targetPath))
                                        {
                                            return RefuseRollback(installation, $"Already restored installation has unexpected file present: {mf.RelativePath}");
                                        }
                                    }
                                    else if (mf.OriginalState == FileOriginalState.Existing)
                                    {
                                        if (!IsValidSha256(mf.OriginalSha256))
                                        {
                                            return RefuseRollback(installation, $"Original hash baseline is missing or malformed for restore: {mf.RelativePath}");
                                        }

                                        if (!File.Exists(targetPath))
                                        {
                                            return RefuseRollback(installation, $"Already restored original file is missing: {mf.RelativePath}");
                                        }

                                        var currentIdentity = FileIdentity.Capture(targetPath);
                                        if (!string.Equals(currentIdentity.Sha256, mf.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                                        {
                                            return RefuseRollback(installation, $"Already restored original file hash mismatch: {mf.RelativePath} (expected original {mf.OriginalSha256}, actual {currentIdentity.Sha256})");
                                        }
                                    }
                                }

                                var preCandidateSnapshot = _gameLibraryStore.CreateSnapshot(installation);
                                installation.RestorationState = RestorationState.Restored;
                                installation.LastRefusalReason = null;
                                installation.LastSeenUtc = DateTime.UtcNow;

                                var candidateSave = _gameLibraryStore.TrySaveWithOutcome(installation);
                                if (candidateSave.Outcome == OwnershipPersistenceOutcome.Committed)
                                {
                                    return true;
                                }

                                LastRefusalReason = candidateSave.DiagnosticMessage
                                    ?? candidateSave.Exception?.Message
                                    ?? "Failed to persist already-restored installation state.";
                                if (candidateSave.Outcome == OwnershipPersistenceOutcome.ProvenNotCommitted)
                                {
                                    _gameLibraryStore.RestoreSnapshot(installation, preCandidateSnapshot);
                                }
                                else
                                {
                                    installation.RestorationState = RestorationState.AttentionRequired;
                                    installation.LastRefusalReason = LastRefusalReason;
                                }
                                return false;
                            }

                            var filesToRestore = new List<MultiFileTransactionFile>();

                            foreach (var mf in installation.ManagedFiles)
                            {
                                if (mf.OriginalState == FileOriginalState.Unknown)
                                {
                                    return RefuseRollback(installation, $"Original state is unknown for managed file: {mf.RelativePath}");
                                }

                                if (!IsValidSha256(mf.ExpectedManagedSha256))
                                {
                                    return RefuseRollback(installation, $"Managed hash baseline is missing or malformed for restore: {mf.RelativePath}");
                                }

                                var targetPath = Path.Combine(installation.InstallationPath, mf.RelativePath);
                                if (!File.Exists(targetPath))
                                {
                                    return RefuseRollback(installation, $"Managed target file is missing for restore: {mf.RelativePath}");
                                }

                                var currentTargetIdentity = FileIdentity.Capture(targetPath);
                                if (!string.Equals(currentTargetIdentity.Sha256, mf.ExpectedManagedSha256, StringComparison.OrdinalIgnoreCase))
                                {
                                    return RefuseRollback(installation, $"Managed target hash mismatch for restore: {mf.RelativePath} (expected managed {mf.ExpectedManagedSha256}, actual {currentTargetIdentity.Sha256})");
                                }

                                if (mf.OriginalState == FileOriginalState.Existing && !IsValidSha256(mf.OriginalSha256))
                                {
                                    return RefuseRollback(installation, $"Original hash baseline is missing or malformed for restore: {mf.RelativePath}");
                                }

                                var safetyOriginalState = mf.OriginalState switch
                                {
                                    FileOriginalState.Existing => OriginalFileState.Existing,
                                    FileOriginalState.Missing => OriginalFileState.DidNotExist,
                                    _ => OriginalFileState.Unknown
                                };

                                filesToRestore.Add(new MultiFileTransactionFile
                                {
                                    RelativePath = mf.RelativePath,
                                    OriginalState = safetyOriginalState,
                                    BackupRelativePath = mf.BackupRelativePath,
                                    ExpectedTargetIdentity = currentTargetIdentity,
                                    ExpectedOriginalSha256 = mf.OriginalSha256,
                                    ExpectedManagedSha256 = mf.ExpectedManagedSha256
                                });
                            }

                            if (filesToRestore.Count > 0)
                            {
                                var preOperationSnapshot = _gameLibraryStore.CreateSnapshot(installation);
                                _gameLibraryStore.CaptureCurrentLibraryBaseline(out var preOperationLibraryBytes, out bool preOperationLibraryExisted);

                                var request = new MultiFileTransactionRequest
                                {
                                    InstallationRoot = installation.InstallationPath,
                                    Operation = TransactionOperation.Restore,
                                    Files = filesToRestore
                                };

                                bool saveCallbackInvoked = false;
                                OwnershipSaveResult lastSaveResult = new OwnershipSaveResult(
                                    OwnershipPersistenceOutcome.Unknown,
                                    DiagnosticMessage: "Ownership commit callback did not complete.");

                                var result = _transactionEngine.ExecuteWithCommitScope(request, _ =>
                                {
                                    saveCallbackInvoked = true;
                                    try
                                    {
                                        foreach (var mf in installation.ManagedFiles)
                                        {
                                            mf.CurrentState = ManagedFileState.Consistent;
                                            mf.ManagedDxvkVersion = null;
                                            mf.LastVerifiedUtc = DateTime.UtcNow;
                                        }

                                        installation.ManagedDxvkVersion = null;
                                        installation.RestorationState = RestorationState.Restored;
                                        installation.LastRefusalReason = null;
                                        installation.LastSeenUtc = DateTime.UtcNow;

                                        lastSaveResult = _gameLibraryStore.TrySaveWithOutcome(installation);
                                        return lastSaveResult;
                                    }
                                    catch (Exception callbackEx)
                                    {
                                        string reason = $"Unexpected exception in ownership commit callback: {callbackEx.GetType().Name} - {callbackEx.Message}";
                                        _gameLibraryStore.RecordUnexpectedCommitCallbackException(reason, installation);
                                        lastSaveResult = new OwnershipSaveResult(
                                            OwnershipPersistenceOutcome.Unknown,
                                            Exception: callbackEx,
                                            DiagnosticMessage: reason,
                                            PreWriteBytes: preOperationLibraryBytes,
                                            PreWriteExisted: preOperationLibraryExisted);
                                        throw;
                                    }
                                });

                                if (result.State != TransactionState.Committed || result.Outcome != TransactionOutcome.Success)
                                {
                                    var reason = result.Message ?? $"Restore transaction failed ({result.State}/{result.Outcome})";
                                    LastRefusalReason = reason;

                                    if (result.State == TransactionState.AttentionRequired || result.Outcome == TransactionOutcome.UnresolvedRecovery)
                                    {
                                        if (saveCallbackInvoked && lastSaveResult.Outcome == OwnershipPersistenceOutcome.Unknown)
                                        {
                                            installation.RestorationState = RestorationState.AttentionRequired;
                                            installation.LastRefusalReason = reason;
                                        }
                                        else
                                        {
                                            _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                                            installation.RestorationState = RestorationState.AttentionRequired;
                                            installation.LastRefusalReason = reason;

                                            byte[]? expectedPreWriteBytes = saveCallbackInvoked ? lastSaveResult.PreWriteBytes : preOperationLibraryBytes;
                                            bool expectedPreWriteExisted = saveCallbackInvoked ? lastSaveResult.PreWriteExisted : preOperationLibraryExisted;
                                            _gameLibraryStore.TrySaveHealthAfterFailedCompensation(installation, expectedPreWriteBytes, expectedPreWriteExisted);

                                            installation.RestorationState = RestorationState.AttentionRequired;
                                            installation.LastRefusalReason = reason;
                                        }

                                        Logger.Log($"DxvkRollback: {reason}");
                                        return false;
                                    }

                                    if (saveCallbackInvoked && lastSaveResult.Outcome == OwnershipPersistenceOutcome.ProvenNotCommitted)
                                    {
                                        _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                                        Logger.Log($"DxvkRollback: {reason}");
                                        return false;
                                    }

                                    _gameLibraryStore.RestoreSnapshot(installation, preOperationSnapshot);
                                    return RefuseRollback(installation, reason);
                                }

                                Logger.Log($"DxvkRollback: successfully restored {filesToRestore.Count} files for {profile.ExeName} via safe transaction {result.TransactionId}.");
                                return true;
                            }

                            return false;
                    }
                catch (Exception ex)
                {
                    var reason = $"Unexpected error restoring {profile.ExeName}: {ex.GetType().Name} - {ex.Message}";
                    Logger.Log($"DxvkRollback: {reason}");
                    LastRefusalReason = reason;
                    if (installation != null)
                    {
                        installation.RestorationState = RestorationState.AttentionRequired;
                        installation.LastRefusalReason = reason;
                        try
                        {
                            if (!_gameLibraryStore.HasUncertainPersistence)
                            {
                                _gameLibraryStore.Save(installation);
                            }
                        }
                        catch { }
                    }
                    return false;
                }
            });
        }

        private bool RefuseRollback(GameInstallation installation, string reason)
        {
            Logger.Log($"DxvkRollback: {reason}");
            LastRefusalReason = reason;
            installation.RestorationState = RestorationState.AttentionRequired;
            installation.LastRefusalReason = reason;
            try
            {
                if (!_gameLibraryStore.HasUncertainPersistence)
                {
                    _gameLibraryStore.Save(installation);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkRollback: could not persist rollback refusal state: {ex.Message}");
            }
            return false;
        }

        private static bool IsValidSha256(string? sha256) =>
            !string.IsNullOrWhiteSpace(sha256) &&
            sha256.Length == 64 &&
            sha256.All(static c => Uri.IsHexDigit(c));
    }
}
