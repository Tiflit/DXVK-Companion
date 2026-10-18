using System.Text.Json;
using DXVKCompanion.Storage;

namespace DXVKCompanion.Safety;

public sealed record MultiFileTransactionRequest
{
    public required string InstallationRoot { get; init; }
    public required TransactionOperation Operation { get; init; }
    public required IReadOnlyList<MultiFileTransactionFile> Files { get; init; }
}

public sealed record MultiFileTransactionFile
{
    public required string RelativePath { get; init; }
    public string? SourceFilePath { get; init; }
    public SafetyFileIdentity? ExpectedTargetIdentity { get; init; }
    public SafetyFileIdentity? ExpectedSourceIdentity { get; init; }
    public OriginalFileState OriginalState { get; init; } = OriginalFileState.Unknown;
    public string? BackupRelativePath { get; init; }
    public string? ExpectedOriginalSha256 { get; init; }
    public string? ExpectedManagedSha256 { get; init; }
    internal bool ReplaceExistingBackup { get; init; }
    internal SafetyFileIdentity? ExpectedPriorBackupIdentity { get; init; }
    internal bool UpdateOriginalSha256OnCommit { get; init; }
}

public sealed record MultiFileTransactionTestHooks
{
    public Action<string, int>? AfterApply { get; init; }
    public Action<string>? BeforeRecovery { get; init; }
    public Action<int>? DuringRecovery { get; init; }
    internal Action<string, string, int>? AfterStageNativeBackup { get; init; }
    internal Action<string, string, int>? BeforePublishNativeBackup { get; init; }
    internal Action<string, string, int>? AfterPublishNativeBackup { get; init; }
}

public sealed class MultiFileTransactionEngine
{
    private readonly string _transactionStoreRoot;
    private readonly MultiFileTransactionTestHooks _hooks;

    public MultiFileTransactionEngine(string transactionStoreRoot, MultiFileTransactionTestHooks? hooks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionStoreRoot);
        _transactionStoreRoot = Path.GetFullPath(transactionStoreRoot);
        Directory.CreateDirectory(_transactionStoreRoot);
        _hooks = hooks ?? new MultiFileTransactionTestHooks();
    }

    public string TransactionStoreRoot => _transactionStoreRoot;

    public SafetyTransactionResult Execute(MultiFileTransactionRequest request) =>
        ExecuteWithCommitScope(request, commitOwnershipCallback: null);

    internal SafetyTransactionResult ExecuteWithCommitScope(
        MultiFileTransactionRequest request,
        Func<IReadOnlyList<MultiFileTransactionFile>, OwnershipSaveResult>? commitOwnershipCallback)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transactionId = Guid.NewGuid().ToString("N");
        var affected = request.Files.Select(f => f.RelativePath).ToArray();
        var validated = new List<ValidatedFile>(request.Files.Count);
        var prepared = new List<PreparedFile>(request.Files.Count);
        var createdNativeBackups = new List<string>();
        var replacedPriorBackups = new List<ReplacedBackupRecord>();
        var transactionsRoot = ResolveInsideRoot(_transactionStoreRoot, ".transactions");
        var operationRoot = ResolveInsideRoot(transactionsRoot, transactionId);
        var planPath = ResolveInsideRoot(operationRoot, "plan.json");
        var undoRoot = ResolveInsideRoot(operationRoot, "undo");
        var nativeBackupStagingRoot = ResolveInsideRoot(operationRoot, "native-backups");
        var priorBackupsStagingRoot = ResolveInsideRoot(operationRoot, "prior-backups");
        var anyMutationAttempted = false;
        var lastAttemptedIndex = -1;

        try
        {
            ValidateRequest(request);

            // Phase 1: Validate all targets, sources, and restore/replacement baselines before writing any backup or undo artifact.
            foreach (var file in request.Files)
            {
                var targetPath = ResolveInsideRoot(request.InstallationRoot, file.RelativePath);
                var currentState = File.Exists(targetPath) ? OriginalFileState.Existing : OriginalFileState.DidNotExist;
                var currentIdentity = currentState == OriginalFileState.Existing ? FileIdentity.Capture(targetPath) : null;

                ValidateTargetExpectation(request.Operation, file, currentState, currentIdentity);

                string? sourcePath = null;
                SafetyFileIdentity? sourceIdentity = null;
                if (request.Operation is TransactionOperation.Install or TransactionOperation.Update or TransactionOperation.Reapply)
                {
                    sourcePath = Path.GetFullPath(file.SourceFilePath!);
                    if (!File.Exists(sourcePath))
                    {
                        return Abort(transactionId, request.Operation, affected, $"The source file does not exist: {file.SourceFilePath}");
                    }

                    sourceIdentity = FileIdentity.Capture(sourcePath);
                    if (file.ExpectedSourceIdentity is not null && sourceIdentity != file.ExpectedSourceIdentity)
                    {
                        return Abort(transactionId, request.Operation, affected, $"The source file changed before apply: {file.RelativePath}");
                    }
                }

                string? backupPath = null;
                string? stagedNativeBackupPath = null;
                string? priorBackupCopyPath = null;
                SafetyFileIdentity? backupIdentity = null;
                if ((request.Operation is TransactionOperation.Install or TransactionOperation.Update or TransactionOperation.Reapply)
                    && file.OriginalState == OriginalFileState.Existing)
                {
                    backupPath = ResolveBackupPath(file.BackupRelativePath, transactionId, file.RelativePath);
                    stagedNativeBackupPath = ResolveInsideRoot(nativeBackupStagingRoot, file.RelativePath);

                    if (file.ReplaceExistingBackup)
                    {
                        if (file.ExpectedPriorBackupIdentity is null || !file.ExpectedPriorBackupIdentity.IsValid)
                        {
                            return SafeFailure(transactionId, request.Operation, affected, $"Prior canonical backup identity is missing or invalid for baseline replacement: {file.RelativePath}");
                        }

                        if (!File.Exists(backupPath))
                        {
                            return SafeFailure(transactionId, request.Operation, affected, $"Prior canonical backup is unavailable for baseline replacement: {file.RelativePath}");
                        }

                        var currentPriorBackupIdentity = FileIdentity.Capture(backupPath);
                        if (currentPriorBackupIdentity != file.ExpectedPriorBackupIdentity)
                        {
                            return SafeFailure(transactionId, request.Operation, affected, $"Prior canonical backup identity mismatch before baseline replacement: {file.RelativePath}");
                        }

                        priorBackupCopyPath = ResolveInsideRoot(priorBackupsStagingRoot, file.RelativePath);
                    }
                }
                else if (request.Operation == TransactionOperation.Restore)
                {
                    if (!IsValidSha256(file.ExpectedManagedSha256))
                        return SafeFailure(transactionId, request.Operation, affected, $"Managed hash baseline is missing or malformed for restore: {file.RelativePath}");

                    if (currentState != OriginalFileState.Existing || currentIdentity is null)
                        return SafeFailure(transactionId, request.Operation, affected, $"Managed target file is missing for restore: {file.RelativePath}");

                    if (!string.Equals(currentIdentity.Sha256, file.ExpectedManagedSha256, StringComparison.OrdinalIgnoreCase))
                        return SafeFailure(transactionId, request.Operation, affected, $"Managed target hash mismatch for restore: {file.RelativePath} (expected managed {file.ExpectedManagedSha256}, actual {currentIdentity.Sha256})");

                    if (file.OriginalState == OriginalFileState.Existing)
                    {
                        if (!IsValidSha256(file.ExpectedOriginalSha256))
                            return SafeFailure(transactionId, request.Operation, affected, $"Original hash baseline is missing or malformed for restore: {file.RelativePath}");

                        backupPath = ResolveBackupPath(file.BackupRelativePath, transactionId, file.RelativePath);
                        if (!File.Exists(backupPath))
                            return SafeFailure(transactionId, request.Operation, affected, $"Original backup is unavailable for restore: {file.RelativePath}");

                        backupIdentity = FileIdentity.Capture(backupPath);
                        if (!string.Equals(backupIdentity.Sha256, file.ExpectedOriginalSha256, StringComparison.OrdinalIgnoreCase))
                            return SafeFailure(transactionId, request.Operation, affected, $"Original backup hash mismatch for restore: {file.RelativePath} (expected {file.ExpectedOriginalSha256}, actual {backupIdentity.Sha256})");
                    }
                }

                var undoPath = currentState == OriginalFileState.Existing
                    ? ResolveInsideRoot(undoRoot, file.RelativePath)
                    : null;

                validated.Add(new ValidatedFile(
                    file,
                    targetPath,
                    sourcePath,
                    sourceIdentity,
                    currentState,
                    currentIdentity,
                    undoPath,
                    stagedNativeBackupPath,
                    priorBackupCopyPath,
                    backupPath,
                    backupIdentity));
            }

            // Phase 2: Capture per-operation undo copies in the isolated operation namespace and stage/publish native backups.
            Directory.CreateDirectory(operationRoot);

            for (var index = 0; index < validated.Count; index++)
            {
                var item = validated[index];
                string? undoPath = item.UndoPath;
                if (item.PreOperationState == OriginalFileState.Existing)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(undoPath!)!);
                    File.Copy(item.TargetPath, undoPath!, overwrite: true);
                    var undoIdentity = FileIdentity.Capture(undoPath!);
                    if (undoIdentity != item.PreOperationIdentity)
                    {
                        return FailPhase2WithBackupCompensation(
                            transactionId,
                            request.Operation,
                            affected,
                            operationRoot,
                            transactionsRoot,
                            createdNativeBackups,
                            replacedPriorBackups,
                            $"The pre-operation undo copy could not be verified: {item.File.RelativePath}");
                    }
                }

                var backupIdentity = item.BackupIdentity;
                if ((request.Operation is TransactionOperation.Install or TransactionOperation.Update or TransactionOperation.Reapply)
                    && item.File.OriginalState == OriginalFileState.Existing)
                {
                    var backupPath = item.BackupPath!;
                    if (item.File.ReplaceExistingBackup)
                    {
                        var expectedPriorIdentity = item.File.ExpectedPriorBackupIdentity!;
                        if (!File.Exists(backupPath) || FileIdentity.Capture(backupPath) != expectedPriorIdentity)
                        {
                            return FailPhase2WithBackupCompensation(
                                transactionId,
                                request.Operation,
                                affected,
                                operationRoot,
                                transactionsRoot,
                                createdNativeBackups,
                                replacedPriorBackups,
                                $"The prior canonical backup changed before staging replacement: {item.File.RelativePath}");
                        }

                        var priorCopyPath = item.PriorBackupCopyPath!;
                        Directory.CreateDirectory(Path.GetDirectoryName(priorCopyPath)!);
                        File.Copy(backupPath, priorCopyPath, overwrite: true);
                        var priorCopyIdentity = FileIdentity.Capture(priorCopyPath);
                        if (priorCopyIdentity != expectedPriorIdentity)
                        {
                            return FailPhase2WithBackupCompensation(
                                transactionId,
                                request.Operation,
                                affected,
                                operationRoot,
                                transactionsRoot,
                                createdNativeBackups,
                                replacedPriorBackups,
                                $"The prior canonical backup undo copy could not be verified: {item.File.RelativePath}");
                        }

                        var stagedBackupPath = item.StagedNativeBackupPath!;
                        Directory.CreateDirectory(Path.GetDirectoryName(stagedBackupPath)!);
                        File.Copy(item.TargetPath, stagedBackupPath, overwrite: true);
                        _hooks.AfterStageNativeBackup?.Invoke(stagedBackupPath, backupPath, index);
                        var stagedBackupIdentity = FileIdentity.Capture(stagedBackupPath);
                        if (stagedBackupIdentity != item.PreOperationIdentity)
                        {
                            return FailPhase2WithBackupCompensation(
                                transactionId,
                                request.Operation,
                                affected,
                                operationRoot,
                                transactionsRoot,
                                createdNativeBackups,
                                replacedPriorBackups,
                                $"The replacement backup could not be verified: {item.File.RelativePath}");
                        }

                        _hooks.BeforePublishNativeBackup?.Invoke(stagedBackupPath, backupPath, index);
                        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                        replacedPriorBackups.Add(new ReplacedBackupRecord(
                            item.File.RelativePath,
                            backupPath,
                            priorCopyPath,
                            expectedPriorIdentity));
                        File.Move(stagedBackupPath, backupPath, overwrite: true);
                        _hooks.AfterPublishNativeBackup?.Invoke(stagedBackupPath, backupPath, index);

                        backupIdentity = FileIdentity.Capture(backupPath);
                        if (backupIdentity != item.PreOperationIdentity)
                        {
                            return FailPhase2WithBackupCompensation(
                                transactionId,
                                request.Operation,
                                affected,
                                operationRoot,
                                transactionsRoot,
                                createdNativeBackups,
                                replacedPriorBackups,
                                $"The replacement backup could not be verified: {item.File.RelativePath}");
                        }
                    }
                    else if (!File.Exists(backupPath))
                    {
                        var stagedBackupPath = item.StagedNativeBackupPath!;
                        Directory.CreateDirectory(Path.GetDirectoryName(stagedBackupPath)!);
                        File.Copy(item.TargetPath, stagedBackupPath, overwrite: true);
                        _hooks.AfterStageNativeBackup?.Invoke(stagedBackupPath, backupPath, index);
                        var stagedBackupIdentity = FileIdentity.Capture(stagedBackupPath);
                        if (stagedBackupIdentity != item.PreOperationIdentity)
                        {
                            return FailPhase2WithBackupCompensation(
                                transactionId,
                                request.Operation,
                                affected,
                                operationRoot,
                                transactionsRoot,
                                createdNativeBackups,
                                replacedPriorBackups,
                                $"The backup could not be verified: {item.File.RelativePath}");
                        }

                        _hooks.BeforePublishNativeBackup?.Invoke(stagedBackupPath, backupPath, index);
                        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                        try
                        {
                            File.Move(stagedBackupPath, backupPath, overwrite: false);
                        }
                        catch
                        {
                            PruneEmptyParentDirectories(Path.GetDirectoryName(backupPath), _transactionStoreRoot);
                            throw;
                        }

                        createdNativeBackups.Add(backupPath);
                        _hooks.AfterPublishNativeBackup?.Invoke(stagedBackupPath, backupPath, index);
                        backupIdentity = FileIdentity.Capture(backupPath);
                        if (backupIdentity != item.PreOperationIdentity)
                        {
                            return FailPhase2WithBackupCompensation(
                                transactionId,
                                request.Operation,
                                affected,
                                operationRoot,
                                transactionsRoot,
                                createdNativeBackups,
                                replacedPriorBackups,
                                $"The backup could not be verified: {item.File.RelativePath}");
                        }
                    }
                    else
                    {
                        backupIdentity = FileIdentity.Capture(backupPath);
                    }
                }

                prepared.Add(new PreparedFile(
                    item.File,
                    item.TargetPath,
                    item.SourcePath,
                    item.SourceIdentity,
                    item.PreOperationState,
                    item.PreOperationIdentity,
                    undoPath,
                    item.BackupPath,
                    backupIdentity));
            }

            var plan = new SafetyTransactionPlan
            {
                TransactionId = transactionId,
                Operation = request.Operation,
                InstallationRoot = Path.GetFullPath(request.InstallationRoot),
                Files = prepared.Select(p => new SafetyFilePlan
                {
                    RelativePath = p.File.RelativePath,
                    SourceRelativePath = p.SourcePath ?? string.Empty,
                    ExpectedTargetIdentity = p.File.ExpectedTargetIdentity,
                    ExpectedSourceIdentity = p.File.ExpectedSourceIdentity
                }).ToArray(),
                State = TransactionState.Prepared
            };
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan));

            for (var index = 0; index < prepared.Count; index++)
            {
                var item = prepared[index];
                anyMutationAttempted = true;
                lastAttemptedIndex = index;
                Apply(item, request.Operation);
                _hooks.AfterApply?.Invoke(item.TargetPath, index);
            }

            foreach (var item in prepared)
                Verify(item, request.Operation);

            if (commitOwnershipCallback is not null)
            {
                OwnershipSaveResult saveResult;
                try
                {
                    saveResult = commitOwnershipCallback(request.Files);
                }
                catch (Exception callbackEx)
                {
                    saveResult = new OwnershipSaveResult(
                        OwnershipPersistenceOutcome.Unknown,
                        Exception: callbackEx,
                        DiagnosticMessage: $"Unexpected exception in ownership commit callback ({callbackEx.GetType().Name}: {callbackEx.Message}).");
                }

                if (saveResult.Outcome == OwnershipPersistenceOutcome.Unknown)
                {
                    return new SafetyTransactionResult
                    {
                        TransactionId = transactionId,
                        Operation = request.Operation,
                        State = TransactionState.AttentionRequired,
                        Outcome = TransactionOutcome.UnresolvedRecovery,
                        Message = saveResult.DiagnosticMessage
                            ?? saveResult.Exception?.Message
                            ?? "Ownership metadata commit outcome could not be verified.",
                        AffectedFiles = affected
                    };
                }

                if (saveResult.Outcome == OwnershipPersistenceOutcome.ProvenNotCommitted)
                {
                    throw new IOException(
                        saveResult.DiagnosticMessage
                            ?? saveResult.Exception?.Message
                            ?? "Ownership metadata commit did not complete.",
                        saveResult.Exception);
                }
            }

            CleanupOperationArtifacts(operationRoot, transactionsRoot);
            return new SafetyTransactionResult
            {
                TransactionId = transactionId,
                Operation = request.Operation,
                State = TransactionState.Committed,
                Outcome = TransactionOutcome.Success,
                AffectedFiles = affected
            };
        }
        catch (Exception ex)
        {
            if (!anyMutationAttempted)
            {
                try
                {
                    CompensateBackups(createdNativeBackups, replacedPriorBackups);
                    CleanupOperationArtifacts(operationRoot, transactionsRoot);
                    return replacedPriorBackups.Count > 0
                        ? SafeFailure(transactionId, request.Operation, affected, ex.Message)
                        : Abort(transactionId, request.Operation, affected, ex.Message);
                }
                catch (Exception backupRecoveryEx)
                {
                    return new SafetyTransactionResult
                    {
                        TransactionId = transactionId,
                        Operation = request.Operation,
                        State = TransactionState.AttentionRequired,
                        Outcome = TransactionOutcome.UnresolvedRecovery,
                        Message = $"{ex.Message} Backup compensation failed: {backupRecoveryEx.Message}",
                        AffectedFiles = affected
                    };
                }
            }

            try
            {
                _hooks.BeforeRecovery?.Invoke(Path.GetFullPath(request.InstallationRoot));
                RecoverAttempted(prepared, lastAttemptedIndex);
                VerifyPreOperationState(prepared);
                CompensateBackups(createdNativeBackups, replacedPriorBackups);
                CleanupOperationArtifacts(operationRoot, transactionsRoot);
                return new SafetyTransactionResult
                {
                    TransactionId = transactionId,
                    Operation = request.Operation,
                    State = TransactionState.FailedSafely,
                    Outcome = TransactionOutcome.SafeFailure,
                    Message = ex.Message,
                    AffectedFiles = affected
                };
            }
            catch (Exception recoveryEx)
            {
                return new SafetyTransactionResult
                {
                    TransactionId = transactionId,
                    Operation = request.Operation,
                    State = TransactionState.AttentionRequired,
                    Outcome = TransactionOutcome.UnresolvedRecovery,
                    Message = $"{ex.Message} Recovery failed: {recoveryEx.Message}",
                    AffectedFiles = affected
                };
            }
        }
    }

    private void ValidateRequest(MultiFileTransactionRequest request)
    {
        if (request.Operation == TransactionOperation.None)
            throw new InvalidOperationException("A transaction operation is required.");
        if (request.Files.Count == 0)
            throw new InvalidOperationException("At least one file is required.");

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in request.Files)
        {
            if (!seen.Add(file.RelativePath))
                throw new InvalidOperationException($"The transaction contains duplicate paths: {file.RelativePath}");

            _ = ResolveInsideRoot(request.InstallationRoot, file.RelativePath);

            if (request.Operation is TransactionOperation.Install or TransactionOperation.Update or TransactionOperation.Reapply)
            {
                if (string.IsNullOrWhiteSpace(file.SourceFilePath))
                    throw new InvalidOperationException($"A source file is required: {file.RelativePath}");
            }

            if (request.Operation == TransactionOperation.Restore && file.OriginalState == OriginalFileState.Unknown)
                throw new InvalidOperationException($"Original state is required for restore: {file.RelativePath}");
        }
    }

    private static void ValidateTargetExpectation(TransactionOperation operation, MultiFileTransactionFile file, OriginalFileState currentState, SafetyFileIdentity? currentIdentity)
    {
        var matches = file.ExpectedTargetIdentity is null
            ? currentState == OriginalFileState.DidNotExist
            : currentState == OriginalFileState.Existing && currentIdentity == file.ExpectedTargetIdentity;

        if (operation != TransactionOperation.Restore)
        {
            if (!matches)
                throw new InvalidOperationException($"The target changed during validation: {file.RelativePath}");
            return;
        }

        if (file.ExpectedTargetIdentity is not null && !matches)
            throw new InvalidOperationException($"The managed target changed before restore: {file.RelativePath}");
    }

    private static void Apply(PreparedFile item, TransactionOperation operation)
    {
        if (operation == TransactionOperation.Restore)
        {
            if (item.File.OriginalState == OriginalFileState.Existing)
            {
                File.Copy(item.BackupPath!, item.TargetPath, overwrite: true);
            }
            else if (item.File.OriginalState == OriginalFileState.DidNotExist && File.Exists(item.TargetPath))
            {
                File.Delete(item.TargetPath);
            }
            else if (item.File.OriginalState == OriginalFileState.Unknown)
            {
                throw new IOException($"Original state is unknown: {item.File.RelativePath}");
            }
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
        File.Copy(item.SourcePath!, item.TargetPath, overwrite: true);
    }

    private static void Verify(PreparedFile item, TransactionOperation operation)
    {
        if (operation == TransactionOperation.Restore)
        {
            if (item.File.OriginalState == OriginalFileState.DidNotExist)
            {
                if (File.Exists(item.TargetPath))
                    throw new IOException($"Restore verification failed: originally absent file still exists: {item.File.RelativePath}");
            }
            else
            {
                var current = FileIdentity.Capture(item.TargetPath);
                if (current != item.BackupIdentity)
                    throw new IOException($"Restore verification failed: original identity mismatch: {item.File.RelativePath}");

                if (!string.Equals(current.Sha256, item.File.ExpectedOriginalSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Restore verification failed: restored content does not match expected original hash: {item.File.RelativePath}");
            }
            return;
        }

        var applied = FileIdentity.Capture(item.TargetPath);
        if (applied != item.SourceIdentity)
            throw new IOException($"The resulting target does not match the source identity: {item.File.RelativePath}");
    }

    private void RecoverAttempted(IReadOnlyList<PreparedFile> prepared, int lastAttemptedIndex)
    {
        var startIndex = Math.Min(lastAttemptedIndex, prepared.Count - 1);
        for (var index = startIndex; index >= 0; index--)
        {
            var item = prepared[index];
            _hooks.DuringRecovery?.Invoke(index);

            if (item.PreOperationState == OriginalFileState.Existing)
            {
                if (item.UndoPath is null || !File.Exists(item.UndoPath))
                    throw new IOException($"Pre-operation undo copy is unavailable: {item.File.RelativePath}");

                var undoIdentity = FileIdentity.Capture(item.UndoPath);
                if (undoIdentity != item.PreOperationIdentity)
                    throw new IOException($"Pre-operation undo copy failed verification: {item.File.RelativePath}");

                Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
                File.Copy(item.UndoPath, item.TargetPath, overwrite: true);
                var recovered = FileIdentity.Capture(item.TargetPath);
                if (recovered != item.PreOperationIdentity)
                    throw new IOException($"Recovered target does not match the pre-operation identity: {item.File.RelativePath}");
            }
            else if (item.PreOperationState == OriginalFileState.DidNotExist)
            {
                if (File.Exists(item.TargetPath))
                    File.Delete(item.TargetPath);
                if (File.Exists(item.TargetPath))
                    throw new IOException($"Pre-operation absent target still exists after recovery: {item.File.RelativePath}");
            }
            else
            {
                throw new IOException($"Pre-operation state is unknown, so recovery cannot be proven safe: {item.File.RelativePath}");
            }
        }
    }

    private static void VerifyPreOperationState(IReadOnlyList<PreparedFile> prepared)
    {
        foreach (var item in prepared)
        {
            if (item.PreOperationState == OriginalFileState.Existing)
            {
                if (!File.Exists(item.TargetPath))
                    throw new IOException($"Target file is missing after recovery verification: {item.File.RelativePath}");

                var current = FileIdentity.Capture(item.TargetPath);
                if (current != item.PreOperationIdentity)
                    throw new IOException($"Target file does not match pre-operation identity during verification: {item.File.RelativePath}");
            }
            else if (item.PreOperationState == OriginalFileState.DidNotExist)
            {
                if (File.Exists(item.TargetPath))
                    throw new IOException($"Pre-operation absent target still exists during verification: {item.File.RelativePath}");
            }
            else
            {
                throw new IOException($"Pre-operation state is unknown during verification: {item.File.RelativePath}");
            }
        }
    }

    private string ResolveBackupPath(string? requestedRelativePath, string transactionId, string relativePath)
    {
        var relative = requestedRelativePath ?? Path.Combine("backups", transactionId, relativePath);
        return ResolveInsideRoot(_transactionStoreRoot, relative);
    }

    private static string ResolveInsideRoot(string installationRoot, string relativePath)
    {
        var root = Path.GetFullPath(installationRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, comparison))
            throw new UnauthorizedAccessException("The target path escapes the installation root.");
        return candidate;
    }

    private static void CleanupOperationArtifacts(string operationRoot, string transactionsRoot)
    {
        try
        {
            if (Directory.Exists(operationRoot))
                Directory.Delete(operationRoot, recursive: true);

            if (Directory.Exists(transactionsRoot) && !Directory.EnumerateFileSystemEntries(transactionsRoot).Any())
                Directory.Delete(transactionsRoot, recursive: false);
        }
        catch
        {
        }
    }

    private SafetyTransactionResult FailPhase2WithBackupCompensation(
        string transactionId,
        TransactionOperation operation,
        IReadOnlyList<string> affected,
        string operationRoot,
        string transactionsRoot,
        IReadOnlyList<string> createdNativeBackups,
        IReadOnlyList<ReplacedBackupRecord> replacedPriorBackups,
        string message)
    {
        try
        {
            CompensateBackups(createdNativeBackups, replacedPriorBackups);
            CleanupOperationArtifacts(operationRoot, transactionsRoot);
            return SafeFailure(transactionId, operation, affected, message);
        }
        catch (Exception backupRecoveryEx)
        {
            return new SafetyTransactionResult
            {
                TransactionId = transactionId,
                Operation = operation,
                State = TransactionState.AttentionRequired,
                Outcome = TransactionOutcome.UnresolvedRecovery,
                Message = $"{message} Backup compensation failed: {backupRecoveryEx.Message}",
                AffectedFiles = affected
            };
        }
    }

    private void CompensateBackups(
        IReadOnlyList<string> createdNativeBackups,
        IReadOnlyList<ReplacedBackupRecord> replacedPriorBackups)
    {
        for (var i = replacedPriorBackups.Count - 1; i >= 0; i--)
        {
            var replaced = replacedPriorBackups[i];
            if (!File.Exists(replaced.PriorBackupCopyPath))
            {
                throw new IOException($"Prior canonical backup copy is unavailable during compensation: {replaced.RelativePath}");
            }

            var priorCopyIdentity = FileIdentity.Capture(replaced.PriorBackupCopyPath);
            if (priorCopyIdentity != replaced.ExpectedPriorBackupIdentity)
            {
                throw new IOException($"Prior canonical backup copy failed identity verification during compensation: {replaced.RelativePath}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(replaced.BackupPath)!);
            File.Copy(replaced.PriorBackupCopyPath, replaced.BackupPath, overwrite: true);
            var restoredBackupIdentity = FileIdentity.Capture(replaced.BackupPath);
            if (restoredBackupIdentity != replaced.ExpectedPriorBackupIdentity)
            {
                throw new IOException($"Restored canonical backup does not match prior backup identity: {replaced.RelativePath}");
            }
        }

        for (var i = createdNativeBackups.Count - 1; i >= 0; i--)
        {
            var backupPath = createdNativeBackups[i];
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            if (File.Exists(backupPath))
            {
                throw new IOException($"Newly created native backup still exists after compensation deletion: {backupPath}");
            }

            try
            {
                PruneEmptyParentDirectories(Path.GetDirectoryName(backupPath), _transactionStoreRoot);
            }
            catch
            {
                // Directory pruning is best-effort after verified file removal.
            }
        }
    }

    private static void PruneEmptyParentDirectories(string? directoryPath, string stopAtRoot)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAtRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var current = directoryPath;

        while (!string.IsNullOrWhiteSpace(current))
        {
            var currentFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
            if (string.Equals(currentFull, rootFull, comparison) || !currentFull.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison))
                break;

            if (!Directory.Exists(currentFull) || Directory.EnumerateFileSystemEntries(currentFull).Any())
                break;

            Directory.Delete(currentFull, recursive: false);
            current = Path.GetDirectoryName(currentFull);
        }
    }

    private static SafetyTransactionResult Abort(string id, TransactionOperation operation, IReadOnlyList<string> affected, string message) =>
        new()
        {
            TransactionId = id,
            Operation = operation,
            State = TransactionState.Aborted,
            Outcome = TransactionOutcome.SafeAbort,
            Message = message,
            AffectedFiles = affected
        };

    private static bool IsValidSha256(string? sha256) =>
        !string.IsNullOrWhiteSpace(sha256) &&
        sha256.Length == 64 &&
        sha256.All(static c => Uri.IsHexDigit(c));

    private static SafetyTransactionResult SafeFailure(string id, TransactionOperation operation, IReadOnlyList<string> affected, string message) =>
        new()
        {
            TransactionId = id,
            Operation = operation,
            State = TransactionState.FailedSafely,
            Outcome = TransactionOutcome.SafeFailure,
            Message = message,
            AffectedFiles = affected
        };

    private sealed record ReplacedBackupRecord(
        string RelativePath,
        string BackupPath,
        string PriorBackupCopyPath,
        SafetyFileIdentity ExpectedPriorBackupIdentity);

    private sealed record ValidatedFile(
        MultiFileTransactionFile File,
        string TargetPath,
        string? SourcePath,
        SafetyFileIdentity? SourceIdentity,
        OriginalFileState PreOperationState,
        SafetyFileIdentity? PreOperationIdentity,
        string? UndoPath,
        string? StagedNativeBackupPath,
        string? PriorBackupCopyPath,
        string? BackupPath,
        SafetyFileIdentity? BackupIdentity);

    private sealed record PreparedFile(
        MultiFileTransactionFile File,
        string TargetPath,
        string? SourcePath,
        SafetyFileIdentity? SourceIdentity,
        OriginalFileState PreOperationState,
        SafetyFileIdentity? PreOperationIdentity,
        string? UndoPath,
        string? BackupPath,
        SafetyFileIdentity? BackupIdentity);
}
