using System.Text.Json;
using System.Text.Json.Serialization;
using DXVKCompanion.Safety;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class PhaseA5SafetyContractTests
{
    private static JsonSerializerOptions JsonOptions => new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void TransactionStateValues_AreExplicitAndStable()
    {
        Assert.Equal(0, (byte)TransactionState.None);
        Assert.Equal(1, (byte)TransactionState.Planned);
        Assert.Equal(2, (byte)TransactionState.Validated);
        Assert.Equal(3, (byte)TransactionState.Prepared);
        Assert.Equal(4, (byte)TransactionState.Applying);
        Assert.Equal(5, (byte)TransactionState.Verifying);
        Assert.Equal(6, (byte)TransactionState.Committed);
        Assert.Equal(7, (byte)TransactionState.Aborted);
        Assert.Equal(8, (byte)TransactionState.Recovering);
        Assert.Equal(9, (byte)TransactionState.FailedSafely);
        Assert.Equal(10, (byte)TransactionState.AttentionRequired);

        var json = JsonSerializer.Serialize(TransactionState.AttentionRequired, JsonOptions);
        Assert.Equal("\"AttentionRequired\"", json);
        Assert.Equal(TransactionState.AttentionRequired,
            JsonSerializer.Deserialize<TransactionState>(json, JsonOptions));
    }

    [Fact]
    public void OwnershipStates_AreDistinct()
    {
        var values = Enum.GetValues<ManagedOwnership>();
        Assert.Equal(3, values.Length);
        Assert.Equal(0, (byte)ManagedOwnership.Unmanaged);
        Assert.Equal(1, (byte)ManagedOwnership.Managed);
        Assert.Equal(2, (byte)ManagedOwnership.AttentionRequired);
    }

    [Fact]
    public void OriginalState_ExplicitlyDistinguishesExistingAbsentAndUnknown()
    {
        Assert.NotEqual(OriginalFileState.Existing, OriginalFileState.DidNotExist);
        Assert.NotEqual(OriginalFileState.Existing, OriginalFileState.Unknown);
        Assert.NotEqual(OriginalFileState.DidNotExist, OriginalFileState.Unknown);
    }

    [Fact]
    public void FileIdentity_IsDeterministicForSameContentAndSize()
    {
        using var directory = new SyntheticTestDirectory();
        var first = directory.CreateFile("first.bin", "same synthetic content");
        var second = directory.CreateFile("second.bin", "same synthetic content");

        var identity1 = FileIdentity.Capture(first);
        var identity2 = FileIdentity.Capture(second);

        Assert.Equal(identity1, identity2);
        Assert.True(identity1.IsValid);
        Assert.Equal(64, identity1.Sha256.Length);
    }

    [Fact]
    public void SyntheticTestDirectory_UsesSystemTempAndCleansUp()
    {
        string rootPath;

        using (var directory = new SyntheticTestDirectory())
        {
            rootPath = directory.RootPath;
            directory.CreateFile("nested\file.txt", "synthetic");

            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), rootPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(directory.GetPath("nested\file.txt")));
            Assert.NotEqual(Path.GetFullPath(Directory.GetCurrentDirectory()), rootPath);
        }

        Assert.False(Directory.Exists(rootPath));
    }

    [Fact]
    public void SafetyContracts_RoundTripThroughJson()
    {
        using var directory = new SyntheticTestDirectory();
        var source = directory.CreateFile("source.dll", "source");
        var identity = FileIdentity.Capture(source);

        var file = new SafetyManagedFileRecord
        {
            RelativePath = "d3d11.dll",
            Ownership = ManagedOwnership.Managed,
            OriginalState = OriginalFileState.Existing,
            OriginalIdentity = identity,
            LastCommittedManagedIdentity = identity,
            BackupRelativePath = "backups/abc/source.dll",
            BackupIdentity = identity
        };

        var plan = new SafetyTransactionPlan
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            Operation = TransactionOperation.Update,
            InstallationRoot = directory.RootPath,
            Files = new[]
            {
                new SafetyFilePlan
                {
                    RelativePath = "d3d11.dll",
                    SourceRelativePath = "dxvk/x64/d3d11.dll",
                    ExpectedTargetIdentity = identity,
                    ExpectedSourceIdentity = identity
                }
            },
            State = TransactionState.Prepared
        };

        var json = JsonSerializer.Serialize(new { file, plan }, JsonOptions);
        Assert.Contains("AttentionRequired", JsonSerializer.Serialize(
            new SafetyTransactionResult
            {
                TransactionId = plan.TransactionId,
                Operation = plan.Operation,
                State = TransactionState.AttentionRequired,
                Outcome = TransactionOutcome.UnresolvedRecovery
            }, JsonOptions));
        Assert.Contains("Prepared", json);
        Assert.Contains("Managed", json);
        Assert.Contains(identity.Sha256, json);
    }

    [Fact]
    public void TransactionOutcome_DistinguishesSafeAbortSafeFailureAndUnresolvedRecovery()
    {
        Assert.NotEqual(TransactionOutcome.SafeAbort, TransactionOutcome.SafeFailure);
        Assert.NotEqual(TransactionOutcome.SafeFailure, TransactionOutcome.UnresolvedRecovery);
        Assert.NotEqual(TransactionOutcome.SafeAbort, TransactionOutcome.UnresolvedRecovery);
    }
}
