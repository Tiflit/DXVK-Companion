using System.Text.Json;

namespace DXVKCompanion.Safety;

public sealed record SingleFileTransactionRequest
{
    public required string InstallationRoot { get; init; }
    public required string RelativePath { get; init; }
    public required TransactionOperation Operation { get; init; }
    public string? SourceFilePath { get; init; }
    public SafetyFileIdentity? ExpectedTargetIdentity { get; init; }
    public SafetyFileIdentity? ExpectedSourceIdentity { get; init; }
    public OriginalFileState OriginalState { get; init; } = OriginalFileState.Unknown;
    public string? BackupRelativePath { get; init; }
}

public sealed record SingleFileTransactionTestHooks
{
    public Action<string>? AfterApply { get; init; }
    public Action<string>? BeforeRecovery { get; init; }
    public Action<string>? DuringRecovery { get; init; }
}

public sealed class SingleFileTransactionEngine
{
    private readonly string _transactionStoreRoot;
    private readonly SingleFileTransactionTestHooks _hooks;

    public SingleFileTransactionEngine(string transactionStoreRoot, SingleFileTransactionTestHooks? hooks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionStoreRoot);
        _transactionStoreRoot = Path.GetFullPath(transactionStoreRoot);
        Directory.CreateDirectory(_transactionStoreRoot);
        _hooks = hooks ?? new SingleFileTransactionTestHooks();
    }

    public SafetyTransactionResult Execute(SingleFileTransactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transactionId = Guid.NewGuid().ToString("N");
        var affected = new[] { request.RelativePath };
        string targetPath = string.Empty;
        string? sourcePath = null;
        string? backupPath = null;
        SafetyFileIdentity? originalIdentity = null;
        OriginalFileState originalState = OriginalFileState.Unknown;
        SafetyFileIdentity? expectedSourceIdentity = request.ExpectedSourceIdentity;

        try
        {
            try
            {
                targetPath = ResolveInsideRoot(request.InstallationRoot, request.RelativePath);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Abort(transactionId, request.Operation, affected, ex.Message);
            }
            if (request.Operation == TransactionOperation.None)
            {
                return Abort(transactionId, request.Operation, affected, "A transaction operation is required.");
            }
            if (request.SourceFilePath is not null)
            {
                sourcePath = Path.GetFullPath(request.SourceFilePath);
            }

            if (request.Operation is TransactionOperation.Install or TransactionOperation.Update or TransactionOperation.Reapply)
            {
                if (sourcePath is null)
                {
                    return Abort(transactionId, request.Operation, affected, "A source file is required.");
                }

                if (!File.Exists(sourcePath))
                {
                    return Abort(transactionId, request.Operation, affected, "The source file does not exist.");
                }

                var sourceIdentity = FileIdentity.Capture(sourcePath);
                if (expectedSourceIdentity is not null && sourceIdentity != expectedSourceIdentity)
                {
                    return Abort(transactionId, request.Operation, affected, "The source file changed before apply.");
                }
            }

            originalState = File.Exists(targetPath) ? OriginalFileState.Existing : OriginalFileState.DidNotExist;
            originalIdentity = originalState == OriginalFileState.Existing ? FileIdentity.Capture(targetPath) : null;

            var targetMatchesExpected = request.ExpectedTargetIdentity is null
                ? originalState == OriginalFileState.DidNotExist
                : originalState == OriginalFileState.Existing && originalIdentity == request.ExpectedTargetIdentity;

            if (!targetMatchesExpected && request.Operation != TransactionOperation.Restore)
            {
                return Abort(transactionId, request.Operation, affected, "The target changed during validation.");
            }

            if (request.Operation == TransactionOperation.Restore && request.OriginalState == OriginalFileState.Existing)
            {
                if (request.ExpectedTargetIdentity is not null && (originalState != OriginalFileState.Existing || originalIdentity != request.ExpectedTargetIdentity))
                {
                    return Abort(transactionId, request.Operation, affected, "The managed target changed before restore.");
                }
            }

            var plan = new SafetyTransactionPlan
            {
                TransactionId = transactionId,
                Operation = request.Operation,
                InstallationRoot = Path.GetFullPath(request.InstallationRoot),
                Files = new[]
                {
                    new SafetyFilePlan
                    {
                        RelativePath = request.RelativePath,
                        SourceRelativePath = request.SourceFilePath is null ? string.Empty : sourcePath!,
                        ExpectedTargetIdentity = request.ExpectedTargetIdentity,
                        ExpectedSourceIdentity = expectedSourceIdentity
                    }
                },
                State = TransactionState.Prepared
            };

            var planPath = Path.Combine(_transactionStoreRoot, transactionId + ".json");
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan));

            if (originalState == OriginalFileState.Existing)
            {
                backupPath = ResolveBackupPath(request.BackupRelativePath, transactionId, request.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(targetPath, backupPath, overwrite: true);
                var backupIdentity = FileIdentity.Capture(backupPath);
                if (originalIdentity != backupIdentity)
                {
                    return SafeFailure(transactionId, request.Operation, affected, "The backup could not be verified.");
                }
            }

            if (request.Operation == TransactionOperation.Restore)
            {
                RestoreOriginal(targetPath, backupPath, request.OriginalState);
            }
            else
            {
                File.Copy(sourcePath!, targetPath, overwrite: true);
            }

            _hooks.AfterApply?.Invoke(targetPath);

            if (request.Operation == TransactionOperation.Restore)
            {
                VerifyRestore(targetPath, request.OriginalState, backupPath);
            }
            else
            {
                var appliedIdentity = FileIdentity.Capture(targetPath);
                var sourceIdentity = FileIdentity.Capture(sourcePath!);
                if (appliedIdentity != sourceIdentity)
                {
                    throw new IOException("The resulting target does not match the source identity.");
                }
            }

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
            try
            {
                _hooks.BeforeRecovery?.Invoke(Path.GetFullPath(request.InstallationRoot));
                Recover(request, transactionId, backupPath, originalState, targetPath, originalIdentity);
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

    private void Recover(
        SingleFileTransactionRequest request,
        string transactionId,
        string? backupPath,
        OriginalFileState originalState,
        string targetPath,
        SafetyFileIdentity? originalIdentity)
    {
        _hooks.DuringRecovery?.Invoke(transactionId);

        if (originalState == OriginalFileState.Existing)
        {
            if (backupPath is null || !File.Exists(backupPath))
            {
                throw new IOException("Original backup is unavailable.");
            }

            File.Copy(backupPath, targetPath, overwrite: true);
            var recoveredIdentity = FileIdentity.Capture(targetPath);
            if (recoveredIdentity != originalIdentity)
            {
                throw new IOException("Recovered target does not match the original identity.");
            }
        }
        else if (originalState == OriginalFileState.DidNotExist)
        {
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            if (File.Exists(targetPath))
            {
                throw new IOException("Originally absent target still exists after recovery.");
            }
        }
        else
        {
            throw new IOException("Original state was unknown, so recovery cannot be proven safe.");
        }
    }

    private static void RestoreOriginal(string targetPath, string? backupPath, OriginalFileState originalState)
    {
        switch (originalState)
        {
            case OriginalFileState.Existing:
                if (backupPath is null || !File.Exists(backupPath))
                {
                    throw new IOException("Original backup is unavailable for restore.");
                }

                File.Copy(backupPath, targetPath, overwrite: true);
                break;
            case OriginalFileState.DidNotExist:
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
                break;
            default:
                throw new IOException("Original state is unknown; restore is unsafe.");
        }
    }

    private static void VerifyRestore(string targetPath, OriginalFileState originalState, string? backupPath)
    {
        if (originalState == OriginalFileState.DidNotExist)
        {
            if (File.Exists(targetPath))
            {
                throw new IOException("Restore verification failed: originally absent file still exists.");
            }

            return;
        }

        if (backupPath is null || !File.Exists(targetPath))
        {
            throw new IOException("Restore verification failed: original file is missing.");
        }

        var restoredIdentity = FileIdentity.Capture(targetPath);
        var backupIdentity = FileIdentity.Capture(backupPath);
        if (restoredIdentity != backupIdentity)
        {
            throw new IOException("Restore verification failed: original identity mismatch.");
        }
    }

    private static string ResolveInsideRoot(string installationRoot, string relativePath)
    {
        var root = Path.GetFullPath(installationRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, comparison))
        {
            throw new UnauthorizedAccessException("The target path escapes the installation root.");
        }

        return candidate;
    }

    private string ResolveBackupPath(string? requestedRelativePath, string transactionId, string relativePath)
    {
        var relative = requestedRelativePath ?? Path.Combine("backups", transactionId, relativePath);
        return ResolveInsideRoot(_transactionStoreRoot, relative);
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
}
