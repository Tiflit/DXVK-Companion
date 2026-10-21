using System.Diagnostics;
using System.Net.Http;
using DXVKCompanion.DXVK;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;

namespace DXVKCompanion.PhaseATests;

// For filesystem-only compatibility/restoration fixtures, not activity/exclusion tests.
// Keep real leases and transactions, but make the synthetic process universe explicit.
internal static class SyntheticSafetyFixture
{
    internal enum Phase
    {
        StoreLeaseSetup,
        HelperStartupSignal,
        HelperSelfExit,
        HelperCleanup,
        PostHelperLease,
        HelperExclusion,
        HelperTimerArm
    }

    // Explicit enum phases prevent callers from inserting arbitrary paths/output.
    internal static string Diagnostic(Phase phase, long elapsedMilliseconds, string? refusal = null)
        => $"Synthetic phase={phase}; elapsedMs={Math.Max(0, elapsedMilliseconds)}; category={RefusalCategory(refusal)}";

    private static readonly IProcessActivityInspector Inactive = new ProcessActivityInspector(
        processEnumerator: () => Array.Empty<Process>());

    internal static OperationLeaseCoordinator ConfigureStore(GameLibraryStore store)
    {
        var directory = Path.Combine(Path.GetDirectoryName(store.LibraryFilePath)!, "coordination");
        var coordinator = new OperationLeaseCoordinator(directory);
        store.LeaseCoordinator = coordinator;
        return coordinator;
    }

    internal static DxvkInstaller CreateInstaller(
        HttpClient http, MultiFileTransactionEngine engine, GameLibraryStore store,
        string sourceDirectory, FileUtils? files = null)
        => new DxvkInstaller(http, engine, store, sourceDirectory, files,
            ConfigureStore(store), Inactive);

    internal static DxvkRollback CreateRollback(
        MultiFileTransactionEngine engine, GameLibraryStore store, FileUtils? files = null)
        => new DxvkRollback(engine, store, files, ConfigureStore(store), Inactive);

    internal static DxvkManager CreateManager(
        DxvkInstaller installer, DxvkRollback rollback, DxvkGithubClient github,
        ProfileStore profiles, GameLibraryStore store)
        => new DxvkManager(installer, rollback, github, profiles, store,
            ConfigureStore(store), Inactive);

    internal static ManagedFileInspector CreateInspector(GameLibraryStore store)
    {
        ConfigureStore(store);
        return new ManagedFileInspector(store);
    }

    // Never include arbitrary refusal text, exception paths or process information in CI.
    internal static string RefusalCategory(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "NoRefusalRecorded";
        if (reason.Contains("activity", StringComparison.OrdinalIgnoreCase)) return "ActivityUncertain";
        if (reason.Contains("progress", StringComparison.OrdinalIgnoreCase)) return "OperationBusy";
        if (reason.Contains("access denied", StringComparison.OrdinalIgnoreCase)) return "AccessDenied";
        if (reason.Contains("persist", StringComparison.OrdinalIgnoreCase)) return "Persistence";
        if (reason.Contains("unsupported", StringComparison.OrdinalIgnoreCase)) return "UnsupportedApi";
        return "OtherRefusal";
    }
}
