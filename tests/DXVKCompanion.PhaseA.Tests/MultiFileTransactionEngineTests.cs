using DXVKCompanion.Safety;

namespace DXVKCompanion.PhaseATests;

public sealed class MultiFileTransactionEngineTests
{
    [Fact]
    public void ThreeFileUpdate_SucceedsAsOneLogicalTransaction()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var a = game.CreateFile("d3d9.dll", "old-a");
        var b = game.CreateFile("d3d11.dll", "old-b");
        var c = game.CreateFile("dxgi.dll", "old-c");
        var sa = game.CreateFile("src-a.dll", "new-a");
        var sb = game.CreateFile("src-b.dll", "new-b");
        var sc = game.CreateFile("src-c.dll", "new-c");

        var engine = new MultiFileTransactionEngine(storage.RootPath);
        var result = engine.Execute(UpdateRequest(game.RootPath,
            ("d3d9.dll", a, sa, OriginalFileState.Existing, null),
            ("d3d11.dll", b, sb, OriginalFileState.Existing, null),
            ("dxgi.dll", c, sc, OriginalFileState.Existing, null)));

        Assert.Equal(TransactionState.Committed, result.State);
        Assert.Equal(TransactionOutcome.Success, result.Outcome);
        Assert.Equal("new-a", File.ReadAllText(a));
        Assert.Equal("new-b", File.ReadAllText(b));
        Assert.Equal("new-c", File.ReadAllText(c));
    }

    [Fact]
    public void FailureOnSecondFile_RestoresExactPreTransactionState()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var a = game.CreateFile("d3d9.dll", "old-a");
        var b = game.CreateFile("d3d11.dll", "old-b");
        var c = game.CreateFile("dxgi.dll", "old-c");
        var sa = game.CreateFile("src-a.dll", "new-a");
        var sb = game.CreateFile("src-b.dll", "new-b");
        var sc = game.CreateFile("src-c.dll", "new-c");
        var before = new[] { FileIdentity.Capture(a), FileIdentity.Capture(b), FileIdentity.Capture(c) };

        var engine = new MultiFileTransactionEngine(storage.RootPath, new MultiFileTransactionTestHooks
        {
            AfterApply = (_, index) =>
            {
                if (index == 1)
                    throw new IOException("synthetic second-file failure");
            }
        });

        var result = engine.Execute(UpdateRequest(game.RootPath,
            ("d3d9.dll", a, sa, OriginalFileState.Existing, null),
            ("d3d11.dll", b, sb, OriginalFileState.Existing, null),
            ("dxgi.dll", c, sc, OriginalFileState.Existing, null)));

        Assert.Equal(TransactionState.FailedSafely, result.State);
        Assert.Equal(TransactionOutcome.SafeFailure, result.Outcome);
        Assert.Equal(before[0], FileIdentity.Capture(a));
        Assert.Equal(before[1], FileIdentity.Capture(b));
        Assert.Equal(before[2], FileIdentity.Capture(c));
    }

    [Fact]
    public void FailureOnThirdFile_RestoresEarlierFilesAndOriginallyAbsentFile()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var a = game.CreateFile("d3d9.dll", "old-a");
        var b = game.CreateFile("d3d11.dll", "old-b");
        var c = Path.Combine(game.RootPath, "dxgi.dll");
        var sa = game.CreateFile("src-a.dll", "new-a");
        var sb = game.CreateFile("src-b.dll", "new-b");
        var sc = game.CreateFile("src-c.dll", "new-c");
        var aBefore = FileIdentity.Capture(a);
        var bBefore = FileIdentity.Capture(b);

        var engine = new MultiFileTransactionEngine(storage.RootPath, new MultiFileTransactionTestHooks
        {
            AfterApply = (_, index) =>
            {
                if (index == 2)
                    throw new IOException("synthetic third-file failure");
            }
        });

        var result = engine.Execute(UpdateRequest(game.RootPath,
            ("d3d9.dll", a, sa, OriginalFileState.Existing, null),
            ("d3d11.dll", b, sb, OriginalFileState.Existing, null),
            ("dxgi.dll", null, sc, OriginalFileState.DidNotExist, null)));

        Assert.Equal(TransactionState.FailedSafely, result.State);
        Assert.Equal(TransactionOutcome.SafeFailure, result.Outcome);
        Assert.Equal(aBefore, FileIdentity.Capture(a));
        Assert.Equal(bBefore, FileIdentity.Capture(b));
        Assert.False(File.Exists(c));
    }

    [Fact]
    public void RecoveryUncertainty_ProducesAttentionRequired()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var a = game.CreateFile("d3d9.dll", "old-a");
        var b = game.CreateFile("d3d11.dll", "old-b");
        var sa = game.CreateFile("src-a.dll", "new-a");
        var sb = game.CreateFile("src-b.dll", "new-b");

        var engine = new MultiFileTransactionEngine(storage.RootPath, new MultiFileTransactionTestHooks
        {
            AfterApply = (_, index) =>
            {
                if (index == 1)
                    throw new IOException("synthetic failure");
            },
            DuringRecovery = index =>
            {
                if (index == 0)
                    throw new IOException("synthetic recovery uncertainty");
            }
        });

        var result = engine.Execute(UpdateRequest(game.RootPath,
            ("d3d9.dll", a, sa, OriginalFileState.Existing, null),
            ("d3d11.dll", b, sb, OriginalFileState.Existing, null)));

        Assert.Equal(TransactionState.AttentionRequired, result.State);
        Assert.Equal(TransactionOutcome.UnresolvedRecovery, result.Outcome);
    }

    [Fact]
    public void ValidationFailure_ChangesNothing()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var a = game.CreateFile("d3d9.dll", "old-a");
        var b = game.CreateFile("d3d11.dll", "old-b");
        var sa = game.CreateFile("src-a.dll", "new-a");
        var sb = game.CreateFile("src-b.dll", "new-b");
        var aBefore = FileIdentity.Capture(a);
        var bBefore = FileIdentity.Capture(b);

        var result = new MultiFileTransactionEngine(storage.RootPath).Execute(UpdateRequest(game.RootPath,
            ("d3d9.dll", a, sa, OriginalFileState.Existing, null),
            ("d3d11.dll", b, sb, OriginalFileState.Existing, new SafetyFileIdentity(new string('0', 64), new FileInfo(b).Length))));

        Assert.Equal(TransactionState.Aborted, result.State);
        Assert.Equal(aBefore, FileIdentity.Capture(a));
        Assert.Equal(bBefore, FileIdentity.Capture(b));
    }

    [Fact]
    public void DuplicatePath_IsRejectedBeforeAnyWrite()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var target = game.CreateFile("d3d11.dll", "old");
        var source = game.CreateFile("source.dll", "new");
        var result = new MultiFileTransactionEngine(storage.RootPath).Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Update,
            Files = new[]
            {
                new MultiFileTransactionFile { RelativePath = "d3d11.dll", SourceFilePath = source, ExpectedTargetIdentity = FileIdentity.Capture(target), ExpectedSourceIdentity = FileIdentity.Capture(source), OriginalState = OriginalFileState.Existing },
                new MultiFileTransactionFile { RelativePath = "d3d11.dll", SourceFilePath = source, ExpectedTargetIdentity = FileIdentity.Capture(target), ExpectedSourceIdentity = FileIdentity.Capture(source), OriginalState = OriginalFileState.Existing }
            }
        });

        Assert.Equal(TransactionState.Aborted, result.State);
        Assert.Equal("old", File.ReadAllText(target));
    }

    [Fact]
    public void RestoreThreeFiles_ReturnsAllFilesToOriginalState()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        var a = game.CreateFile("d3d9.dll", "old-a");
        var b = game.CreateFile("d3d11.dll", "old-b");
        var c = game.CreateFile("dxgi.dll", "old-c");
        var sa = game.CreateFile("src-a.dll", "new-a");
        var sb = game.CreateFile("src-b.dll", "new-b");
        var sc = game.CreateFile("src-c.dll", "new-c");
        var engine = new MultiFileTransactionEngine(storage.RootPath);

        var install = engine.Execute(UpdateRequest(game.RootPath, ("d3d9.dll", a, sa, OriginalFileState.Existing, null), ("d3d11.dll", b, sb, OriginalFileState.Existing, null), ("dxgi.dll", c, sc, OriginalFileState.Existing, null)));
        Assert.Equal(TransactionOutcome.Success, install.Outcome);

        var restore = engine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Restore,
            Files = new[]
            {
                RestoreFile("d3d9.dll", a, install.TransactionId, storage.RootPath),
                RestoreFile("d3d11.dll", b, install.TransactionId, storage.RootPath),
                RestoreFile("dxgi.dll", c, install.TransactionId, storage.RootPath)
            }
        });

        Assert.Equal(TransactionOutcome.Success, restore.Outcome);
        Assert.Equal("old-a", File.ReadAllText(a));
        Assert.Equal("old-b", File.ReadAllText(b));
        Assert.Equal("old-c", File.ReadAllText(c));
    }

    private static MultiFileTransactionRequest UpdateRequest(string root, params (string RelativePath, string? Target, string Source, OriginalFileState State, SafetyFileIdentity? ExpectedTarget)[] items)
        => new()
        {
            InstallationRoot = root,
            Operation = TransactionOperation.Update,
            Files = items.Select(x => new MultiFileTransactionFile
            {
                RelativePath = x.RelativePath,
                SourceFilePath = x.Source,
                ExpectedTargetIdentity = x.ExpectedTarget ?? (x.State == OriginalFileState.Existing ? FileIdentity.Capture(x.Target!) : null),
                ExpectedSourceIdentity = FileIdentity.Capture(x.Source),
                OriginalState = x.State
            }).ToArray()
        };

    private static MultiFileTransactionFile RestoreFile(string relativePath, string target, string transactionId, string storageRoot)
    {
        var backup = Path.Combine(storageRoot, "backups", transactionId, relativePath);
        return new MultiFileTransactionFile
        {
            RelativePath = relativePath,
            ExpectedTargetIdentity = FileIdentity.Capture(target),
            OriginalState = OriginalFileState.Existing,
            BackupRelativePath = Path.GetRelativePath(storageRoot, backup)
        };
    }
}
