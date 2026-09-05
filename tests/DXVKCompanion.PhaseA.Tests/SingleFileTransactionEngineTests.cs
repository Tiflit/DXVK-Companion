using DXVKCompanion.Safety;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class SingleFileTransactionEngineTests
{
    [Fact]
    public void Install_ExistingFile_CapturesBaselineAndCommits()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var target = game.CreateFile("d3d11.dll", "original");
        var source = game.CreateFile("source.dll", "dxvk-new");
        var original = FileIdentity.Capture(target);
        var sourceIdentity = FileIdentity.Capture(source);

        var engine = new SingleFileTransactionEngine(storage.RootPath);
        var result = engine.Execute(new SingleFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            RelativePath = "d3d11.dll",
            SourceFilePath = source,
            ExpectedTargetIdentity = original,
            ExpectedSourceIdentity = sourceIdentity,
            OriginalState = OriginalFileState.Existing,
            Operation = TransactionOperation.Install
        });

        Assert.Equal(TransactionState.Committed, result.State);
        Assert.Equal(TransactionOutcome.Success, result.Outcome);
        Assert.Equal("dxvk-new", File.ReadAllText(target));
    }

    [Fact]
    public void Install_AbsentFile_CreatesAndCommits()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var source = game.CreateFile("source.dll", "dxvk-new");

        var engine = new SingleFileTransactionEngine(storage.RootPath);
        var result = engine.Execute(new SingleFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            RelativePath = "d3d11.dll",
            SourceFilePath = source,
            ExpectedSourceIdentity = FileIdentity.Capture(source),
            OriginalState = OriginalFileState.DidNotExist,
            Operation = TransactionOperation.Install
        });

        Assert.Equal(TransactionState.Committed, result.State);
        Assert.True(File.Exists(Path.Combine(game.RootPath, "d3d11.dll")));
    }

    [Fact]
    public void Restore_ExistingOriginal_ReturnsExactBaseline()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var target = game.CreateFile("d3d11.dll", "original");
        var source = game.CreateFile("source.dll", "dxvk-new");
        var original = FileIdentity.Capture(target);

        var engine = new SingleFileTransactionEngine(storage.RootPath);
        var install = engine.Execute(new SingleFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            RelativePath = "d3d11.dll",
            SourceFilePath = source,
            ExpectedTargetIdentity = original,
            ExpectedSourceIdentity = FileIdentity.Capture(source),
            OriginalState = OriginalFileState.Existing,
            Operation = TransactionOperation.Install
        });
        Assert.Equal(TransactionOutcome.Success, install.Outcome);

        var restore = engine.Execute(new SingleFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            RelativePath = "d3d11.dll",
            ExpectedTargetIdentity = FileIdentity.Capture(target),
            OriginalState = OriginalFileState.Existing,
            BackupRelativePath = Path.Combine("backups", install.TransactionId, "d3d11.dll"),
            Operation = TransactionOperation.Restore
        });

        Assert.Equal(TransactionOutcome.Success, restore.Outcome);
        Assert.Equal(original, FileIdentity.Capture(target));
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Fact]
    public void Restore_OriginallyAbsentFile_RemovesManagedFile()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var source = game.CreateFile("source.dll", "dxvk-new");
        var engine = new SingleFileTransactionEngine(storage.RootPath);

        var install = engine.Execute(new SingleFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            RelativePath = "d3d11.dll",
            SourceFilePath = source,
            ExpectedSourceIdentity = FileIdentity.Capture(source),
            OriginalState = OriginalFileState.DidNotExist,
            Operation = TransactionOperation.Install
        });
        Assert.Equal(TransactionOutcome.Success, install.Outcome);

        var target = Path.Combine(game.RootPath, "d3d11.dll");
        var restore = engine.Execute(new SingleFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            RelativePath = "d3d11.dll",
            ExpectedTargetIdentity = FileIdentity.Capture(target),
            OriginalState = OriginalFileState.DidNotExist,
            Operation = TransactionOperation.Restore
        });

        Assert.Equal(TransactionOutcome.Success, restore.Outcome);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void MissingSource_AbortsWithoutChangingTarget()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var target = game.CreateFile("d3d11.dll", "original");
        var before = FileIdentity.Capture(target);

        var engine = new SingleFileTransactionEngine(storage.RootPath);
        var result = engine.Execute(new SingleFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            RelativePath = "d3d11.dll",
            SourceFilePath = Path.Combine(game.RootPath, "missing.dll"),
            ExpectedTargetIdentity = before,
            OriginalState = OriginalFileState.Existing,
            Operation = TransactionOperation.Update
        });

        Assert.Equal(TransactionState.Aborted, result.State);
        Assert.Equal(TransactionOutcome.SafeAbort, result.Outcome);
        Assert.Equal(before, FileIdentity.Capture(target));
    }

    [Fact]
    public void ValidationTimeExternalChange_AbortsWithoutOverwrite()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var target = game.CreateFile("d3d11.dll", "original");
        var source = game.CreateFile("source.dll", "dxvk-new");

        var engine = new SingleFileTransactionEngine(storage.RootPath);
        var result = engine.Execute(new SingleFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            RelativePath = "d3d11.dll",
            SourceFilePath = source,
            ExpectedTargetIdentity = new SafetyFileIdentity(new string('0', 64), FileIdentity.Capture(target).Size),
            ExpectedSourceIdentity = FileIdentity.Capture(source),
            OriginalState = OriginalFileState.Existing,
            Operation = TransactionOperation.Update
        });

        Assert.Equal(TransactionState.Aborted, result.State);
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Fact]
    public void VerificationMismatch_RollsBackToOriginal()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var target = game.CreateFile("d3d11.dll", "original");
        var source = game.CreateFile("source.dll", "dxvk-new");
        var original = FileIdentity.Capture(target);

        var engine = new SingleFileTransactionEngine(storage.RootPath, new SingleFileTransactionTestHooks
        {
            AfterApply = path => File.WriteAllText(path, "tampered-after-apply")
        });

        var result = engine.Execute(new SingleFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            RelativePath = "d3d11.dll",
            SourceFilePath = source,
            ExpectedTargetIdentity = original,
            ExpectedSourceIdentity = FileIdentity.Capture(source),
            OriginalState = OriginalFileState.Existing,
            Operation = TransactionOperation.Update
        });

        Assert.Equal(TransactionState.FailedSafely, result.State);
        Assert.Equal(TransactionOutcome.SafeFailure, result.Outcome);
        Assert.Equal(original, FileIdentity.Capture(target));
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Fact]
    public void FailedApply_OnOriginallyAbsentFile_RollsBackByDeletingTarget()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var source = game.CreateFile("source.dll", "dxvk-new");
        var target = Path.Combine(game.RootPath, "d3d11.dll");

        var engine = new SingleFileTransactionEngine(storage.RootPath, new SingleFileTransactionTestHooks
        {
            AfterApply = path => throw new IOException("synthetic verification/apply failure")
        });

        var result = engine.Execute(new SingleFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            RelativePath = "d3d11.dll",
            SourceFilePath = source,
            ExpectedSourceIdentity = FileIdentity.Capture(source),
            OriginalState = OriginalFileState.DidNotExist,
            Operation = TransactionOperation.Install
        });

        Assert.Equal(TransactionState.FailedSafely, result.State);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void RecoveryFailure_ProducesAttentionRequired()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var target = game.CreateFile("d3d11.dll", "original");
        var source = game.CreateFile("source.dll", "dxvk-new");
        var original = FileIdentity.Capture(target);

        var engine = new SingleFileTransactionEngine(storage.RootPath, new SingleFileTransactionTestHooks
        {
            AfterApply = path => File.WriteAllText(path, "tampered"),
            DuringRecovery = _ => throw new IOException("synthetic recovery failure")
        });

        var result = engine.Execute(new SingleFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            RelativePath = "d3d11.dll",
            SourceFilePath = source,
            ExpectedTargetIdentity = original,
            ExpectedSourceIdentity = FileIdentity.Capture(source),
            OriginalState = OriginalFileState.Existing,
            Operation = TransactionOperation.Update
        });

        Assert.Equal(TransactionState.AttentionRequired, result.State);
        Assert.Equal(TransactionOutcome.UnresolvedRecovery, result.Outcome);
    }

    [Fact]
    public void LockedTarget_FailsSafelyAndLeavesOriginalIntact()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var target = game.CreateFile("d3d11.dll", "original");
        var source = game.CreateFile("source.dll", "dxvk-new");
        var original = FileIdentity.Capture(target);

        using var lockHandle = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);
        var engine = new SingleFileTransactionEngine(storage.RootPath);
        var result = engine.Execute(new SingleFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            RelativePath = "d3d11.dll",
            SourceFilePath = source,
            ExpectedTargetIdentity = original,
            ExpectedSourceIdentity = FileIdentity.Capture(source),
            OriginalState = OriginalFileState.Existing,
            Operation = TransactionOperation.Update
        });

        Assert.NotEqual(TransactionOutcome.Success, result.Outcome);
        Assert.True(result.State is TransactionState.Aborted or TransactionState.FailedSafely or TransactionState.AttentionRequired);
    }

    [Fact]
    public void PathTraversal_IsRejectedBeforeAnyWrite()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var source = game.CreateFile("source.dll", "dxvk-new");
        var outside = Path.Combine(Path.GetDirectoryName(game.RootPath)!, "outside.dll");
        if (File.Exists(outside)) File.Delete(outside);

        var engine = new SingleFileTransactionEngine(storage.RootPath);
        var result = engine.Execute(new SingleFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            RelativePath = @"..\outside.dll",
            SourceFilePath = source,
            ExpectedSourceIdentity = FileIdentity.Capture(source),
            OriginalState = OriginalFileState.DidNotExist,
            Operation = TransactionOperation.Install
        });

        Assert.Equal(TransactionState.Aborted, result.State);
        Assert.False(File.Exists(outside));
    }
}
