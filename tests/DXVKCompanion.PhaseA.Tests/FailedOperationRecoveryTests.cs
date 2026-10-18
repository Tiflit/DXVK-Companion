using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DXVKCompanion.Safety;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class FailedOperationRecoveryTests
{
    private sealed record FileSnapshot(byte[] Bytes, string Content, SafetyFileIdentity Identity);

    private static IReadOnlyDictionary<string, FileSnapshot> CaptureSnapshot(string rootPath)
    {
        var result = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(rootPath))
            return result;

        foreach (var file in Directory.GetFiles(rootPath, "*", SearchOption.AllDirectories)
                     .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
            var bytes = File.ReadAllBytes(file);
            var text = File.ReadAllText(file);
            var identity = FileIdentity.Capture(file);
            result[rel] = new FileSnapshot(bytes, text, identity);
        }

        return result;
    }

    private static void AssertSnapshotsEqual(
        IReadOnlyDictionary<string, FileSnapshot> expected,
        IReadOnlyDictionary<string, FileSnapshot> actual)
    {
        var expectedKeys = expected.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        var actualKeys = actual.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(expectedKeys, actualKeys);

        foreach (var key in expectedKeys)
        {
            Assert.True(actual.TryGetValue(key, out var actualEntry), $"Missing expected path '{key}'.");
            var expectedEntry = expected[key];
            Assert.Equal(expectedEntry.Content, actualEntry!.Content);
            Assert.Equal(expectedEntry.Bytes, actualEntry.Bytes);
            Assert.Equal(expectedEntry.Identity, actualEntry.Identity);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedUpdate_AllOriginallyExisting_RecoversVersionA_PreservesNativeBackups_AndRestoresNative(int failIndex)
    {
        using var game = new SyntheticTestDirectory();
        using var source = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        // Setup initial native game files + sentinel
        var d3d11Target = game.CreateFile("d3d11.dll", "native-original-d3d11");
        var dxgiTarget = game.CreateFile("dxgi.dll", "native-original-dxgi");
        var confTarget = game.CreateFile("dxvk.conf", "native-original-conf");
        game.CreateFile("Game.exe", "native-game-exe-sentinel");
        game.CreateFile("data/sentinel.pak", "untouched-game-asset");
        var initialNativeGameSnapshot = CaptureSnapshot(game.RootPath);

        // Setup unrelated sentinel in backup store
        storage.CreateFile("sentinel-backup/other.dll", "unrelated-backup-sentinel");

        // Setup source payloads for Version A and Version B
        var srcAD3d11 = source.CreateFile("vA/d3d11.dll", "dxvk-version-A-d3d11");
        var srcADxgi = source.CreateFile("vA/dxgi.dll", "dxvk-version-A-dxgi");
        var srcAConf = source.CreateFile("vA/dxvk.conf", "dxvk-version-A-conf");
        var srcBD3d11 = source.CreateFile("vB/d3d11.dll", "dxvk-version-B-d3d11");
        var srcBDxgi = source.CreateFile("vB/dxgi.dll", "dxvk-version-B-dxgi");
        var srcBConf = source.CreateFile("vB/dxvk.conf", "dxvk-version-B-conf");
        var initialSourceSnapshot = CaptureSnapshot(source.RootPath);

        // 1. Install Version A
        var installEngine = new MultiFileTransactionEngine(storage.RootPath);
        var installResult = installEngine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Install,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = srcAD3d11,
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAD3d11),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-all-existing/d3d11.dll"
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = srcADxgi,
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcADxgi),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-all-existing/dxgi.dll"
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxvk.conf",
                    SourceFilePath = srcAConf,
                    ExpectedTargetIdentity = FileIdentity.Capture(confTarget),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAConf),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-all-existing/dxvk.conf"
                }
            }
        });

        Assert.Equal(TransactionState.Committed, installResult.State);
        Assert.Equal(TransactionOutcome.Success, installResult.Outcome);
        Assert.Equal("dxvk-version-A-d3d11", File.ReadAllText(d3d11Target));
        Assert.Equal("dxvk-version-A-dxgi", File.ReadAllText(dxgiTarget));
        Assert.Equal("dxvk-version-A-conf", File.ReadAllText(confTarget));

        var postInstallGameSnapshot = CaptureSnapshot(game.RootPath);
        var postInstallStorageSnapshot = CaptureSnapshot(storage.RootPath);
        Assert.Equal("native-original-d3d11", postInstallStorageSnapshot["inst-all-existing/d3d11.dll"].Content);
        Assert.Equal("native-original-dxgi", postInstallStorageSnapshot["inst-all-existing/dxgi.dll"].Content);
        Assert.Equal("native-original-conf", postInstallStorageSnapshot["inst-all-existing/dxvk.conf"].Content);

        // 2. Fail Update to Version B at failIndex; verify untouched targets are not mutated during recovery
        var recoveredIndices = new List<int>();
        FileStream? untouchedReadLock = null;
        try
        {
            var updateEngine = new MultiFileTransactionEngine(storage.RootPath, new MultiFileTransactionTestHooks
            {
                AfterApply = (_, index) =>
                {
                    if (index == failIndex)
                    {
                        if (failIndex < 2)
                        {
                            // Lock untouched file at index 2 for reading only: proves recovery does not overwrite untouched targets
                            untouchedReadLock = new FileStream(confTarget, FileMode.Open, FileAccess.Read, FileShare.Read);
                        }
                        throw new IOException($"Synthetic update failure at index {failIndex}");
                    }
                },
                DuringRecovery = index => recoveredIndices.Add(index)
            });

            var updateResult = updateEngine.Execute(new MultiFileTransactionRequest
            {
                InstallationRoot = game.RootPath,
                Operation = TransactionOperation.Update,
                Files = new[]
                {
                    new MultiFileTransactionFile
                    {
                        RelativePath = "d3d11.dll",
                        SourceFilePath = srcBD3d11,
                        ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                        ExpectedSourceIdentity = FileIdentity.Capture(srcBD3d11),
                        OriginalState = OriginalFileState.Existing,
                        BackupRelativePath = "inst-all-existing/d3d11.dll"
                    },
                    new MultiFileTransactionFile
                    {
                        RelativePath = "dxgi.dll",
                        SourceFilePath = srcBDxgi,
                        ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                        ExpectedSourceIdentity = FileIdentity.Capture(srcBDxgi),
                        OriginalState = OriginalFileState.Existing,
                        BackupRelativePath = "inst-all-existing/dxgi.dll"
                    },
                    new MultiFileTransactionFile
                    {
                        RelativePath = "dxvk.conf",
                        SourceFilePath = srcBConf,
                        ExpectedTargetIdentity = FileIdentity.Capture(confTarget),
                        ExpectedSourceIdentity = FileIdentity.Capture(srcBConf),
                        OriginalState = OriginalFileState.Existing,
                        BackupRelativePath = "inst-all-existing/dxvk.conf"
                    }
                }
            });

            Assert.Equal(TransactionState.FailedSafely, updateResult.State);
            Assert.Equal(TransactionOutcome.SafeFailure, updateResult.Outcome);
            Assert.Equal(Enumerable.Range(0, failIndex + 1).Reverse().ToArray(), recoveredIndices.ToArray());
        }
        finally
        {
            untouchedReadLock?.Dispose();
        }

        // Entire game directory and backup store snapshots must equal pre-update state
        AssertSnapshotsEqual(postInstallGameSnapshot, CaptureSnapshot(game.RootPath));
        AssertSnapshotsEqual(postInstallStorageSnapshot, CaptureSnapshot(storage.RootPath));
        AssertSnapshotsEqual(initialSourceSnapshot, CaptureSnapshot(source.RootPath));

        // 3. Subsequent Restore recovers the true original native state
        var restoreResult = installEngine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Restore,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-all-existing/d3d11.dll",
                    ExpectedOriginalSha256 = initialNativeGameSnapshot["d3d11.dll"].Identity.Sha256,
                    ExpectedManagedSha256 = postInstallGameSnapshot["d3d11.dll"].Identity.Sha256
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-all-existing/dxgi.dll",
                    ExpectedOriginalSha256 = initialNativeGameSnapshot["dxgi.dll"].Identity.Sha256,
                    ExpectedManagedSha256 = postInstallGameSnapshot["dxgi.dll"].Identity.Sha256
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxvk.conf",
                    ExpectedTargetIdentity = FileIdentity.Capture(confTarget),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-all-existing/dxvk.conf",
                    ExpectedOriginalSha256 = initialNativeGameSnapshot["dxvk.conf"].Identity.Sha256,
                    ExpectedManagedSha256 = postInstallGameSnapshot["dxvk.conf"].Identity.Sha256
                }
            }
        });

        Assert.Equal(TransactionState.Committed, restoreResult.State);
        Assert.Equal(TransactionOutcome.Success, restoreResult.Outcome);
        AssertSnapshotsEqual(initialNativeGameSnapshot, CaptureSnapshot(game.RootPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedUpdate_AllOriginallyAbsent_RecoversVersionA_DoesNotDeleteWorkingManagedFiles_AndRestoresCleanAbsence(int failIndex)
    {
        using var game = new SyntheticTestDirectory();
        using var source = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        game.CreateFile("Game.exe", "native-game-exe-sentinel");
        game.CreateFile("data/sentinel.pak", "untouched-game-asset");
        var initialCleanGameSnapshot = CaptureSnapshot(game.RootPath);

        storage.CreateFile("sentinel-backup/other.dll", "unrelated-backup-sentinel");

        var srcAD3d11 = source.CreateFile("vA/d3d11.dll", "dxvk-version-A-d3d11");
        var srcADxgi = source.CreateFile("vA/dxgi.dll", "dxvk-version-A-dxgi");
        var srcAConf = source.CreateFile("vA/dxvk.conf", "dxvk-version-A-conf");
        var srcBD3d11 = source.CreateFile("vB/d3d11.dll", "dxvk-version-B-d3d11");
        var srcBDxgi = source.CreateFile("vB/dxgi.dll", "dxvk-version-B-dxgi");
        var srcBConf = source.CreateFile("vB/dxvk.conf", "dxvk-version-B-conf");
        var initialSourceSnapshot = CaptureSnapshot(source.RootPath);

        var d3d11Target = game.GetPath("d3d11.dll");
        var dxgiTarget = game.GetPath("dxgi.dll");
        var confTarget = game.GetPath("dxvk.conf");

        // 1. Install Version A into clean game directory
        var installEngine = new MultiFileTransactionEngine(storage.RootPath);
        var installResult = installEngine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Install,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = srcAD3d11,
                    ExpectedTargetIdentity = null,
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAD3d11),
                    OriginalState = OriginalFileState.DidNotExist
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = srcADxgi,
                    ExpectedTargetIdentity = null,
                    ExpectedSourceIdentity = FileIdentity.Capture(srcADxgi),
                    OriginalState = OriginalFileState.DidNotExist
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxvk.conf",
                    SourceFilePath = srcAConf,
                    ExpectedTargetIdentity = null,
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAConf),
                    OriginalState = OriginalFileState.DidNotExist
                }
            }
        });

        Assert.Equal(TransactionState.Committed, installResult.State);
        Assert.Equal(TransactionOutcome.Success, installResult.Outcome);
        var postInstallGameSnapshot = CaptureSnapshot(game.RootPath);
        var postInstallStorageSnapshot = CaptureSnapshot(storage.RootPath);

        // 2. Fail Update to Version B at failIndex
        var updateEngine = new MultiFileTransactionEngine(storage.RootPath, new MultiFileTransactionTestHooks
        {
            AfterApply = (_, index) =>
            {
                if (index == failIndex)
                    throw new IOException($"Synthetic update failure at index {failIndex}");
            }
        });

        var updateResult = updateEngine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Update,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = srcBD3d11,
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcBD3d11),
                    OriginalState = OriginalFileState.DidNotExist
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = srcBDxgi,
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcBDxgi),
                    OriginalState = OriginalFileState.DidNotExist
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxvk.conf",
                    SourceFilePath = srcBConf,
                    ExpectedTargetIdentity = FileIdentity.Capture(confTarget),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcBConf),
                    OriginalState = OriginalFileState.DidNotExist
                }
            }
        });

        Assert.Equal(TransactionState.FailedSafely, updateResult.State);
        Assert.Equal(TransactionOutcome.SafeFailure, updateResult.Outcome);

        // Working Version A files must remain intact, not deleted!
        AssertSnapshotsEqual(postInstallGameSnapshot, CaptureSnapshot(game.RootPath));
        AssertSnapshotsEqual(postInstallStorageSnapshot, CaptureSnapshot(storage.RootPath));
        AssertSnapshotsEqual(initialSourceSnapshot, CaptureSnapshot(source.RootPath));

        // 3. Subsequent Restore removes all originally absent DXVK files
        var restoreResult = installEngine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Restore,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    OriginalState = OriginalFileState.DidNotExist,
                    ExpectedManagedSha256 = postInstallGameSnapshot["d3d11.dll"].Identity.Sha256
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    OriginalState = OriginalFileState.DidNotExist,
                    ExpectedManagedSha256 = postInstallGameSnapshot["dxgi.dll"].Identity.Sha256
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxvk.conf",
                    ExpectedTargetIdentity = FileIdentity.Capture(confTarget),
                    OriginalState = OriginalFileState.DidNotExist,
                    ExpectedManagedSha256 = postInstallGameSnapshot["dxvk.conf"].Identity.Sha256
                }
            }
        });

        Assert.Equal(TransactionState.Committed, restoreResult.State);
        Assert.Equal(TransactionOutcome.Success, restoreResult.Outcome);
        AssertSnapshotsEqual(initialCleanGameSnapshot, CaptureSnapshot(game.RootPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedUpdate_MixedDllAndConfigStates_IncludingNewlyAddedConfigOnUpdate_RecoversExactVersionA_AndRestoresNative(int failIndex)
    {
        using var game = new SyntheticTestDirectory();
        using var source = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        // Game starts with native d3d11.dll, absent dxgi.dll, absent dxvk.conf
        var d3d11Target = game.CreateFile("d3d11.dll", "native-mixed-d3d11");
        var dxgiTarget = game.GetPath("dxgi.dll");
        var confTarget = game.GetPath("dxvk.conf");
        game.CreateFile("Game.exe", "native-game-exe");
        var initialNativeGameSnapshot = CaptureSnapshot(game.RootPath);

        var srcAD3d11 = source.CreateFile("vA/d3d11.dll", "dxvk-A-mixed-d3d11");
        var srcADxgi = source.CreateFile("vA/dxgi.dll", "dxvk-A-mixed-dxgi");
        var srcBD3d11 = source.CreateFile("vB/d3d11.dll", "dxvk-B-mixed-d3d11");
        var srcBDxgi = source.CreateFile("vB/dxgi.dll", "dxvk-B-mixed-dxgi");
        var srcBConf = source.CreateFile("vB/dxvk.conf", "dxvk.hud = fps\n");

        // 1. Install Version A (without dxvk.conf)
        var installEngine = new MultiFileTransactionEngine(storage.RootPath);
        var installResult = installEngine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Install,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = srcAD3d11,
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAD3d11),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-mixed/d3d11.dll"
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = srcADxgi,
                    ExpectedTargetIdentity = null,
                    ExpectedSourceIdentity = FileIdentity.Capture(srcADxgi),
                    OriginalState = OriginalFileState.DidNotExist
                }
            }
        });

        Assert.Equal(TransactionState.Committed, installResult.State);
        Assert.Equal(TransactionOutcome.Success, installResult.Outcome);
        Assert.False(File.Exists(confTarget));
        var postInstallGameSnapshot = CaptureSnapshot(game.RootPath);
        var postInstallStorageSnapshot = CaptureSnapshot(storage.RootPath);

        // 2. Fail Update to Version B (which also introduces dxvk.conf for the first time) at failIndex
        var updateEngine = new MultiFileTransactionEngine(storage.RootPath, new MultiFileTransactionTestHooks
        {
            AfterApply = (_, index) =>
            {
                if (index == failIndex)
                    throw new IOException($"Synthetic mixed update failure at index {failIndex}");
            }
        });

        var updateResult = updateEngine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Update,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = srcBD3d11,
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcBD3d11),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-mixed/d3d11.dll"
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = srcBDxgi,
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcBDxgi),
                    OriginalState = OriginalFileState.DidNotExist
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxvk.conf",
                    SourceFilePath = srcBConf,
                    ExpectedTargetIdentity = null,
                    ExpectedSourceIdentity = FileIdentity.Capture(srcBConf),
                    OriginalState = OriginalFileState.DidNotExist
                }
            }
        });

        Assert.Equal(TransactionState.FailedSafely, updateResult.State);
        Assert.Equal(TransactionOutcome.SafeFailure, updateResult.Outcome);

        // Target and backup snapshots must match post-Install-A state (d3d11.dll & dxgi.dll on vA, dxvk.conf absent)
        AssertSnapshotsEqual(postInstallGameSnapshot, CaptureSnapshot(game.RootPath));
        AssertSnapshotsEqual(postInstallStorageSnapshot, CaptureSnapshot(storage.RootPath));

        // 3. Restore returns native d3d11.dll and deletes dxgi.dll
        var restoreResult = installEngine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Restore,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-mixed/d3d11.dll",
                    ExpectedOriginalSha256 = initialNativeGameSnapshot["d3d11.dll"].Identity.Sha256,
                    ExpectedManagedSha256 = postInstallGameSnapshot["d3d11.dll"].Identity.Sha256
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    OriginalState = OriginalFileState.DidNotExist,
                    ExpectedManagedSha256 = postInstallGameSnapshot["dxgi.dll"].Identity.Sha256
                }
            }
        });

        Assert.Equal(TransactionState.Committed, restoreResult.State);
        Assert.Equal(TransactionOutcome.Success, restoreResult.Outcome);
        AssertSnapshotsEqual(initialNativeGameSnapshot, CaptureSnapshot(game.RootPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedReapply_WithValidSuppliedBaseline_RecoversExactPreReapplyState_PreservesNativeBackups_AndRestoresNative(int failIndex)
    {
        using var game = new SyntheticTestDirectory();
        using var source = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        var d3d11Target = game.CreateFile("d3d11.dll", "native-reapply-d3d11");
        var dxgiTarget = game.GetPath("dxgi.dll");
        var confTarget = game.CreateFile("dxvk.conf", "native-reapply-conf");
        game.CreateFile("Game.exe", "native-exe");
        var initialNativeGameSnapshot = CaptureSnapshot(game.RootPath);

        var srcAD3d11 = source.CreateFile("vA/d3d11.dll", "dxvk-A-d3d11");
        var srcADxgi = source.CreateFile("vA/dxgi.dll", "dxvk-A-dxgi");
        var srcAConf = source.CreateFile("vA/dxvk.conf", "dxvk-A-conf-merged");

        // 1. Initial Install A
        var engine = new MultiFileTransactionEngine(storage.RootPath);
        var installResult = engine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Install,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = srcAD3d11,
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAD3d11),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-reapply/d3d11.dll"
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = srcADxgi,
                    ExpectedTargetIdentity = null,
                    ExpectedSourceIdentity = FileIdentity.Capture(srcADxgi),
                    OriginalState = OriginalFileState.DidNotExist
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxvk.conf",
                    SourceFilePath = srcAConf,
                    ExpectedTargetIdentity = FileIdentity.Capture(confTarget),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAConf),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-reapply/dxvk.conf"
                }
            }
        });

        Assert.Equal(TransactionOutcome.Success, installResult.Outcome);

        // Simulate a modified pre-reapply target state (e.g., user edited conf or external patch modified d3d11.dll while keeping native baseline)
        File.WriteAllText(d3d11Target, "pre-reapply-modified-d3d11");
        File.WriteAllText(confTarget, "pre-reapply-modified-conf");
        var preReapplyGameSnapshot = CaptureSnapshot(game.RootPath);
        var preReapplyStorageSnapshot = CaptureSnapshot(storage.RootPath);

        // 2. Fail Reapply at failIndex
        var failingEngine = new MultiFileTransactionEngine(storage.RootPath, new MultiFileTransactionTestHooks
        {
            AfterApply = (_, index) =>
            {
                if (index == failIndex)
                    throw new IOException($"Synthetic reapply failure at index {failIndex}");
            }
        });

        var reapplyResult = failingEngine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Reapply,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = srcAD3d11,
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAD3d11),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-reapply/d3d11.dll"
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = srcADxgi,
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcADxgi),
                    OriginalState = OriginalFileState.DidNotExist
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxvk.conf",
                    SourceFilePath = srcAConf,
                    ExpectedTargetIdentity = FileIdentity.Capture(confTarget),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAConf),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-reapply/dxvk.conf"
                }
            }
        });

        Assert.Equal(TransactionState.FailedSafely, reapplyResult.State);
        Assert.Equal(TransactionOutcome.SafeFailure, reapplyResult.Outcome);
        AssertSnapshotsEqual(preReapplyGameSnapshot, CaptureSnapshot(game.RootPath));
        AssertSnapshotsEqual(preReapplyStorageSnapshot, CaptureSnapshot(storage.RootPath));

        // 3. Succeed Reapply, then Restore true native state
        var reapplySuccess = engine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Reapply,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = srcAD3d11,
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAD3d11),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-reapply/d3d11.dll"
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = srcADxgi,
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcADxgi),
                    OriginalState = OriginalFileState.DidNotExist
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxvk.conf",
                    SourceFilePath = srcAConf,
                    ExpectedTargetIdentity = FileIdentity.Capture(confTarget),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAConf),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-reapply/dxvk.conf"
                }
            }
        });
        Assert.Equal(TransactionOutcome.Success, reapplySuccess.Outcome);

        var restoreResult = engine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Restore,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-reapply/d3d11.dll",
                    ExpectedOriginalSha256 = initialNativeGameSnapshot["d3d11.dll"].Identity.Sha256,
                    ExpectedManagedSha256 = FileIdentity.Capture(srcAD3d11).Sha256
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    OriginalState = OriginalFileState.DidNotExist,
                    ExpectedManagedSha256 = FileIdentity.Capture(srcADxgi).Sha256
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxvk.conf",
                    ExpectedTargetIdentity = FileIdentity.Capture(confTarget),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-reapply/dxvk.conf",
                    ExpectedOriginalSha256 = initialNativeGameSnapshot["dxvk.conf"].Identity.Sha256,
                    ExpectedManagedSha256 = FileIdentity.Capture(srcAConf).Sha256
                }
            }
        });

        Assert.Equal(TransactionOutcome.Success, restoreResult.Outcome);
        AssertSnapshotsEqual(initialNativeGameSnapshot, CaptureSnapshot(game.RootPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FailedRestore_AfterEarlyTargetMutation_RecoversManagedVersionAIncludingOriginallyAbsentFiles_ThenSubsequentRestoreSucceeds(int failIndex)
    {
        using var game = new SyntheticTestDirectory();
        using var source = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        // Game starts with native d3d11.dll; dxgi.dll and dxvk.conf are originally absent
        var d3d11Target = game.CreateFile("d3d11.dll", "native-restore-test-d3d11");
        var dxgiTarget = game.GetPath("dxgi.dll");
        var confTarget = game.GetPath("dxvk.conf");
        game.CreateFile("Game.exe", "native-exe");
        var initialNativeGameSnapshot = CaptureSnapshot(game.RootPath);

        var srcAD3d11 = source.CreateFile("vA/d3d11.dll", "dxvk-A-restore-d3d11");
        var srcADxgi = source.CreateFile("vA/dxgi.dll", "dxvk-A-restore-dxgi");
        var srcAConf = source.CreateFile("vA/dxvk.conf", "dxvk-A-restore-conf");

        // 1. Install Version A
        var engine = new MultiFileTransactionEngine(storage.RootPath);
        var installResult = engine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Install,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = srcADxgi,
                    ExpectedTargetIdentity = null,
                    ExpectedSourceIdentity = FileIdentity.Capture(srcADxgi),
                    OriginalState = OriginalFileState.DidNotExist
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = srcAD3d11,
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAD3d11),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-restore/d3d11.dll"
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxvk.conf",
                    SourceFilePath = srcAConf,
                    ExpectedTargetIdentity = null,
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAConf),
                    OriginalState = OriginalFileState.DidNotExist
                }
            }
        });

        Assert.Equal(TransactionOutcome.Success, installResult.Outcome);
        var postInstallGameSnapshot = CaptureSnapshot(game.RootPath);
        var postInstallStorageSnapshot = CaptureSnapshot(storage.RootPath);

        // 2. Fail Restore at failIndex (0 = after deleting originally absent dxgi.dll; 1 = after also overwriting d3d11.dll with native)
        bool shouldFail = true;
        var toggleEngine = new MultiFileTransactionEngine(storage.RootPath, new MultiFileTransactionTestHooks
        {
            AfterApply = (_, index) =>
            {
                if (shouldFail && index == failIndex)
                    throw new IOException($"Synthetic restore failure at index {failIndex}");
            }
        });

        var restoreRequest = new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Restore,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    OriginalState = OriginalFileState.DidNotExist,
                    ExpectedManagedSha256 = postInstallGameSnapshot["dxgi.dll"].Identity.Sha256
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-restore/d3d11.dll",
                    ExpectedOriginalSha256 = initialNativeGameSnapshot["d3d11.dll"].Identity.Sha256,
                    ExpectedManagedSha256 = postInstallGameSnapshot["d3d11.dll"].Identity.Sha256
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxvk.conf",
                    ExpectedTargetIdentity = FileIdentity.Capture(confTarget),
                    OriginalState = OriginalFileState.DidNotExist,
                    ExpectedManagedSha256 = postInstallGameSnapshot["dxvk.conf"].Identity.Sha256
                }
            }
        };

        var failedRestoreResult = toggleEngine.Execute(restoreRequest);
        Assert.Equal(TransactionState.FailedSafely, failedRestoreResult.State);
        Assert.Equal(TransactionOutcome.SafeFailure, failedRestoreResult.Outcome);

        // Managed Version-A bytes and existence must be recovered, including originally absent dxgi.dll!
        AssertSnapshotsEqual(postInstallGameSnapshot, CaptureSnapshot(game.RootPath));
        AssertSnapshotsEqual(postInstallStorageSnapshot, CaptureSnapshot(storage.RootPath));

        // 3. Disable injected failure and run Restore again: must succeed and recover true original native state
        shouldFail = false;
        var succeededRestoreResult = toggleEngine.Execute(restoreRequest);
        Assert.Equal(TransactionState.Committed, succeededRestoreResult.State);
        Assert.Equal(TransactionOutcome.Success, succeededRestoreResult.Outcome);
        AssertSnapshotsEqual(initialNativeGameSnapshot, CaptureSnapshot(game.RootPath));
        AssertSnapshotsEqual(postInstallStorageSnapshot, CaptureSnapshot(storage.RootPath));
    }

    [Fact]
    public void RecoveryFailure_WhenUndoReadOrWriteFails_ReturnsUnresolvedRecovery_AndRetainsOperationEvidenceAcrossLaterTransactions()
    {
        using var game = new SyntheticTestDirectory();
        using var source = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        var d3d11Target = game.CreateFile("d3d11.dll", "dxvk-A-d3d11");
        var dxgiTarget = game.CreateFile("dxgi.dll", "dxvk-A-dxgi");
        storage.CreateFile("inst-unresolved/d3d11.dll", "native-d3d11-baseline");
        storage.CreateFile("sibling.recovery.json", "{\"preserved\":true}");

        var srcBD3d11 = source.CreateFile("vB/d3d11.dll", "dxvk-B-d3d11");
        var srcBDxgi = source.CreateFile("vB/dxgi.dll", "dxvk-B-dxgi");

        // Hold target locked read-only during recovery so the undo write in RecoverAttempted fails with IOException
        FileStream? recoveryWriteBlocker = null;
        SafetyTransactionResult unresolvedResult;
        try
        {
            var engine = new MultiFileTransactionEngine(storage.RootPath, new MultiFileTransactionTestHooks
            {
                AfterApply = (_, index) =>
                {
                    if (index == 0)
                        throw new IOException("Synthetic apply failure after mutating first file");
                },
                BeforeRecovery = _ =>
                {
                    recoveryWriteBlocker = new FileStream(d3d11Target, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            });

            unresolvedResult = engine.Execute(new MultiFileTransactionRequest
            {
                InstallationRoot = game.RootPath,
                Operation = TransactionOperation.Update,
                Files = new[]
                {
                    new MultiFileTransactionFile
                    {
                        RelativePath = "d3d11.dll",
                        SourceFilePath = srcBD3d11,
                        ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                        ExpectedSourceIdentity = FileIdentity.Capture(srcBD3d11),
                        OriginalState = OriginalFileState.Existing,
                        BackupRelativePath = "inst-unresolved/d3d11.dll"
                    },
                    new MultiFileTransactionFile
                    {
                        RelativePath = "dxgi.dll",
                        SourceFilePath = srcBDxgi,
                        ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                        ExpectedSourceIdentity = FileIdentity.Capture(srcBDxgi),
                        OriginalState = OriginalFileState.DidNotExist
                    }
                }
            });
        }
        finally
        {
            recoveryWriteBlocker?.Dispose();
        }

        Assert.Equal(TransactionState.AttentionRequired, unresolvedResult.State);
        Assert.Equal(TransactionOutcome.UnresolvedRecovery, unresolvedResult.Outcome);

        // Operation-owned evidence (plan + undo snapshot of pre-update Version A) must be retained in the isolated operation namespace
        var retainedOpDir = Path.Combine(storage.RootPath, ".transactions", unresolvedResult.TransactionId);
        Assert.True(Directory.Exists(retainedOpDir), "Expected unresolved operation directory to be retained.");
        Assert.True(File.Exists(Path.Combine(retainedOpDir, "plan.json")), "Expected plan.json to be retained on unresolved recovery.");
        var retainedUndoD3d11 = Path.Combine(retainedOpDir, "undo", "d3d11.dll");
        Assert.True(File.Exists(retainedUndoD3d11), "Expected undo snapshot for d3d11.dll to be retained on unresolved recovery.");
        Assert.Equal("dxvk-A-d3d11", File.ReadAllText(retainedUndoD3d11));

        // Native backup and sibling recovery files must remain intact
        Assert.Equal("native-d3d11-baseline", File.ReadAllText(storage.GetPath("inst-unresolved/d3d11.dll")));
        Assert.Equal("{\"preserved\":true}", File.ReadAllText(storage.GetPath("sibling.recovery.json")));

        // A subsequent successful operation must clean up ONLY its own operation artifacts and preserve the earlier unresolved evidence
        var cleanEngine = new MultiFileTransactionEngine(storage.RootPath);
        var secondResult = cleanEngine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Update,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = srcBDxgi,
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcBDxgi),
                    OriginalState = OriginalFileState.DidNotExist
                }
            }
        });

        Assert.Equal(TransactionOutcome.Success, secondResult.Outcome);
        Assert.False(Directory.Exists(Path.Combine(storage.RootPath, ".transactions", secondResult.TransactionId)));
        Assert.True(Directory.Exists(retainedOpDir), "Earlier unresolved operation evidence must not be deleted by later operations.");
        Assert.Equal("dxvk-A-d3d11", File.ReadAllText(retainedUndoD3d11));
    }

    [Fact]
    public void FirstFileCopyException_BeforeAfterApplyHook_IsClassifiedAsAttempted_AndEntersRecoveryInsteadOfSafeAbort()
    {
        using var game = new SyntheticTestDirectory();
        using var source = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        var d3d11Target = game.CreateFile("d3d11.dll", "dxvk-A-d3d11");
        var dxgiTarget = game.CreateFile("dxgi.dll", "dxvk-A-dxgi");
        storage.CreateFile("inst-first-copy/d3d11.dll", "native-d3d11");

        var srcBD3d11 = source.CreateFile("vB/d3d11.dll", "dxvk-B-d3d11");
        var srcBDxgi = source.CreateFile("vB/dxgi.dll", "dxvk-B-dxgi");

        var preUpdateGameSnapshot = CaptureSnapshot(game.RootPath);
        var preUpdateStorageSnapshot = CaptureSnapshot(storage.RootPath);

        // Hold a read-only lock on d3d11Target so validation/undo-capture (reads) succeed,
        // but the very first File.Copy in Apply(index: 0) throws IOException BEFORE AfterApply is reached.
        FileStream? firstCopyWriteBlocker = new FileStream(d3d11Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        bool beforeRecoveryCalled = false;
        var recoveredIndices = new List<int>();
        try
        {
            var engine = new MultiFileTransactionEngine(storage.RootPath, new MultiFileTransactionTestHooks
            {
                BeforeRecovery = _ =>
                {
                    beforeRecoveryCalled = true;
                    firstCopyWriteBlocker?.Dispose();
                    firstCopyWriteBlocker = null;
                },
                DuringRecovery = index => recoveredIndices.Add(index)
            });

            var result = engine.Execute(new MultiFileTransactionRequest
            {
                InstallationRoot = game.RootPath,
                Operation = TransactionOperation.Update,
                Files = new[]
                {
                    new MultiFileTransactionFile
                    {
                        RelativePath = "d3d11.dll",
                        SourceFilePath = srcBD3d11,
                        ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                        ExpectedSourceIdentity = FileIdentity.Capture(srcBD3d11),
                        OriginalState = OriginalFileState.Existing,
                        BackupRelativePath = "inst-first-copy/d3d11.dll"
                    },
                    new MultiFileTransactionFile
                    {
                        RelativePath = "dxgi.dll",
                        SourceFilePath = srcBDxgi,
                        ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                        ExpectedSourceIdentity = FileIdentity.Capture(srcBDxgi),
                        OriginalState = OriginalFileState.DidNotExist
                    }
                }
            });

            Assert.True(beforeRecoveryCalled, "First-file Apply exception must trigger recovery rather than returning SafeAbort.");
            Assert.Equal(new[] { 0 }, recoveredIndices.ToArray());
            Assert.Equal(TransactionState.FailedSafely, result.State);
            Assert.Equal(TransactionOutcome.SafeFailure, result.Outcome);
        }
        finally
        {
            firstCopyWriteBlocker?.Dispose();
        }

        AssertSnapshotsEqual(preUpdateGameSnapshot, CaptureSnapshot(game.RootPath));
        AssertSnapshotsEqual(preUpdateStorageSnapshot, CaptureSnapshot(storage.RootPath));
    }

    [Fact]
    public void ValidationRefusalBeforeTargetMutation_PreservesTargetAndStorageSnapshotsWithoutStrayBackups()
    {
        using var game = new SyntheticTestDirectory();
        using var source = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        var d3d11Target = game.CreateFile("d3d11.dll", "native-d3d11");
        var dxgiTarget = game.CreateFile("dxgi.dll", "native-dxgi");
        storage.CreateFile("sentinel.txt", "keep-me");

        var srcAD3d11 = source.CreateFile("vA/d3d11.dll", "dxvk-A-d3d11");
        var srcADxgi = source.CreateFile("vA/dxgi.dll", "dxvk-A-dxgi");

        var initialGameSnapshot = CaptureSnapshot(game.RootPath);
        var initialStorageSnapshot = CaptureSnapshot(storage.RootPath);

        // File 0 is valid, File 1 has mismatched ExpectedTargetIdentity
        var engine = new MultiFileTransactionEngine(storage.RootPath);
        var result = engine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Install,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = srcAD3d11,
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAD3d11),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-abort/d3d11.dll"
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = srcADxgi,
                    ExpectedTargetIdentity = new SafetyFileIdentity(new string('f', 64), 999),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcADxgi),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-abort/dxgi.dll"
                }
            }
        });

        Assert.Equal(TransactionState.Aborted, result.State);
        Assert.Equal(TransactionOutcome.SafeAbort, result.Outcome);
        AssertSnapshotsEqual(initialGameSnapshot, CaptureSnapshot(game.RootPath));
        AssertSnapshotsEqual(initialStorageSnapshot, CaptureSnapshot(storage.RootPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InterruptedNativeBackupPreparation_ThrowOrCorruptStagedBackup_DoesNotPublishPartialBackup_ThenRetryAndRestoreSucceed(bool throwDuringStaging)
    {
        using var game = new SyntheticTestDirectory();
        using var source = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        var d3d11Target = game.CreateFile("d3d11.dll", "native-d3d11-full-payload");
        var dxgiTarget = game.CreateFile("dxgi.dll", "native-dxgi-full-payload");
        game.CreateFile("Game.exe", "native-exe-sentinel");
        var initialGameSnapshot = CaptureSnapshot(game.RootPath);

        storage.CreateFile("existing-baseline/other.dll", "preserved-existing-backup");
        storage.CreateFile(".transactions/earlier-tx/plan.json", "{\"unresolved\":true}");
        var initialStorageSnapshot = CaptureSnapshot(storage.RootPath);

        var srcAD3d11 = source.CreateFile("vA/d3d11.dll", "dxvk-A-d3d11");
        var srcADxgi = source.CreateFile("vA/dxgi.dll", "dxvk-A-dxgi");

        var installRequest = new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Install,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = srcAD3d11,
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAD3d11),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-interrupted/d3d11.dll"
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = srcADxgi,
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcADxgi),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-interrupted/dxgi.dll"
                }
            }
        };

        // 1. Interrupt native-backup staging on second file (after first file was staged and published in this attempt)
        var interruptedEngine = new MultiFileTransactionEngine(storage.RootPath, new MultiFileTransactionTestHooks
        {
            AfterStageNativeBackup = (stagedPath, _, index) =>
            {
                if (index == 1)
                {
                    File.WriteAllText(stagedPath, "partial-corrupted-native-bytes");
                    if (throwDuringStaging)
                        throw new IOException("Simulated I/O interruption while staging native backup");
                }
            }
        });

        var interruptedResult = interruptedEngine.Execute(installRequest);
        if (throwDuringStaging)
        {
            Assert.Equal(TransactionState.Aborted, interruptedResult.State);
            Assert.Equal(TransactionOutcome.SafeAbort, interruptedResult.Outcome);
        }
        else
        {
            Assert.Equal(TransactionState.FailedSafely, interruptedResult.State);
            Assert.Equal(TransactionOutcome.SafeFailure, interruptedResult.Outcome);
        }

        // Neither partial backup nor earlier file's backup from the interrupted attempt may remain at final paths;
        // pre-existing backups and earlier unresolved evidence must remain intact.
        Assert.False(File.Exists(storage.GetPath("inst-interrupted/d3d11.dll")));
        Assert.False(File.Exists(storage.GetPath("inst-interrupted/dxgi.dll")));
        AssertSnapshotsEqual(initialGameSnapshot, CaptureSnapshot(game.RootPath));
        AssertSnapshotsEqual(initialStorageSnapshot, CaptureSnapshot(storage.RootPath));

        // 2. Retry Install with a clean engine: must stage and publish full verified native backups and deploy DXVK
        var cleanEngine = new MultiFileTransactionEngine(storage.RootPath);
        var retryResult = cleanEngine.Execute(installRequest);
        Assert.Equal(TransactionState.Committed, retryResult.State);
        Assert.Equal(TransactionOutcome.Success, retryResult.Outcome);

        Assert.Equal("dxvk-A-d3d11", File.ReadAllText(d3d11Target));
        Assert.Equal("dxvk-A-dxgi", File.ReadAllText(dxgiTarget));
        Assert.Equal(initialGameSnapshot["d3d11.dll"].Identity, FileIdentity.Capture(storage.GetPath("inst-interrupted/d3d11.dll")));
        Assert.Equal(initialGameSnapshot["dxgi.dll"].Identity, FileIdentity.Capture(storage.GetPath("inst-interrupted/dxgi.dll")));
        Assert.Equal("preserved-existing-backup", File.ReadAllText(storage.GetPath("existing-baseline/other.dll")));
        Assert.Equal("{\"unresolved\":true}", File.ReadAllText(storage.GetPath(".transactions/earlier-tx/plan.json")));

        // 3. Subsequent Restore returns to exact initial native game state
        var restoreResult = cleanEngine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Restore,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-interrupted/d3d11.dll",
                    ExpectedOriginalSha256 = initialGameSnapshot["d3d11.dll"].Identity.Sha256,
                    ExpectedManagedSha256 = FileIdentity.Capture(srcAD3d11).Sha256
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-interrupted/dxgi.dll",
                    ExpectedOriginalSha256 = initialGameSnapshot["dxgi.dll"].Identity.Sha256,
                    ExpectedManagedSha256 = FileIdentity.Capture(srcADxgi).Sha256
                }
            }
        });

        Assert.Equal(TransactionState.Committed, restoreResult.State);
        Assert.Equal(TransactionOutcome.Success, restoreResult.Outcome);
        AssertSnapshotsEqual(initialGameSnapshot, CaptureSnapshot(game.RootPath));
    }

    [Fact]
    public void NativeBackupPublicationCollision_AbortsWithoutDeletingOrAdoptingCompetingDestination_AndPreservesGameAndStorageSentinels()
    {
        using var game = new SyntheticTestDirectory();
        using var source = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        var d3d11Target = game.CreateFile("d3d11.dll", "native-d3d11-collision-test");
        var dxgiTarget = game.CreateFile("dxgi.dll", "native-dxgi-collision-test");
        game.CreateFile("Game.exe", "native-exe-sentinel");
        var initialGameSnapshot = CaptureSnapshot(game.RootPath);

        storage.CreateFile("existing-baseline/other.dll", "preserved-existing-backup");
        storage.CreateFile(".transactions/earlier-tx/plan.json", "{\"unresolved\":true}");

        var srcAD3d11 = source.CreateFile("vA/d3d11.dll", "dxvk-A-d3d11");
        var srcADxgi = source.CreateFile("vA/dxgi.dll", "dxvk-A-dxgi");

        var engine = new MultiFileTransactionEngine(storage.RootPath, new MultiFileTransactionTestHooks
        {
            BeforePublishNativeBackup = (_, finalBackupPath, index) =>
            {
                if (index == 1)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(finalBackupPath)!);
                    File.WriteAllText(finalBackupPath, "competing-writer-dxgi-backup");
                }
            }
        });

        var result = engine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Install,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    SourceFilePath = srcAD3d11,
                    ExpectedTargetIdentity = FileIdentity.Capture(d3d11Target),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcAD3d11),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-collision/d3d11.dll"
                },
                new MultiFileTransactionFile
                {
                    RelativePath = "dxgi.dll",
                    SourceFilePath = srcADxgi,
                    ExpectedTargetIdentity = FileIdentity.Capture(dxgiTarget),
                    ExpectedSourceIdentity = FileIdentity.Capture(srcADxgi),
                    OriginalState = OriginalFileState.Existing,
                    BackupRelativePath = "inst-collision/dxgi.dll"
                }
            }
        });

        Assert.Equal(TransactionState.Aborted, result.State);
        Assert.Equal(TransactionOutcome.SafeAbort, result.Outcome);

        // Game targets are completely unchanged (competing backup was not adopted to deploy DXVK)
        AssertSnapshotsEqual(initialGameSnapshot, CaptureSnapshot(game.RootPath));

        // Competing writer's destination file is preserved untouched, while File 0's backup created by this attempt was cleaned up
        var competingPath = storage.GetPath("inst-collision/dxgi.dll");
        Assert.True(File.Exists(competingPath));
        Assert.Equal("competing-writer-dxgi-backup", File.ReadAllText(competingPath));
        Assert.False(File.Exists(storage.GetPath("inst-collision/d3d11.dll")));

        // Pre-existing backups and earlier unresolved evidence remain untouched
        Assert.Equal("preserved-existing-backup", File.ReadAllText(storage.GetPath("existing-baseline/other.dll")));
        Assert.Equal("{\"unresolved\":true}", File.ReadAllText(storage.GetPath(".transactions/earlier-tx/plan.json")));
    }
}
