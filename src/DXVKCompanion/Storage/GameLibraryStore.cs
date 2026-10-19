using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;

namespace DXVKCompanion.Storage
{
    internal enum OwnershipPersistenceOutcome
    {
        Unknown = 0,
        Committed = 1,
        ProvenNotCommitted = 2
    }

    internal readonly record struct OwnershipSaveResult(
        OwnershipPersistenceOutcome Outcome,
        Exception? Exception = null,
        string? DiagnosticMessage = null,
        byte[]? PreWriteBytes = null,
        bool PreWriteExisted = false);

    public sealed class GameLibraryStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, GameInstallation> _installations =
            new(StringComparer.OrdinalIgnoreCase);

        internal Action<string, string>? CustomTempWriter { get; set; }
        internal Action<string, string>? CustomCommitReplace { get; set; }
        internal Action<string>? CustomTempCleaner { get; set; }
        internal Action<GameInstallation>? CustomSaveOutcomeHook { get; set; }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private const string RecoverySuffix = ".recovery";

        private readonly string _libraryFilePath;
        private readonly string _backupsDirectoryPath;

        public string LibraryFilePath => _libraryFilePath;
        internal IOperationLeaseCoordinator LeaseCoordinator { get; set; } = OperationLeaseCoordinator.Default;

        private bool _isRefused;
        private string? _refusedSchemaText;

        private bool _hasUncertainPersistence;
        private string? _uncertainPersistenceReason;

        private bool _hasInaccessibleLibraryOnLoad;
        private string? _inaccessibleLibraryReason;

        private bool _loadedFromBrokenFileRecovery;
        private bool _lastCommittedExisted;
        private byte[]? _lastCommittedLibraryBytes;

        internal bool IsRefused
        {
            get
            {
                lock (_sync)
                {
                    return _isRefused;
                }
            }
        }

        internal string? RefusedSchemaVersionText
        {
            get
            {
                lock (_sync)
                {
                    return _refusedSchemaText;
                }
            }
        }

        internal bool HasUncertainPersistence
        {
            get
            {
                lock (_sync)
                {
                    return _hasUncertainPersistence;
                }
            }
        }

        internal string? UncertainPersistenceReason
        {
            get
            {
                lock (_sync)
                {
                    return _uncertainPersistenceReason;
                }
            }
        }

        public GameLibraryStore(
            string? libraryFilePath = null,
            string? backupsDirectoryPath = null,
            string? legacyProfilesPath = null)
        {
            _libraryFilePath = libraryFilePath ?? GameLibraryPaths.GameLibraryFile;
            _backupsDirectoryPath = backupsDirectoryPath ?? GameLibraryPaths.BackupsDir;
            // legacyProfilesPath is retained for caller compatibility, but clean-slate V1 policy does not import legacy profiles.
            Load();
        }

        public IReadOnlyCollection<GameInstallation> GetAll()
        {
            lock (_sync)
            {
                return _installations.Values.ToList().AsReadOnly();
            }
        }

        public GameInstallation? FindByInstallationPath(string installationPath)
        {
            var normalized = GameInstallation.NormalizeInstallationPath(installationPath);
            lock (_sync)
            {
                return _installations.TryGetValue(normalized, out var installation)
                    ? installation
                    : null;
            }
        }

        public GameInstallation? FindInstallationForExecutable(string exeFullPath)
        {
            if (string.IsNullOrWhiteSpace(exeFullPath)) return null;

            var fullExe = Path.GetFullPath(exeFullPath);
            var exeDir = Path.GetDirectoryName(fullExe);

            lock (_sync)
            {
                // 1. Direct match on executable directory
                if (exeDir != null)
                {
                    var normalizedDir = GameInstallation.NormalizeInstallationPath(exeDir);
                    if (_installations.TryGetValue(normalizedDir, out var direct))
                        return direct;
                }

                // 2. Match on existing installation where executable path is inside installation root
                foreach (var installation in _installations.Values)
                {
                    var rootWithSep = installation.InstallationPath.EndsWith(Path.DirectorySeparatorChar)
                        ? installation.InstallationPath
                        : installation.InstallationPath + Path.DirectorySeparatorChar;

                    if (fullExe.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                    {
                        return installation;
                    }
                }

                return null;
            }
        }

        public GameInstallation GetOrCreateInstallation(string installationPath, string? displayName = null)
        {
            var normalized = GameInstallation.NormalizeInstallationPath(installationPath);

            lock (_sync)
            {
                EnsureNotRefusedLocked();

                if (_installations.TryGetValue(normalized, out var existing))
                    return existing;

                var installation = new GameInstallation
                {
                    InstallationPath = normalized,
                    DisplayName = string.IsNullOrWhiteSpace(displayName)
                        ? new DirectoryInfo(normalized).Name
                        : displayName
                };

                var result = ExecuteClassifiedCommitLocked(
                    applyInMemoryMutation: () => _installations[normalized] = installation,
                    rollbackInMemoryMutationOnProvenNotCommitted: () => _installations.Remove(normalized),
                    markAttentionOnUnknown: reason =>
                    {
                        installation.RestorationState = RestorationState.AttentionRequired;
                        installation.LastRefusalReason = reason;
                    });

                if (result.Outcome == OwnershipPersistenceOutcome.Committed)
                {
                    return installation;
                }

                throw result.Exception ?? new IOException(result.DiagnosticMessage ?? "Failed to persist new installation record.");
            }
        }

        private IOperationLease? ValidateOrAcquireStoreLease(IOperationLease? storeLease, out bool ownsLease)
        {
            if (storeLease != null)
            {
                if (storeLease.IsDisposed || !storeLease.IsAcquired || !storeLease.IsValidForStore(_libraryFilePath))
                {
                    throw new ArgumentException("Invalid, mismatched, or disposed store lease token.", nameof(storeLease));
                }
                ownsLease = false;
                return storeLease;
            }

            var acquired = LeaseCoordinator.TryAcquireStoreLease(_libraryFilePath, TimeSpan.FromMilliseconds(500));
            ownsLease = acquired != null;
            return acquired;
        }

        public GameInstallation RecordDetectionSnapshot(DetectionSnapshot snapshot) =>
            RecordDetectionSnapshot(snapshot, null);

        internal GameInstallation RecordDetectionSnapshot(DetectionSnapshot snapshot, IOperationLease? storeLease)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            var lease = ValidateOrAcquireStoreLease(storeLease, out bool ownsLease);
            if (lease == null)
            {
                throw new IOException($"Operation in progress for game library store: {_libraryFilePath}. Please retry shortly.");
            }

            try
            {
                lock (_sync)
                {
                    EnsureNotRefusedLocked();

                var normalized = GameInstallation.NormalizeInstallationPath(snapshot.InstallationRoot);
                bool hadExisting = _installations.TryGetValue(normalized, out var existingInstallation);
                GameInstallation? existingSnapshot = hadExisting && existingInstallation != null
                    ? CreateSnapshot(existingInstallation)
                    : null;
                bool prevHasApiChanged = snapshot.HasApiChanged;
                var prevPreviousApi = snapshot.PreviousApi;

                GameInstallation? targetInstallation = null;

                var result = ExecuteClassifiedCommitLocked(
                    applyInMemoryMutation: () =>
                    {
                        if (hadExisting && existingInstallation != null)
                        {
                            targetInstallation = existingInstallation;
                        }
                        else
                        {
                            targetInstallation = new GameInstallation
                            {
                                InstallationPath = normalized,
                                DisplayName = string.IsNullOrWhiteSpace(Path.GetFileName(snapshot.InstallationRoot))
                                    ? new DirectoryInfo(normalized).Name
                                    : Path.GetFileName(snapshot.InstallationRoot)
                            };
                            _installations[normalized] = targetInstallation;
                        }

                        var profile = targetInstallation.GetOrAddExecutable(snapshot.ExecutableRelativePath, snapshot.ProcessName);

                        if (profile.LastKnownApi != GraphicsApi.Unknown &&
                            snapshot.Classification.PrimaryApi != GraphicsApi.Unknown &&
                            profile.LastKnownApi != snapshot.Classification.PrimaryApi)
                        {
                            snapshot.HasApiChanged = true;
                            snapshot.PreviousApi = profile.LastKnownApi;
                            Log($"GameLibraryStore: API transition detected for {snapshot.ExecutableRelativePath}: {profile.LastKnownApi} -> {snapshot.Classification.PrimaryApi}");
                        }

                        profile.LastKnownApi = snapshot.Classification.PrimaryApi;
                        profile.ApiConfidence = snapshot.Classification.Confidence;
                        profile.LastKnownArchitecture = snapshot.Classification.Architecture;
                        profile.DetectionEvidence = snapshot.Classification.Evidence.ToList();
                        profile.LastSeenUtc = snapshot.TimestampUtc;

                        targetInstallation.LastSeenUtc = snapshot.TimestampUtc;
                    },
                    rollbackInMemoryMutationOnProvenNotCommitted: () =>
                    {
                        snapshot.HasApiChanged = prevHasApiChanged;
                        snapshot.PreviousApi = prevPreviousApi;
                        if (hadExisting && existingInstallation != null && existingSnapshot != null)
                        {
                            RestoreSnapshot(existingInstallation, existingSnapshot);
                            _installations[normalized] = existingInstallation;
                        }
                        else
                        {
                            _installations.Remove(normalized);
                        }
                    },
                    markAttentionOnUnknown: reason =>
                    {
                        if (targetInstallation != null)
                        {
                            targetInstallation.RestorationState = RestorationState.AttentionRequired;
                            targetInstallation.LastRefusalReason = reason;
                        }
                        if (existingInstallation != null && !ReferenceEquals(existingInstallation, targetInstallation))
                        {
                            existingInstallation.RestorationState = RestorationState.AttentionRequired;
                            existingInstallation.LastRefusalReason = reason;
                        }
                    });

                if (result.Outcome == OwnershipPersistenceOutcome.Committed && targetInstallation != null)
                {
                    return targetInstallation;
                }

                throw result.Exception ?? new IOException(result.DiagnosticMessage ?? "Failed to persist detection snapshot.");
            }
            finally
            {
                if (ownsLease) lease.Dispose();
            }
        }

        public void Save(GameInstallation installation) => Save(installation, null);

        internal void Save(GameInstallation installation, IOperationLease? storeLease)
        {
            if (installation == null) throw new ArgumentNullException(nameof(installation));

            var lease = ValidateOrAcquireStoreLease(storeLease, out bool ownsLease);
            if (lease == null)
            {
                throw new IOException($"Operation in progress for game library store: {_libraryFilePath}. Please retry shortly.");
            }

            try
            {
                lock (_sync)
                {
                    EnsureNotRefusedLocked();

                    var normalizedPath = GameInstallation.NormalizeInstallationPath(installation.InstallationPath);
                    bool hadPrevious = _installations.TryGetValue(normalizedPath, out var previousEntry);
                    GameInstallation? previousSnapshot = hadPrevious && previousEntry != null
                        ? CreateSnapshot(previousEntry)
                        : null;
                    var previousId = installation.Id;

                    var result = ExecuteClassifiedCommitLocked(
                        applyInMemoryMutation: () =>
                        {
                            installation.InstallationPath = normalizedPath;
                            if (string.IsNullOrWhiteSpace(installation.Id))
                                installation.Id = Guid.NewGuid().ToString("N");

                            _installations[normalizedPath] = installation;
                        },
                        rollbackInMemoryMutationOnProvenNotCommitted: () =>
                        {
                            if (hadPrevious && previousEntry != null)
                            {
                                if (previousSnapshot != null)
                                {
                                    RestoreSnapshot(previousEntry, previousSnapshot);
                                }
                                _installations[normalizedPath] = previousEntry;
                            }
                            else
                            {
                                _installations.Remove(normalizedPath);
                            }
                            installation.Id = previousId;
                        },
                        markAttentionOnUnknown: reason =>
                        {
                            installation.RestorationState = RestorationState.AttentionRequired;
                            installation.LastRefusalReason = reason;
                            if (previousEntry != null && !ReferenceEquals(previousEntry, installation))
                            {
                                previousEntry.RestorationState = RestorationState.AttentionRequired;
                                previousEntry.LastRefusalReason = reason;
                            }
                        });

                    if (result.Outcome == OwnershipPersistenceOutcome.Committed)
                    {
                        return;
                    }

                    throw result.Exception ?? new IOException(result.DiagnosticMessage ?? "Failed to save installation record.");
                }
            }
            finally
            {
                if (ownsLease) lease.Dispose();
            }
        }

        public void SaveAll() => SaveAll(null);

        internal void SaveAll(IOperationLease? storeLease)
        {
            var lease = ValidateOrAcquireStoreLease(storeLease, out bool ownsLease);
            if (lease == null)
            {
                throw new IOException($"Operation in progress for game library store: {_libraryFilePath}. Please retry shortly.");
            }

            try
            {
                lock (_sync)
                {
                    EnsureNotRefusedLocked();

                    var result = ExecuteClassifiedCommitLocked(
                        applyInMemoryMutation: static () => { },
                        rollbackInMemoryMutationOnProvenNotCommitted: static () => { },
                        markAttentionOnUnknown: reason =>
                        {
                            foreach (var inst in _installations.Values)
                            {
                                inst.RestorationState = RestorationState.AttentionRequired;
                                inst.LastRefusalReason = reason;
                            }
                        });

                    if (result.Outcome == OwnershipPersistenceOutcome.Committed)
                    {
                        return;
                    }

                    throw result.Exception ?? new IOException(result.DiagnosticMessage ?? "Failed to save game library.");
                }
            }
            finally
            {
                if (ownsLease) lease.Dispose();
            }
        }

        internal bool TryCheckCanModifyLibrary(out string? refusalReason, IOperationLease? storeLease = null)
        {
            var lease = ValidateOrAcquireStoreLease(storeLease, out bool ownsLease);
            if (lease == null)
            {
                refusalReason = $"Operation in progress for game library store: {_libraryFilePath}. Please retry shortly.";
                return false;
            }

            try
            {
                lock (_sync)
                {
                    if (!VerifyDiskMatchesAuthoritativeBaselineLocked(
                            out _,
                            out _,
                            out _,
                            out var diagMessage,
                            out _))
                    {
                        refusalReason = diagMessage ?? "Refused operation: game library cannot be safely modified.";
                        return false;
                    }

                    refusalReason = null;
                    return true;
                }
            }
            finally
            {
                if (ownsLease) lease.Dispose();
            }
        }

        internal void CaptureCurrentLibraryBaseline(out byte[]? baselineBytes, out bool baselineExisted)
        {
            lock (_sync)
            {
                baselineExisted = _lastCommittedExisted;
                baselineBytes = _lastCommittedLibraryBytes != null
                    ? (byte[])_lastCommittedLibraryBytes.Clone()
                    : null;
            }
        }

        internal OwnershipSaveResult RecordUnexpectedCommitCallbackException(
            string reason,
            GameInstallation? installation = null)
        {
            lock (_sync)
            {
                ActivateUncertainPersistenceLocked(reason);
                if (installation != null)
                {
                    installation.RestorationState = RestorationState.AttentionRequired;
                    installation.LastRefusalReason = reason;
                }
                return new OwnershipSaveResult(
                    OwnershipPersistenceOutcome.Unknown,
                    Exception: new InvalidOperationException(reason),
                    DiagnosticMessage: reason,
                    PreWriteBytes: _lastCommittedLibraryBytes,
                    PreWriteExisted: _lastCommittedExisted);
            }
        }

        internal OwnershipSaveResult RecordUnexpectedCommitCallbackException(
            Exception callbackEx,
            bool expectedPreWriteExisted,
            byte[]? expectedPreWriteBytes)
        {
            ArgumentNullException.ThrowIfNull(callbackEx);
            string reason = $"Ownership metadata commit outcome is unknown: unexpected exception in ownership commit callback ({callbackEx.GetType().Name}: {callbackEx.Message}).";
            return RecordUnexpectedCommitCallbackException(reason, null);
        }

        internal OwnershipSaveResult TrySaveWithOutcome(GameInstallation installation) =>
            TrySaveWithOutcome(installation, null);

        internal OwnershipSaveResult TrySaveWithOutcome(GameInstallation installation, IOperationLease? storeLease)
        {
            if (installation == null) throw new ArgumentNullException(nameof(installation));

            CustomSaveOutcomeHook?.Invoke(installation);

            var lease = ValidateOrAcquireStoreLease(storeLease, out bool ownsLease);
            if (lease == null)
            {
                return new OwnershipSaveResult(
                    OwnershipPersistenceOutcome.ProvenNotCommitted,
                    DiagnosticMessage: $"Operation in progress for game library store: {_libraryFilePath}. Please retry shortly.",
                    PreWriteBytes: _lastCommittedLibraryBytes,
                    PreWriteExisted: _lastCommittedExisted);
            }

            try
            {
                lock (_sync)
                {
                    var normalizedPath = GameInstallation.NormalizeInstallationPath(installation.InstallationPath);
                    bool hadPrevious = _installations.TryGetValue(normalizedPath, out var previousEntry);
                    var previousId = installation.Id;

                    return ExecuteClassifiedCommitLocked(
                        applyInMemoryMutation: () =>
                        {
                            installation.InstallationPath = normalizedPath;
                            if (string.IsNullOrWhiteSpace(installation.Id))
                                installation.Id = Guid.NewGuid().ToString("N");

                            _installations[normalizedPath] = installation;
                        },
                        rollbackInMemoryMutationOnProvenNotCommitted: () =>
                        {
                            if (hadPrevious && previousEntry != null)
                            {
                                _installations[normalizedPath] = previousEntry;
                            }
                            else
                            {
                                _installations.Remove(normalizedPath);
                            }
                            installation.Id = previousId;
                        },
                        markAttentionOnUnknown: reason =>
                        {
                            installation.RestorationState = RestorationState.AttentionRequired;
                            installation.LastRefusalReason = reason;
                            if (previousEntry != null && !ReferenceEquals(previousEntry, installation))
                            {
                                previousEntry.RestorationState = RestorationState.AttentionRequired;
                                previousEntry.LastRefusalReason = reason;
                            }
                        });
                }
            }
            finally
            {
                if (ownsLease) lease.Dispose();
            }
        }

        private OwnershipSaveResult ExecuteClassifiedCommitLocked(
            Action applyInMemoryMutation,
            Action rollbackInMemoryMutationOnProvenNotCommitted,
            Action<string>? markAttentionOnUnknown)
        {
            if (!VerifyDiskMatchesAuthoritativeBaselineLocked(
                    out var preWriteBytes,
                    out bool preWriteExisted,
                    out var baselineError,
                    out var baselineDiag,
                    out bool isPreWriteUnreadableOrSchemaRefused))
            {
                if (isPreWriteUnreadableOrSchemaRefused)
                {
                    return new OwnershipSaveResult(
                        OwnershipPersistenceOutcome.ProvenNotCommitted,
                        Exception: baselineError,
                        DiagnosticMessage: baselineDiag,
                        PreWriteBytes: _lastCommittedLibraryBytes,
                        PreWriteExisted: _lastCommittedExisted);
                }

                string uncertainReason = baselineDiag
                    ?? _uncertainPersistenceReason
                    ?? "Ownership persistence refused: on-disk library state no longer matches the authoritative store baseline.";
                markAttentionOnUnknown?.Invoke(uncertainReason);
                return new OwnershipSaveResult(
                    OwnershipPersistenceOutcome.Unknown,
                    Exception: baselineError ?? new InvalidOperationException(uncertainReason),
                    DiagnosticMessage: uncertainReason,
                    PreWriteBytes: _lastCommittedLibraryBytes,
                    PreWriteExisted: _lastCommittedExisted);
            }

            string? tempPath = null;
            byte[] expectedPostWriteBytes;

            try
            {
                applyInMemoryMutation();

                Paths.EnsureDirectories();
                Directory.CreateDirectory(_backupsDirectoryPath);

                var library = new GameLibrary
                {
                    SchemaVersion = GameLibrary.CurrentSchemaVersion,
                    Installations = _installations.Values
                        .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.InstallationPath, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };

                var json = JsonSerializer.Serialize(library, JsonOptions);
                expectedPostWriteBytes = Encoding.UTF8.GetBytes(json);
                tempPath = _libraryFilePath + ".tmp." + Guid.NewGuid().ToString("N");

                if (CustomTempWriter != null)
                {
                    CustomTempWriter(tempPath, json);
                }
                else
                {
                    File.WriteAllText(tempPath, json);
                }
            }
            catch (Exception prepOrStageEx)
            {
                if (tempPath != null)
                {
                    TryCleanTempFile(tempPath);
                }

                if (TryVerifyPreWriteIntact(preWriteExisted, preWriteBytes, out var verifyDiag))
                {
                    try { rollbackInMemoryMutationOnProvenNotCommitted(); } catch { }
                    return new OwnershipSaveResult(
                        OwnershipPersistenceOutcome.ProvenNotCommitted,
                        Exception: prepOrStageEx,
                        DiagnosticMessage: $"Ownership metadata preparation or staging failed before replacement ({prepOrStageEx.GetType().Name}: {prepOrStageEx.Message}). Pre-operation library state verified intact.",
                        PreWriteBytes: preWriteBytes,
                        PreWriteExisted: preWriteExisted);
                }

                string stageUncertainReason = $"Ownership metadata preparation or staging failed ({prepOrStageEx.GetType().Name}: {prepOrStageEx.Message}) and pre-operation library state could not be verified ({verifyDiag}).";
                ActivateUncertainPersistenceLocked(stageUncertainReason);
                markAttentionOnUnknown?.Invoke(stageUncertainReason);
                return new OwnershipSaveResult(
                    OwnershipPersistenceOutcome.Unknown,
                    Exception: prepOrStageEx,
                    DiagnosticMessage: stageUncertainReason,
                    PreWriteBytes: preWriteBytes,
                    PreWriteExisted: preWriteExisted);
            }

            Exception? replaceEx = null;
            try
            {
                if (CustomCommitReplace != null)
                {
                    CustomCommitReplace(tempPath, _libraryFilePath);
                }
                else
                {
                    if (File.Exists(_libraryFilePath))
                    {
                        File.Move(tempPath, _libraryFilePath, overwrite: true);
                    }
                    else
                    {
                        File.Move(tempPath, _libraryFilePath);
                    }
                }
            }
            catch (Exception ex)
            {
                replaceEx = ex;
            }

            if (!TryReadFileBytesOrConfirmAbsent(_libraryFilePath, out var postBytes, out bool postAbsent, out var postReadEx))
            {
                TryCleanTempFile(tempPath);
                string reason = $"Ownership metadata commit outcome is unknown: post-replace readback of '{Path.GetFileName(_libraryFilePath)}' failed ({postReadEx?.GetType().Name}: {postReadEx?.Message})"
                    + (replaceEx != null ? $" after replace error ({replaceEx.GetType().Name}: {replaceEx.Message})." : ".");
                ActivateUncertainPersistenceLocked(reason);
                markAttentionOnUnknown?.Invoke(reason);
                return new OwnershipSaveResult(
                    OwnershipPersistenceOutcome.Unknown,
                    Exception: replaceEx ?? postReadEx,
                    DiagnosticMessage: reason,
                    PreWriteBytes: preWriteBytes,
                    PreWriteExisted: preWriteExisted);
            }

            if (postAbsent)
            {
                if (!preWriteExisted && IsTempFileUnappliedWithExpectedBytes(tempPath, expectedPostWriteBytes))
                {
                    TryCleanTempFile(tempPath);
                    try { rollbackInMemoryMutationOnProvenNotCommitted(); } catch { }
                    return new OwnershipSaveResult(
                        OwnershipPersistenceOutcome.ProvenNotCommitted,
                        Exception: replaceEx,
                        DiagnosticMessage: $"Ownership metadata replacement did not commit ({replaceEx?.GetType().Name ?? "NoReplace"}: {replaceEx?.Message ?? "Target absent"}); verified no prior library existed and staged temp file remained unapplied.",
                        PreWriteBytes: null,
                        PreWriteExisted: false);
                }

                TryCleanTempFile(tempPath);
                string missingReason = preWriteExisted
                    ? $"Ownership metadata commit outcome is unknown: '{Path.GetFileName(_libraryFilePath)}' existed before replacement but is missing after replacement."
                    : $"Ownership metadata commit outcome is unknown: '{Path.GetFileName(_libraryFilePath)}' is missing after replacement without proof that the staged temp file remained unapplied.";
                ActivateUncertainPersistenceLocked(missingReason);
                markAttentionOnUnknown?.Invoke(missingReason);
                return new OwnershipSaveResult(
                    OwnershipPersistenceOutcome.Unknown,
                    Exception: replaceEx ?? new IOException(missingReason),
                    DiagnosticMessage: missingReason,
                    PreWriteBytes: preWriteBytes,
                    PreWriteExisted: preWriteExisted);
            }

            if (postBytes != null &&
                postBytes.AsSpan().SequenceEqual(expectedPostWriteBytes) &&
                IsValidCurrentSchemaLibraryJson(postBytes))
            {
                TryCleanTempFile(tempPath);
                _lastCommittedExisted = true;
                _lastCommittedLibraryBytes = expectedPostWriteBytes;
                _loadedFromBrokenFileRecovery = false;

                if (replaceEx != null)
                {
                    Log($"GameLibraryStore: post-replace exception ({replaceEx.GetType().Name}: {replaceEx.Message}) occurred after target library was already durably replaced; verified on-disk bytes match expected commit.");
                }

                return new OwnershipSaveResult(
                    OwnershipPersistenceOutcome.Committed,
                    Exception: replaceEx,
                    DiagnosticMessage: replaceEx != null
                        ? $"Ownership metadata verified committed on disk despite post-replace exception ({replaceEx.GetType().Name}: {replaceEx.Message})."
                        : null,
                    PreWriteBytes: preWriteBytes,
                    PreWriteExisted: preWriteExisted);
            }

            if (preWriteExisted &&
                preWriteBytes != null &&
                postBytes != null &&
                postBytes.AsSpan().SequenceEqual(preWriteBytes))
            {
                TryCleanTempFile(tempPath);
                try { rollbackInMemoryMutationOnProvenNotCommitted(); } catch { }
                return new OwnershipSaveResult(
                    OwnershipPersistenceOutcome.ProvenNotCommitted,
                    Exception: replaceEx,
                    DiagnosticMessage: $"Ownership metadata replacement did not commit ({replaceEx?.GetType().Name ?? "ReplaceIncomplete"}: {replaceEx?.Message ?? "Target unchanged"}); verified pre-write library bytes remain intact on disk.",
                    PreWriteBytes: preWriteBytes,
                    PreWriteExisted: true);
            }

            TryCleanTempFile(tempPath);

            string mismatchReason;
            if (postBytes != null)
            {
                var postAssessment = GameLibrarySchemaCompatibility.AssessJson(postBytes);
                if (postAssessment.IsRefused)
                {
                    _isRefused = true;
                    _refusedSchemaText = postAssessment.FoundSchemaVersionText;
                    mismatchReason = $"Ownership metadata commit outcome is unknown: on-disk '{Path.GetFileName(_libraryFilePath)}' declares newer schema {postAssessment.FoundSchemaVersionText} (supported: {GameLibrary.CurrentSchemaVersion}).";
                }
                else if (postAssessment.Status == SchemaCompatibilityStatus.Malformed || !IsValidCurrentSchemaLibraryJson(postBytes))
                {
                    mismatchReason = $"Ownership metadata commit outcome is unknown: on-disk '{Path.GetFileName(_libraryFilePath)}' contains malformed or unparseable JSON after replacement.";
                }
                else
                {
                    mismatchReason = $"Ownership metadata commit outcome is unknown: on-disk '{Path.GetFileName(_libraryFilePath)}' contains conflicting JSON that matches neither the expected commit nor the pre-write baseline.";
                }
            }
            else
            {
                mismatchReason = $"Ownership metadata commit outcome is unknown: on-disk '{Path.GetFileName(_libraryFilePath)}' could not be verified after replacement.";
            }

            ActivateUncertainPersistenceLocked(mismatchReason);
            markAttentionOnUnknown?.Invoke(mismatchReason);
            return new OwnershipSaveResult(
                OwnershipPersistenceOutcome.Unknown,
                Exception: replaceEx ?? new InvalidOperationException(mismatchReason),
                DiagnosticMessage: mismatchReason,
                PreWriteBytes: preWriteBytes,
                PreWriteExisted: preWriteExisted);
        }

        internal bool TrySaveHealthAfterFailedCompensation(
            GameInstallation installation,
            byte[]? expectedPreWriteBytes,
            bool preWriteExisted,
            IOperationLease? storeLease = null)
        {
            if (installation == null) throw new ArgumentNullException(nameof(installation));

            lock (_sync)
            {
                if (_hasUncertainPersistence || _isRefused || _hasInaccessibleLibraryOnLoad)
                {
                    return false;
                }

                if (!preWriteExisted || expectedPreWriteBytes == null)
                {
                    return false;
                }

                var priorAssessment = GameLibrarySchemaCompatibility.AssessJson(expectedPreWriteBytes);
                if (!priorAssessment.IsCompatible ||
                    priorAssessment.Status != SchemaCompatibilityStatus.Compatible ||
                    !IsValidCurrentSchemaLibraryJson(expectedPreWriteBytes))
                {
                    return false;
                }

                if (!TryReadFileBytesOrConfirmAbsent(_libraryFilePath, out var currentDiskBytes, out bool positivelyAbsent, out var readEx) ||
                    readEx != null ||
                    positivelyAbsent ||
                    currentDiskBytes == null ||
                    !currentDiskBytes.AsSpan().SequenceEqual(expectedPreWriteBytes))
                {
                    if (readEx == null && (positivelyAbsent || currentDiskBytes == null || !currentDiskBytes.AsSpan().SequenceEqual(expectedPreWriteBytes)))
                    {
                        ActivateUncertainPersistenceLocked(
                            $"Cannot persist health state after failed compensation: on-disk '{Path.GetFileName(_libraryFilePath)}' no longer matches the pre-operation baseline.");
                    }
                    return false;
                }

                var healthResult = TrySaveWithOutcome(installation, storeLease);
                return healthResult.Outcome == OwnershipPersistenceOutcome.Committed;
            }
        }

        internal GameInstallation CreateSnapshot(GameInstallation source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            string json = JsonSerializer.Serialize(source, JsonOptions);
            return JsonSerializer.Deserialize<GameInstallation>(json, JsonOptions)
                ?? throw new InvalidOperationException("Failed to deserialize GameInstallation snapshot.");
        }

        internal void RestoreSnapshot(GameInstallation target, GameInstallation snapshot)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var copy = CreateSnapshot(snapshot);
            target.Id = copy.Id;
            target.InstallationPath = copy.InstallationPath;
            target.DisplayName = copy.DisplayName;
            target.IsHidden = copy.IsHidden;
            target.ManagementPolicy = copy.ManagementPolicy;
            target.ManagedDxvkVersion = copy.ManagedDxvkVersion;
            target.ManagedDxvkArchitecture = copy.ManagedDxvkArchitecture;
            target.RestorationState = copy.RestorationState;
            target.ConflictFlags = copy.ConflictFlags;
            target.Configuration = copy.Configuration;
            target.Executables = copy.Executables;
            target.ManagedFiles = copy.ManagedFiles;
            target.PendingAction = copy.PendingAction;
            target.LastCancelledAction = copy.LastCancelledAction;
            target.LastCancellationReason = copy.LastCancellationReason;
            target.LastCancellationOutcome = copy.LastCancellationOutcome;
            target.LastRefusalReason = copy.LastRefusalReason;
            target.CreatedUtc = copy.CreatedUtc;
            target.LastSeenUtc = copy.LastSeenUtc;
        }

        private void ActivateUncertainPersistenceLocked(string reason)
        {
            _hasUncertainPersistence = true;
            _uncertainPersistenceReason = reason;
            Log($"GameLibraryStore: activated session-scoped uncertain persistence guard: {reason}");
        }

        private bool TryVerifyPreWriteIntact(bool preWriteExisted, byte[]? preWriteBytes, out string diagnostic)
        {
            if (!TryReadFileBytesOrConfirmAbsent(_libraryFilePath, out var currentBytes, out bool positivelyAbsent, out var readEx))
            {
                diagnostic = $"readback failed ({readEx?.GetType().Name}: {readEx?.Message})";
                return false;
            }

            if (!preWriteExisted)
            {
                if (positivelyAbsent)
                {
                    diagnostic = "confirmed absent";
                    return true;
                }

                diagnostic = "file unexpectedly appeared on disk";
                return false;
            }

            if (positivelyAbsent || currentBytes == null || preWriteBytes == null)
            {
                diagnostic = "pre-existing file is now missing";
                return false;
            }

            if (currentBytes.AsSpan().SequenceEqual(preWriteBytes))
            {
                diagnostic = "pre-write bytes verified unchanged";
                return true;
            }

            diagnostic = "on-disk bytes no longer match pre-write baseline";
            return false;
        }

        private static bool IsTempFileUnappliedWithExpectedBytes(string tempPath, byte[] expectedBytes)
        {
            if (!TryReadFileBytesOrConfirmAbsent(tempPath, out var tempBytes, out bool positivelyAbsent, out _) ||
                positivelyAbsent ||
                tempBytes == null)
            {
                return false;
            }

            return tempBytes.AsSpan().SequenceEqual(expectedBytes);
        }

        private static bool IsValidCurrentSchemaLibraryJson(byte[] bytes)
        {
            try
            {
                var assessment = GameLibrarySchemaCompatibility.AssessJson(bytes);
                if (!assessment.IsCompatible || assessment.Status != SchemaCompatibilityStatus.Compatible)
                    return false;

                using var ms = new MemoryStream(bytes);
                using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                var parsed = JsonSerializer.Deserialize<GameLibrary>(text, JsonOptions);
                return parsed != null && parsed.SchemaVersion <= GameLibrary.CurrentSchemaVersion;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadFileBytesOrConfirmAbsent(
            string path,
            out byte[]? bytes,
            out bool positivelyAbsent,
            out Exception? error)
        {
            bytes = null;
            positivelyAbsent = false;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                positivelyAbsent = true;
                return true;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                bytes = ms.ToArray();
                return true;
            }
            catch (FileNotFoundException)
            {
                positivelyAbsent = true;
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                positivelyAbsent = true;
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        private void TryCleanTempFile(string tempPath)
        {
            try
            {
                if (CustomTempCleaner != null)
                {
                    CustomTempCleaner(tempPath);
                }
                else if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Cleanup failure must not mask the primary operation.
            }
        }

        private void EnsureNotRefusedLocked()
        {
            if (!VerifyDiskMatchesAuthoritativeBaselineLocked(
                    out _,
                    out _,
                    out var error,
                    out var diagMessage,
                    out _))
            {
                throw error ?? new InvalidOperationException(diagMessage ?? "Cannot modify game library: on-disk library does not match the current store baseline.");
            }
        }

        private bool VerifyDiskMatchesAuthoritativeBaselineLocked(
            out byte[]? diskBytes,
            out bool preWriteExisted,
            out Exception? baselineError,
            out string? diagMessage,
            out bool isPreWriteUnreadableOrSchemaRefused)
        {
            diskBytes = null;
            preWriteExisted = false;
            baselineError = null;
            diagMessage = null;
            isPreWriteUnreadableOrSchemaRefused = false;

            if (_hasUncertainPersistence)
            {
                diagMessage = _uncertainPersistenceReason ?? "Cannot modify game library: ownership persistence is in an uncertain state for this session.";
                baselineError = new InvalidOperationException(diagMessage);
                return false;
            }

            if (_hasInaccessibleLibraryOnLoad)
            {
                diagMessage = _inaccessibleLibraryReason ?? "Cannot modify game library: existing library file could not be read on startup.";
                baselineError = new IOException(diagMessage);
                isPreWriteUnreadableOrSchemaRefused = true;
                return false;
            }

            if (_isRefused)
            {
                diagMessage = $"Cannot modify game library: file on disk declares schema {_refusedSchemaText}, which is newer than supported schema {GameLibrary.CurrentSchemaVersion}.";
                baselineError = new IncompatibleLibrarySchemaException(_refusedSchemaText, GameLibrary.CurrentSchemaVersion, diagMessage);
                isPreWriteUnreadableOrSchemaRefused = true;
                return false;
            }

            if (!TryReadFileBytesOrConfirmAbsent(_libraryFilePath, out diskBytes, out bool positivelyAbsent, out var readEx))
            {
                diagMessage = $"Cannot read game library file '{_libraryFilePath}' before write: {readEx?.Message}";
                baselineError = readEx ?? new IOException(diagMessage);
                isPreWriteUnreadableOrSchemaRefused = true;
                return false;
            }

            if (!positivelyAbsent && diskBytes != null)
            {
                preWriteExisted = true;
                var assessment = GameLibrarySchemaCompatibility.AssessJson(diskBytes);
                if (assessment.IsRefused)
                {
                    _isRefused = true;
                    _refusedSchemaText = assessment.FoundSchemaVersionText;
                    diagMessage = $"Cannot modify game library: file on disk declares schema {_refusedSchemaText}, which is newer than supported schema {GameLibrary.CurrentSchemaVersion}.";
                    baselineError = new IncompatibleLibrarySchemaException(_refusedSchemaText, GameLibrary.CurrentSchemaVersion, diagMessage);
                    isPreWriteUnreadableOrSchemaRefused = true;
                    return false;
                }
            }
            else
            {
                preWriteExisted = false;
            }

            if (!_lastCommittedExisted)
            {
                if (!positivelyAbsent)
                {
                    diagMessage = $"Cannot modify game library: '{Path.GetFileName(_libraryFilePath)}' appeared on disk unexpectedly after store initialization.";
                    baselineError = new InvalidOperationException(diagMessage);
                    ActivateUncertainPersistenceLocked(diagMessage);
                    return false;
                }

                return true;
            }

            if (positivelyAbsent || diskBytes == null)
            {
                diagMessage = $"Cannot modify game library: previously loaded or committed '{Path.GetFileName(_libraryFilePath)}' is missing on disk.";
                baselineError = new InvalidOperationException(diagMessage);
                ActivateUncertainPersistenceLocked(diagMessage);
                return false;
            }

            if (_lastCommittedLibraryBytes == null || !diskBytes.AsSpan().SequenceEqual(_lastCommittedLibraryBytes))
            {
                diagMessage = $"Cannot modify game library: on-disk '{Path.GetFileName(_libraryFilePath)}' was modified outside the current store baseline.";
                baselineError = new InvalidOperationException(diagMessage);
                ActivateUncertainPersistenceLocked(diagMessage);
                return false;
            }

            if (!_loadedFromBrokenFileRecovery && !IsValidCurrentSchemaLibraryJson(diskBytes))
            {
                diagMessage = $"Cannot modify game library: on-disk '{Path.GetFileName(_libraryFilePath)}' contains malformed or unparseable JSON.";
                baselineError = new InvalidOperationException(diagMessage);
                ActivateUncertainPersistenceLocked(diagMessage);
                return false;
            }

            return true;
        }

        private void Load()
        {
            if (!TryReadFileBytesOrConfirmAbsent(_libraryFilePath, out var rawBytes, out bool positivelyAbsent, out var readEx))
            {
                lock (_sync)
                {
                    _hasInaccessibleLibraryOnLoad = true;
                    _inaccessibleLibraryReason = $"Cannot modify game library: existing library file could not be read on startup ({readEx?.GetType().Name}: {readEx?.Message}).";
                }
                Log($"GameLibraryStore: existing library file could not be accessed on startup: {readEx?.GetType().Name} - {readEx?.Message}");
                return;
            }

            if (!positivelyAbsent && rawBytes != null)
            {
                var assessment = GameLibrarySchemaCompatibility.AssessJson(rawBytes);
                if (assessment.IsRefused)
                {
                    lock (_sync)
                    {
                        _isRefused = true;
                        _refusedSchemaText = assessment.FoundSchemaVersionText;
                    }
                    // A refused library remains byte-for-byte unchanged.
                    // No temporary file, recovery copy, backup creation/rotation or directory creation on that refusal path.
                    return;
                }

                Paths.EnsureDirectories();
                Directory.CreateDirectory(_backupsDirectoryPath);

                var result = TryLoadCurrentFormat();
                if (result == LoadResult.Success)
                {
                    lock (_sync)
                    {
                        _lastCommittedExisted = true;
                        _lastCommittedLibraryBytes = rawBytes;
                    }
                    return;
                }

                if (result == LoadResult.FutureSchema)
                    return;

                // A current-format file exists but is unreadable. Never silently
                // fall back to stale legacy data. Preserve it first.
                lock (_sync)
                {
                    _loadedFromBrokenFileRecovery = true;
                    _lastCommittedExisted = true;
                    _lastCommittedLibraryBytes = rawBytes;
                }
                TryPreserveBrokenCurrentFile();
                Log($"GameLibraryStore: current library could not be loaded. Preserved a recovery copy.");
                return;
            }

            Paths.EnsureDirectories();
            Directory.CreateDirectory(_backupsDirectoryPath);
        }

        private LoadResult TryLoadCurrentFormat()
        {
            try
            {
                var json = File.ReadAllText(_libraryFilePath);
                var library = JsonSerializer.Deserialize<GameLibrary>(json, JsonOptions);
                if (library == null)
                    return LoadResult.Invalid;

                if (library.SchemaVersion > GameLibrary.CurrentSchemaVersion)
                {
                    lock (_sync)
                    {
                        _isRefused = true;
                        _refusedSchemaText = library.SchemaVersion.ToString();
                    }
                    Log($"GameLibraryStore: library schema {library.SchemaVersion} is newer than supported schema {GameLibrary.CurrentSchemaVersion}. No migration was attempted.");
                    return LoadResult.FutureSchema;
                }

                lock (_sync)
                {
                    _installations.Clear();
                    foreach (var installation in library.Installations ?? new List<GameInstallation>())
                    {
                        if (string.IsNullOrWhiteSpace(installation.InstallationPath))
                            continue;

                        var normalized = GameInstallation.NormalizeInstallationPath(installation.InstallationPath);
                        installation.InstallationPath = normalized;
                        installation.ManagementPolicy ??= ManagementPolicy.UseGlobal();
                        installation.Configuration ??= new DxvkConfiguration();
                        installation.Executables ??= new List<ExecutableProfile>();
                        installation.ManagedFiles ??= new List<ManagedFileRecord>();
                        _installations[normalized] = installation;
                    }
                }

                return LoadResult.Success;
            }
            catch (Exception ex)
            {
                Log($"GameLibraryStore: failed to load current library: {ex.GetType().Name} - {ex.Message}");
                return LoadResult.Invalid;
            }
        }

        private void TryPreserveBrokenCurrentFile()
        {
            try
            {
                if (!File.Exists(_libraryFilePath))
                    return;

                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
                var recoveryPath = _libraryFilePath + RecoverySuffix + "." + stamp + ".json";
                File.Copy(_libraryFilePath, recoveryPath, false);
                Log($"GameLibraryStore: preserved unreadable library as {Path.GetFileName(recoveryPath)}.");
            }
            catch (Exception ex)
            {
                Log($"GameLibraryStore: could not preserve unreadable library: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private static void Log(string message)
        {
            try { Utils.Logger.Log(message); } catch { }
        }

        private enum LoadResult
        {
            Invalid = 0,
            Success = 1,
            FutureSchema = 2
        }
    }
}
