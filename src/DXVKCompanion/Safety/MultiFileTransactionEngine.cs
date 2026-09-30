using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DXVKCompanion.Utils;

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
    public FileTransactionAction Action { get; init; } = FileTransactionAction.Deploy;
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

    public string TransactionStoreRoot => _transactionStoreRoot;

    public int RecoverInterruptedTransactions()
    {
        if (!Directory.Exists(_transactionStoreRoot)) return 0;

        int recoveredCount = 0;
        foreach (var planFile in Directory.EnumerateFiles(_transactionStoreRoot, "*.json"))
        {
            string fileName = Path.GetFileName(planFile);
            if (fileName.Equals("game-library.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("games.json", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var json = File.ReadAllText(planFile);
                var plan = JsonSerializer.Deserialize<SafetyTransactionPlan>(json);
                if (plan == null || string.IsNullOrWhiteSpace(plan.InstallationRoot) || plan.Files == null)
                    continue;

                // If prepared (no writes ever made) or committed (already verified before termination), simply clean up
                if (plan.State == TransactionState.Prepared || plan.State == TransactionState.Committed)
                {
                    TryDelete(planFile);
                    continue;
                }

                for (var index = plan.Files.Count - 1; index >= 0; index--)
                {
                    var filePlan = plan.Files[index];
                    var targetPath = ResolveInsideRoot(plan.InstallationRoot, filePlan.RelativePath);

                    if (filePlan.OriginalState == OriginalFileState.Existing)
                    {
                        string backupPath = ResolveBackupPath(filePlan.BackupRelativePath, plan.TransactionId, filePlan.RelativePath);
                        if (File.Exists(backupPath))
                        {
                            var backupIdent = FileIdentity.Capture(backupPath);
                            if (filePlan.BackupIdentity != null && backupIdent != filePlan.BackupIdentity)
                            {
                                Logger.Log($"MultiFileTransactionEngine: backup identity mismatch during crash recovery for {filePlan.RelativePath}");
                                continue;
                            }

                            File.Copy(backupPath, targetPath, overwrite: true);

                            var recoveredIdent = FileIdentity.Capture(targetPath);
                            if (filePlan.OriginalIdentity != null && recoveredIdent != filePlan.OriginalIdentity)
                            {
                                Logger.Log($"MultiFileTransactionEngine: recovered file identity mismatch for {filePlan.RelativePath}");
                            }
                        }
                    }
                    else if (filePlan.OriginalState == OriginalFileState.DidNotExist)
                    {
                        if (File.Exists(targetPath))
                        {
                            File.Delete(targetPath);
                        }
                    }
                }

                TryDelete(planFile);
                recoveredCount++;
            }
            catch
            {
                // Ignore corrupt or unreadable plan
            }
        }

        return recoveredCount;
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
                if ((request.Operation is TransactionOperation.Install or TransactionOperation.Update or TransactionOperation.Reapply)
                    && file.Action == FileTransactionAction.Deploy)
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

                if (file.Action == FileTransactionAction.RestoreOriginal || request.Operation == TransactionOperation.Restore)
                {
                    if (file.OriginalState == OriginalFileState.Existing)
                    {
                        backupPath = ResolveBackupPath(file.BackupRelativePath, transactionId, file.RelativePath);
                        if (!File.Exists(backupPath))
                            return SafeFailure(transactionId, request.Operation, affected, $"Original backup is unavailable for restore: {file.RelativePath}");
                        backupIdentity = FileIdentity.Capture(backupPath);
                    }
                }
                else if (file.OriginalState == OriginalFileState.Existing)
                {
                    backupPath = ResolveBackupPath(file.BackupRelativePath, transactionId, file.RelativePath);
                    if (request.Operation == TransactionOperation.Install || !File.Exists(backupPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                        File.Copy(targetPath, backupPath, overwrite: true);
                        backupIdentity = FileIdentity.Capture(backupPath);
                        if (backupIdentity != currentIdentity)
                            return SafeFailure(transactionId, request.Operation, affected, $"The backup could not be verified: {file.RelativePath}");
                    }
                    else
                    {
                        backupIdentity = FileIdentity.Capture(backupPath);
                    }
                }

                prepared.Add(new PreparedFile(file, targetPath, sourcePath, sourceIdentity, file.OriginalState, currentIdentity, backupPath, backupIdentity));
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
                    Action = p.File.Action,
                    ExpectedTargetIdentity = p.File.ExpectedTargetIdentity,
                    ExpectedSourceIdentity = p.File.ExpectedSourceIdentity,
                    OriginalState = p.OriginalState,
                    BackupRelativePath = p.File.BackupRelativePath,
                    OriginalIdentity = p.OriginalIdentity,
                    BackupIdentity = p.BackupIdentity
                }).ToArray(),
                State = TransactionState.Prepared
            };
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan));

            plan = plan with { State = TransactionState.Applying };
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan));

            for (var index = 0; index < prepared.Count; index++)
            {
                var item = prepared[index];
                Apply(item, request.Operation);
                anyWrite = true;
                _hooks.AfterApply?.Invoke(item.TargetPath, index);
            }

            plan = plan with { State = TransactionState.Verifying };
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan));

            foreach (var item in prepared)
                Verify(item, request.Operation);

            plan = plan with { State = TransactionState.Committed };
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan));

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

            if ((request.Operation is TransactionOperation.Install or TransactionOperation.Update or TransactionOperation.Reapply)
                && file.Action == FileTransactionAction.Deploy)
            {
                if (string.IsNullOrWhiteSpace(file.SourceFilePath))
                    throw new InvalidOperationException($"A source file is required: {file.RelativePath}");
            }

            if ((request.Operation == TransactionOperation.Restore || file.Action == FileTransactionAction.RestoreOriginal)
                && file.OriginalState == OriginalFileState.Unknown)
            {
                throw new InvalidOperationException($"Original state is required for restore: {file.RelativePath}");
            }
        }
    }

    private static void ValidateTargetExpectation(TransactionOperation operation, MultiFileTransactionFile file, OriginalFileState currentState, SafetyFileIdentity? currentIdentity)
    {
        var matches = file.ExpectedTargetIdentity is null
            ? currentState == OriginalFileState.DidNotExist
            : currentState == OriginalFileState.Existing && currentIdentity == file.ExpectedTargetIdentity;

        if (operation != TransactionOperation.Restore && file.Action != FileTransactionAction.RestoreOriginal)
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
        if (operation == TransactionOperation.Restore || item.File.Action == FileTransactionAction.RestoreOriginal)
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
        if (operation == TransactionOperation.Restore || item.File.Action == FileTransactionAction.RestoreOriginal)
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

            if (item.OriginalIdentity is not null)
            {
                if (item.BackupPath != null && File.Exists(item.BackupPath))
                {
                    File.Copy(item.BackupPath, item.TargetPath, overwrite: true);
                }
                else if (item.OriginalState == OriginalFileState.Existing && item.BackupPath is null)
                {
                    throw new IOException($"Original backup is unavailable: {item.File.RelativePath}");
                }
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
