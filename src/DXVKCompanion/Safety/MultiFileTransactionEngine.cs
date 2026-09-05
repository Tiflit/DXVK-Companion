using System.Text.Json;

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
}

public sealed record MultiFileTransactionTestHooks
{
    public Action<string, int>? AfterApply { get; init; }
    public Action<string>? BeforeRecovery { get; init; }
    public Action<int>? DuringRecovery { get; init; }
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

    public SafetyTransactionResult Execute(MultiFileTransactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transactionId = Guid.NewGuid().ToString("N");
        var affected = request.Files.Select(f => f.RelativePath).ToArray();
        var prepared = new List<PreparedFile>();
        var planPath = Path.Combine(_transactionStoreRoot, transactionId + ".json");
        var anyWrite = false;

        try
        {
            ValidateRequest(request);

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
                SafetyFileIdentity? backupIdentity = null;
                if (request.Operation is TransactionOperation.Install or TransactionOperation.Update or TransactionOperation.Reapply)
                    && currentState == OriginalFileState.Existing)
                {
                    backupPath = ResolveBackupPath(file.BackupRelativePath, transactionId, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(targetPath, backupPath, overwrite: true);
                    backupIdentity = FileIdentity.Capture(backupPath);
                    if (backupIdentity != currentIdentity)
                        return SafeFailure(transactionId, request.Operation, affected, $"The backup could not be verified: {file.RelativePath}");
                }
                else if (request.Operation == TransactionOperation.Restore && currentState == OriginalFileState.Existing)
                {
                    backupPath = ResolveBackupPath(file.BackupRelativePath, transactionId, file.RelativePath);
                    if (!File.Exists(backupPath))
                        return SafeFailure(transactionId, request.Operation, affected, $"Original backup is unavailable for restore: {file.RelativePath}");
                    backupIdentity = FileIdentity.Capture(backupPath);
                }

                prepared.Add(new PreparedFile(file, targetPath, sourcePath, sourceIdentity, currentState, currentIdentity, backupPath, backupIdentity));
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
                Apply(item, request.Operation);
                anyWrite = true;
                _hooks.AfterApply?.Invoke(item.TargetPath, index);
            }

            foreach (var item in prepared)
                Verify(item, request.Operation);

            File.Delete(planPath);
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
            if (!anyWrite)
            {
                TryDelete(planPath);
                return Abort(transactionId, request.Operation, affected, ex.Message);
            }

            try
            {
                _hooks.BeforeRecovery?.Invoke(Path.GetFullPath(request.InstallationRoot));
                RecoverAll(prepared);
                TryDelete(planPath);
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
            }
            return;
        }

        var applied = FileIdentity.Capture(item.TargetPath);
        if (applied != item.SourceIdentity)
            throw new IOException($"The resulting target does not match the source identity: {item.File.RelativePath}");
    }

    private void RecoverAll(IReadOnlyList<PreparedFile> prepared)
    {
        for (var index = prepared.Count - 1; index >= 0; index--)
        {
            var item = prepared[index];
            _hooks.DuringRecovery?.Invoke(index);

            if (item.OriginalState == OriginalFileState.Existing)
            {
                if (item.BackupPath is null || !File.Exists(item.BackupPath))
                    throw new IOException($"Original backup is unavailable: {item.File.RelativePath}");
                File.Copy(item.BackupPath, item.TargetPath, overwrite: true);
                var recovered = FileIdentity.Capture(item.TargetPath);
                if (recovered != item.OriginalIdentity)
                    throw new IOException($"Recovered target does not match the original identity: {item.File.RelativePath}");
            }
            else if (item.OriginalState == OriginalFileState.DidNotExist)
            {
                if (File.Exists(item.TargetPath))
                    File.Delete(item.TargetPath);
                if (File.Exists(item.TargetPath))
                    throw new IOException($"Originally absent target still exists after recovery: {item.File.RelativePath}");
            }
            else
            {
                throw new IOException($"Original state is unknown, so recovery cannot be proven safe: {item.File.RelativePath}");
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
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

    private sealed record PreparedFile(
        MultiFileTransactionFile File,
        string TargetPath,
        string? SourcePath,
        SafetyFileIdentity? SourceIdentity,
        OriginalFileState OriginalState,
        SafetyFileIdentity? OriginalIdentity,
        string? BackupPath,
        SafetyFileIdentity? BackupIdentity);
}
