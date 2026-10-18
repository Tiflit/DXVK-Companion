using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DXVKCompanion.Models;

namespace DXVKCompanion.Storage
{
    internal enum OwnershipPersistenceOutcome
    {
        Committed = 0,
        ProvenNotCommitted = 1,
        Unknown = 2
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

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private const string RecoverySuffix = ".recovery";

        private readonly string _libraryFilePath;
        private readonly string _backupsDirectoryPath;

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

                _installations[normalized] = installation;
                try
                {
                    WriteAllLocked();
                    return installation;
                }
                catch
                {
                    _installations.Remove(normalized);
                    throw;
                }
            }
        }

        public GameInstallation RecordDetectionSnapshot(DetectionSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            lock (_sync)
            {
                EnsureNotRefusedLocked();

                var installation = GetOrCreateInstallation(snapshot.InstallationRoot, Path.GetFileName(snapshot.InstallationRoot));
                var profile = installation.GetOrAddExecutable(snapshot.ExecutableRelativePath, snapshot.ProcessName);

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

                installation.LastSeenUtc = snapshot.TimestampUtc;
                WriteAllLocked();
                return installation;
            }
        }

        public void Save(GameInstallation installation)
        {
            if (installation == null) throw new ArgumentNullException(nameof(installation));

            lock (_sync)
            {
                EnsureNotRefusedLocked();

                var normalizedPath = GameInstallation.NormalizeInstallationPath(installation.InstallationPath);
                bool hadPrevious = _installations.TryGetValue(normalizedPath, out var previousEntry);
                var previousId = installation.Id;

                installation.InstallationPath = normalizedPath;

                if (string.IsNullOrWhiteSpace(installation.Id))
                    installation.Id = Guid.NewGuid().ToString("N");

                _installations[normalizedPath] = installation;
                try
                {
                    WriteAllLocked();
                }
                catch
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
                    throw;
                }
            }
        }

        public void SaveAll()
        {
            lock (_sync)
            {
                EnsureNotRefusedLocked();
                WriteAllLocked();
            }
        }

        internal bool TryCheckCanModifyLibrary(out string? refusalReason)
        {
            lock (_sync)
            {
                if (_hasUncertainPersistence)
                {
                    refusalReason = _uncertainPersistenceReason
                        ?? "Refused operation: game library persistence is in an uncertain state for this session.";
                    return false;
                }

                if (_hasInaccessibleLibraryOnLoad)
                {
                    refusalReason = _inaccessibleLibraryReason
                        ?? "Refused operation: existing game library file could not be read on startup.";
                    return false;
                }

                if (_isRefused)
                {
                    refusalReason = $"Refused operation: game library on disk declares newer schema {_refusedSchemaText} (supported: {GameLibrary.CurrentSchemaVersion}).";
                    return false;
                }

                if (!TryReadFileBytesOrConfirmAbsent(_libraryFilePath, out var diskBytes, out bool positivelyAbsent, out var readEx))
                {
                    refusalReason = $"Refused operation: game library file is unreadable or locked ({readEx?.GetType().Name}: {readEx?.Message}).";
                    return false;
                }

                if (!positivelyAbsent && diskBytes != null)
                {
                    var assessment = GameLibrarySchemaCompatibility.AssessJson(diskBytes);
                    if (assessment.IsRefused)
                    {
                        _isRefused = true;
                        _refusedSchemaText = assessment.FoundSchemaVersionText;
                        refusalReason = $"Refused operation: game library on disk declares newer schema {_refusedSchemaText} (supported: {GameLibrary.CurrentSchemaVersion}).";
                        return false;
                    }

                    if (!_loadedFromBrokenFileRecovery)
                    {
                        if (assessment.Status == SchemaCompatibilityStatus.Malformed || !IsValidCurrentSchemaLibraryJson(diskBytes))
                        {
                            refusalReason = "Refused operation: game library file on disk is malformed or unreadable.";
                            return false;
                        }
                    }
                }

                refusalReason = null;
                return true;
            }
        }

        internal void CaptureCurrentLibraryBaseline(out byte[]? baselineBytes, out bool baselineExisted)
        {
            lock (_sync)
            {
                if (TryReadFileBytesOrConfirmAbsent(_libraryFilePath, out var bytes, out bool positivelyAbsent, out _) &&
                    !positivelyAbsent &&
                    bytes != null)
                {
                    baselineBytes = bytes;
                    baselineExisted = true;
                    return;
                }

                baselineBytes = null;
                baselineExisted = false;
            }
        }

        internal OwnershipSaveResult TrySaveWithOutcome(GameInstallation installation)
        {
            if (installation == null) throw new ArgumentNullException(nameof(installation));

            lock (_sync)
            {
                if (_hasUncertainPersistence)
                {
                    return new OwnershipSaveResult(
                        OwnershipPersistenceOutcome.Unknown,
                        DiagnosticMessage: _uncertainPersistenceReason ?? "Ownership persistence is already in an uncertain state for this session.");
                }

                if (_hasInaccessibleLibraryOnLoad)
                {
                    return new OwnershipSaveResult(
                        OwnershipPersistenceOutcome.ProvenNotCommitted,
                        DiagnosticMessage: _inaccessibleLibraryReason ?? "Existing game library file could not be read on startup.");
                }

                if (_isRefused)
                {
                    var schemaMsg = $"Cannot modify game library: file on disk declares schema {_refusedSchemaText}, which is newer than supported schema {GameLibrary.CurrentSchemaVersion}.";
                    return new OwnershipSaveResult(
                        OwnershipPersistenceOutcome.ProvenNotCommitted,
                        Exception: new IncompatibleLibrarySchemaException(_refusedSchemaText, GameLibrary.CurrentSchemaVersion, schemaMsg),
                        DiagnosticMessage: schemaMsg);
                }

                if (!TryReadFileBytesOrConfirmAbsent(_libraryFilePath, out var preWriteBytes, out bool preWriteAbsent, out var preReadEx))
                {
                    return new OwnershipSaveResult(
                        OwnershipPersistenceOutcome.ProvenNotCommitted,
                        Exception: preReadEx,
                        DiagnosticMessage: $"Could not read '{Path.GetFileName(_libraryFilePath)}' before staging ownership metadata ({preReadEx?.GetType().Name}: {preReadEx?.Message}).",
                        PreWriteBytes: _lastCommittedLibraryBytes,
                        PreWriteExisted: _lastCommittedExisted);
                }

                bool preWriteExisted = !preWriteAbsent && preWriteBytes != null;
                if (preWriteExisted && preWriteBytes != null)
                {
                    var preAssessment = GameLibrarySchemaCompatibility.AssessJson(preWriteBytes);
                    if (preAssessment.IsRefused)
                    {
                        _isRefused = true;
                        _refusedSchemaText = preAssessment.FoundSchemaVersionText;
                        var schemaMsg = $"Cannot modify game library: file on disk declares schema {_refusedSchemaText}, which is newer than supported schema {GameLibrary.CurrentSchemaVersion}.";
                        return new OwnershipSaveResult(
                            OwnershipPersistenceOutcome.ProvenNotCommitted,
                            Exception: new IncompatibleLibrarySchemaException(_refusedSchemaText, GameLibrary.CurrentSchemaVersion, schemaMsg),
                            DiagnosticMessage: schemaMsg,
                            PreWriteBytes: preWriteBytes,
                            PreWriteExisted: true);
                    }
                }

                var normalizedPath = GameInstallation.NormalizeInstallationPath(installation.InstallationPath);
                installation.InstallationPath = normalizedPath;
                if (string.IsNullOrWhiteSpace(installation.Id))
                    installation.Id = Guid.NewGuid().ToString("N");

                _installations[normalizedPath] = installation;

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
                byte[] expectedPostWriteBytes = Encoding.UTF8.GetBytes(json);
                var tempPath = _libraryFilePath + ".tmp." + Guid.NewGuid().ToString("N");

                try
                {
                    if (CustomTempWriter != null)
                    {
                        CustomTempWriter(tempPath, json);
                    }
                    else
                    {
                        File.WriteAllText(tempPath, json);
                    }
                }
                catch (Exception tempEx)
                {
                    TryCleanTempFile(tempPath);

                    if (TryVerifyPreWriteIntact(preWriteExisted, preWriteBytes, out var verifyDiag))
                    {
                        return new OwnershipSaveResult(
                            OwnershipPersistenceOutcome.ProvenNotCommitted,
                            Exception: tempEx,
                            DiagnosticMessage: $"Ownership metadata staging failed before replacement ({tempEx.GetType().Name}: {tempEx.Message}). Pre-operation library state verified intact.",
                            PreWriteBytes: preWriteBytes,
                            PreWriteExisted: preWriteExisted);
                    }

                    ActivateUncertainPersistenceLocked(
                        $"Ownership metadata staging failed ({tempEx.GetType().Name}: {tempEx.Message}) and pre-operation library state could not be verified ({verifyDiag}).");
                    return new OwnershipSaveResult(
                        OwnershipPersistenceOutcome.Unknown,
                        Exception: tempEx,
                        DiagnosticMessage: _uncertainPersistenceReason,
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
                    return new OwnershipSaveResult(
                        OwnershipPersistenceOutcome.Unknown,
                        Exception: replaceEx,
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
                return new OwnershipSaveResult(
                    OwnershipPersistenceOutcome.Unknown,
                    Exception: replaceEx,
                    DiagnosticMessage: mismatchReason,
                    PreWriteBytes: preWriteBytes,
                    PreWriteExisted: preWriteExisted);
            }
        }

        internal bool TrySaveHealthAfterFailedCompensation(
            GameInstallation installation,
            byte[]? expectedPreWriteBytes,
            bool preWriteExisted)
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
                    return false;
                }

                var healthResult = TrySaveWithOutcome(installation);
                return healthResult.Outcome == OwnershipPersistenceOutcome.Committed;
            }
        }

        internal GameInstallation CreateSnapshot(GameInstallation source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            string json = JsonSerializer.Serialize(source, _jsonOptions);
            return JsonSerializer.Deserialize<GameInstallation>(json, _jsonOptions)
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
            if (_hasUncertainPersistence)
            {
                throw new InvalidOperationException(
                    _uncertainPersistenceReason ?? "Cannot modify game library: ownership persistence is in an uncertain state for this session.");
            }

            if (_hasInaccessibleLibraryOnLoad)
            {
                throw new IOException(
                    _inaccessibleLibraryReason ?? "Cannot modify game library: existing library file could not be read on startup.");
            }

            if (_isRefused)
            {
                throw new IncompatibleLibrarySchemaException(
                    _refusedSchemaText,
                    GameLibrary.CurrentSchemaVersion,
                    $"Cannot modify game library: file on disk declares schema {_refusedSchemaText}, which is newer than supported schema {GameLibrary.CurrentSchemaVersion}.");
            }

            if (!TryReadFileBytesOrConfirmAbsent(_libraryFilePath, out var diskBytes, out bool positivelyAbsent, out var readEx))
            {
                throw new IOException($"Cannot read game library file '{_libraryFilePath}' before write: {readEx?.Message}", readEx);
            }

            if (!positivelyAbsent && diskBytes != null)
            {
                var assessment = GameLibrarySchemaCompatibility.AssessJson(diskBytes);
                if (assessment.IsRefused)
                {
                    _isRefused = true;
                    _refusedSchemaText = assessment.FoundSchemaVersionText;
                    throw new IncompatibleLibrarySchemaException(
                        _refusedSchemaText,
                        GameLibrary.CurrentSchemaVersion,
                        $"Cannot modify game library: file on disk declares schema {_refusedSchemaText}, which is newer than supported schema {GameLibrary.CurrentSchemaVersion}.");
                }
            }
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

        private void WriteAllLocked()
        {
            EnsureNotRefusedLocked();

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
            byte[] expectedBytes = Encoding.UTF8.GetBytes(json);
            var tempPath = _libraryFilePath + ".tmp." + Guid.NewGuid().ToString("N");
            bool committed = false;

            try
            {
                if (CustomTempWriter != null)
                {
                    CustomTempWriter(tempPath, json);
                }
                else
                {
                    File.WriteAllText(tempPath, json);
                }

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

                committed = true;
                _lastCommittedExisted = true;
                _lastCommittedLibraryBytes = expectedBytes;
                _loadedFromBrokenFileRecovery = false;
            }
            finally
            {
                if (!committed)
                {
                    TryCleanTempFile(tempPath);
                }
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
