using DXVKCompanion.Safety;
using Xunit;

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
        var aIdentity = FileIdentity.Capture(a);
        var bIdentity = FileIdentity.Capture(b);
        var cIdentity = FileIdentity.Capture(c);
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
                RestoreFile("d3d9.dll", a, install.TransactionId, storage.RootPath, aIdentity.Sha256),
                RestoreFile("d3d11.dll", b, install.TransactionId, storage.RootPath, bIdentity.Sha256),
                RestoreFile("dxgi.dll", c, install.TransactionId, storage.RootPath, cIdentity.Sha256)
            }
        });

        Assert.Equal(TransactionOutcome.Success, restore.Outcome);
        Assert.Equal("old-a", File.ReadAllText(a));
        Assert.Equal("old-b", File.ReadAllText(b));
        Assert.Equal("old-c", File.ReadAllText(c));
    }

    [Fact]
    public void Restore_MultiFile_InvalidLaterBackup_RefusesBeforeAnyApply_PreservesGameFilesAndBackups()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        // 1. Originally absent file (would be deleted on restore if applied)
        var a = game.CreateFile("dxgi.dll", "deployed-dxgi");

        // 2. File with valid backup
        var b = game.CreateFile("d3d11.dll", "deployed-d3d11");
        var bBackup = Path.Combine(storage.RootPath, "backups", "tx1", "d3d11.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(bBackup)!);
        File.WriteAllText(bBackup, "valid-original-d3d11");
        var bValidSha = FileIdentity.Capture(bBackup).Sha256;

        // 3. File with invalid backup (corrupted on disk)
        var c = game.CreateFile("dxvk.conf", "deployed-dxvk-conf");
        var cBackup = Path.Combine(storage.RootPath, "backups", "tx1", "dxvk.conf");
        File.WriteAllText(cBackup, "corrupted-conf-backup");
        var cExpectedOriginalSha = new string('e', 64); // does not match backup

        var engine = new MultiFileTransactionEngine(storage.RootPath);

        var restoreResult = engine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Restore,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(a),
                    OriginalState = OriginalFileState.DidNotExist,
                    ExpectedManagedSha256 = FileIdentity.Capture(a).Sha256
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(b),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = Path.GetRelativePath(storage.RootPath, bBackup),
                    ExpectedOriginalSha256 = bValidSha,
                    ExpectedManagedSha256 = FileIdentity.Capture(b).Sha256
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxvk.conf",
                    ExpectedTargetIdentity = FileIdentity.Capture(c),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = Path.GetRelativePath(storage.RootPath, cBackup),
                    ExpectedOriginalSha256 = cExpectedOriginalSha,
                    ExpectedManagedSha256 = FileIdentity.Capture(c).Sha256
                }
            }
        });

        Assert.Equal(TransactionOutcome.SafeFailure, restoreResult.Outcome);
        Assert.Equal("deployed-dxgi", File.ReadAllText(a));
        Assert.Equal("deployed-d3d11", File.ReadAllText(b));
        Assert.Equal("deployed-dxvk-conf", File.ReadAllText(c));
        Assert.Equal("valid-original-d3d11", File.ReadAllText(bBackup));
        Assert.Equal("corrupted-conf-backup", File.ReadAllText(cBackup));
    }

    [Fact]
    public void Restore_NullManagedHashEvidence_RefusesSafely_CannotAuthorizeDeletionOrOverwrite()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        // 1. Originally absent file (would be deleted if restore was authorized)
        var a = game.CreateFile("dxgi.dll", "deployed-dxgi");

        // 2. File with valid original backup (would be overwritten if restore was authorized)
        var b = game.CreateFile("d3d11.dll", "deployed-d3d11");
        var bBackup = Path.Combine(storage.RootPath, "backups", "tx1", "d3d11.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(bBackup)!);
        File.WriteAllText(bBackup, "valid-original-d3d11");
        var bValidSha = FileIdentity.Capture(bBackup).Sha256;

        var engine = new MultiFileTransactionEngine(storage.RootPath);

        // Attempt restore with ExpectedManagedSha256 = null on both files
        var restoreResult = engine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Restore,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(a),
                    OriginalState = OriginalFileState.DidNotExist,
                    ExpectedManagedSha256 = null
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(b),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = Path.GetRelativePath(storage.RootPath, bBackup),
                    ExpectedOriginalSha256 = bValidSha,
                    ExpectedManagedSha256 = null
                }
            }
        });

        Assert.Equal(TransactionOutcome.SafeFailure, restoreResult.Outcome);
        Assert.Contains("Managed hash baseline is missing or malformed for restore: dxgi.dll", restoreResult.Message);

        // Invariant: null evidence cannot authorize deletion
        Assert.True(File.Exists(a));
        Assert.Equal("deployed-dxgi", File.ReadAllText(a));

        // Invariant: null evidence cannot authorize overwrite
        Assert.True(File.Exists(b));
        Assert.Equal("deployed-d3d11", File.ReadAllText(b));
        Assert.Equal("valid-original-d3d11", File.ReadAllText(bBackup));
    }

    [Fact]
    public void RepeatedUpdateFailure_PreservesSuppliedNativeBackupAndPreUpdateTargets()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        var d3d11Target = game.CreateFile("d3d11.dll", "native-d3d11");
        var dxgiTarget = Path.Combine(game.RootPath, "dxgi.dll");
        var nativeD3d11Identity = FileIdentity.Capture(d3d11Target);

        var v1D3d11 = game.CreateFile("v1-d3d11.dll", "dxvk-v1-d3d11");
        var v1Dxgi = game.CreateFile("v1-dxgi.dll", "dxvk-v1-dxgi");
        var v2D3d11 = game.CreateFile("v2-d3d11.dll", "dxvk-v2-d3d11");
        var v2Dxgi = game.CreateFile("v2-dxgi.dll", "dxvk-v2-dxgi");

        var cleanEngine = new MultiFileTransactionEngine(storage.RootPath);
        var backupRelD3d11 = Path.Combine("install-1", "d3d11.dll");

        var installResult = cleanEngine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Install,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = v1D3d11,
                    ExpectedSourceIdentity = FileIdentity.Capture(v1D3d11),
                    ExpectedTargetIdentity = nativeD3d11Identity,
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = backupRelD3d11
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = v1Dxgi,
                    ExpectedSourceIdentity = FileIdentity.Capture(v1Dxgi),
                    ExpectedTargetIdentity = null,
                    OriginalState = OriginalFileState.DidNotExist,
                    BackupRelativePath = null
                }
            }
        });

        Assert.Equal(TransactionOutcome.Success, installResult.Outcome);
        var v1D3d11Identity = FileIdentity.Capture(d3d11Target);
        var v1DxgiIdentity = FileIdentity.Capture(dxgiTarget);
        var nativeBackupFullPath = Path.Combine(storage.RootPath, backupRelD3d11);
        Assert.Equal(nativeD3d11Identity, FileIdentity.Capture(nativeBackupFullPath));

        var failingUpdateEngine = new MultiFileTransactionEngine(storage.RootPath, new MultiFileTransactionTestHooks
        {
            AfterApply = (_, index) =>
            {
                if (index == 1)
                    throw new IOException("synthetic failure on second file during update");
            }
        });

        var failedUpdate = failingUpdateEngine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Update,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = v2D3d11,
                    ExpectedSourceIdentity = FileIdentity.Capture(v2D3d11),
                    ExpectedTargetIdentity = v1D3d11Identity,
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = backupRelD3d11
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = v2Dxgi,
                    ExpectedSourceIdentity = FileIdentity.Capture(v2Dxgi),
                    ExpectedTargetIdentity = v1DxgiIdentity,
                    OriginalState = OriginalFileState.DidNotExist,
                    BackupRelativePath = null
                }
            }
        });

        Assert.Equal(TransactionState.FailedSafely, failedUpdate.State);
        Assert.Equal(TransactionOutcome.SafeFailure, failedUpdate.Outcome);
        Assert.Equal(v1D3d11Identity, FileIdentity.Capture(d3d11Target));
        Assert.True(File.Exists(dxgiTarget));
        Assert.Equal(v1DxgiIdentity, FileIdentity.Capture(dxgiTarget));
        Assert.Equal(nativeD3d11Identity, FileIdentity.Capture(nativeBackupFullPath));
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

    private static MultiFileTransactionFile RestoreFile(string relativePath, string target, string transactionId, string storageRoot, string? expectedOriginalSha256 = null, string? expectedManagedSha256 = null)
    {
        var backup = Path.Combine(storageRoot, "backups", transactionId, relativePath);
        return new MultiFileTransactionFile
        {
            RelativePath = relativePath,
            ExpectedTargetIdentity = FileIdentity.Capture(target),
            OriginalState = OriginalFileState.Existing,
            BackupRelativePath = Path.GetRelativePath(storageRoot, backup),
            ExpectedOriginalSha256 = expectedOriginalSha256,
            ExpectedManagedSha256 = expectedManagedSha256 ?? FileIdentity.Capture(target).Sha256
        };
    }
}
