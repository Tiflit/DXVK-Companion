using System.Text.Json.Serialization;

namespace DXVKCompanion.Safety;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransactionOperation : byte
{
    None = 0,
    Install = 1,
    Update = 2,
    Reapply = 3,
    Restore = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransactionState : byte
{
    None = 0,
    Planned = 1,
    Validated = 2,
    Prepared = 3,
    Applying = 4,
    Verifying = 5,
    Committed = 6,
    Aborted = 7,
    Recovering = 8,
    FailedSafely = 9,
    AttentionRequired = 10
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManagedOwnership : byte
{
    Unmanaged = 0,
    Managed = 1,
    AttentionRequired = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OriginalFileState : byte
{
    Existing = 0,
    DidNotExist = 1,
    Unknown = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransactionOutcome : byte
{
    Success = 0,
    SafeAbort = 1,
    SafeFailure = 2,
    UnresolvedRecovery = 3
}

public sealed record SafetyFileIdentity(string Sha256, long Size)
{
    public bool IsValid =>
        Size >= 0 &&
        Sha256.Length == 64 &&
        Sha256.All(static c => Uri.IsHexDigit(c));
}

public sealed record SafetyManagedFileRecord
{
    public required string RelativePath { get; init; }
    public ManagedOwnership Ownership { get; init; } = ManagedOwnership.Unmanaged;
    public OriginalFileState OriginalState { get; init; } = OriginalFileState.Unknown;
    public SafetyFileIdentity? OriginalIdentity { get; init; }
    public SafetyFileIdentity? LastCommittedManagedIdentity { get; init; }
    public string? BackupRelativePath { get; init; }
    public SafetyFileIdentity? BackupIdentity { get; init; }
}

public sealed record SafetyFilePlan
{
    public required string RelativePath { get; init; }
    public required string SourceRelativePath { get; init; }
    public SafetyFileIdentity? ExpectedTargetIdentity { get; init; }
    public SafetyFileIdentity? ExpectedSourceIdentity { get; init; }
}

public sealed record SafetyTransactionPlan
{
    public required string TransactionId { get; init; }
    public required TransactionOperation Operation { get; init; }
    public required string InstallationRoot { get; init; }
    public required IReadOnlyList<SafetyFilePlan> Files { get; init; }
    public TransactionState State { get; init; } = TransactionState.Planned;
}

public sealed record SafetyTransactionResult
{
    public required string TransactionId { get; init; }
    public required TransactionOperation Operation { get; init; }
    public required TransactionState State { get; init; }
    public required TransactionOutcome Outcome { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> AffectedFiles { get; init; } = Array.Empty<string>();
}
