using System;
using System.Collections.Generic;
using System.IO;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Storage;
using DXVKCompanion.UI;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class FakeStartupRegistry : IStartupRegistry
    {
        public bool KeyExists { get; set; } = true;
        public bool ThrowOnOpen { get; set; } = false;
        public Exception? OpenException { get; set; }

        public FakeStartupRegistryKey CurrentKey { get; } = new();

        public List<string> Operations { get; } = new();

        public IStartupRegistryKey? OpenRunKey(bool writable)
        {
            Operations.Add($"OpenRunKey(writable={writable})");
            if (ThrowOnOpen)
            {
                throw OpenException ?? new UnauthorizedAccessException("Simulated registry access denial.");
            }
            if (!KeyExists)
            {
                return null;
            }
            CurrentKey.SetRegistry(this);
            return CurrentKey;
        }
    }

    public class FakeStartupRegistryKey : IStartupRegistryKey
    {
        private FakeStartupRegistry? _registry;

        public Dictionary<string, string> Values { get; } = new();
        public bool ThrowOnSetValue { get; set; } = false;
        public Exception? SetValueException { get; set; }
        public bool ThrowOnDeleteValue { get; set; } = false;
        public Exception? DeleteValueException { get; set; }
        public bool IsDisposed { get; private set; } = false;

        public void SetRegistry(FakeStartupRegistry registry)
        {
            _registry = registry;
            IsDisposed = false;
        }

        public void SetValue(string name, string value)
        {
            _registry?.Operations.Add($"SetValue({name}, {value})");
            if (ThrowOnSetValue)
            {
                throw SetValueException ?? new UnauthorizedAccessException("Simulated SetValue denial.");
            }
            Values[name] = value;
        }

        public void DeleteValue(string name, bool throwOnMissingValue)
        {
            _registry?.Operations.Add($"DeleteValue({name}, throwOnMissing={throwOnMissingValue})");
            if (ThrowOnDeleteValue)
            {
                throw DeleteValueException ?? new UnauthorizedAccessException("Simulated DeleteValue denial.");
            }
            if (!Values.ContainsKey(name))
            {
                if (throwOnMissingValue)
                {
                    throw new ArgumentException("Value not found.");
                }
                return;
            }
            Values.Remove(name);
        }

        public void Dispose()
        {
            _registry?.Operations.Add("DisposeKey");
            IsDisposed = true;
        }
    }

    public class SettingsAndStartupTests
    {
        [Fact]
        public void SettingsStore_SaveAndLoad_RoundtripsSuccessfully()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path };
            store.GlobalPolicy = GlobalManagementPolicy.Automated;
            store.LaunchOnStartup = true;

            bool saveSuccess = store.Save(out var error);
            Assert.True(saveSuccess, error);
            Assert.Null(error);
            Assert.True(File.Exists(path));

            var loaded = SettingsStore.Load(path);
            Assert.Equal(GlobalManagementPolicy.Automated, loaded.GlobalPolicy);
            Assert.True(loaded.LaunchOnStartup);
            Assert.True(loaded.AutoEnableDxvkForNewGames);
        }

        [Fact]
        public void SettingsStore_Save_FailureDuringTempWrite_PreservesOldTargetFileAndBytes()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var initialStore = new SettingsStore { CustomSettingsPath = path };
            initialStore.GlobalPolicy = GlobalManagementPolicy.Manual;
            initialStore.LaunchOnStartup = false;
            Assert.True(initialStore.Save(out _));

            byte[] originalBytes = File.ReadAllBytes(path);

            var store = SettingsStore.Load(path);
            store.GlobalPolicy = GlobalManagementPolicy.Automated;
            store.SimulatedTempWriteFailure = () => throw new IOException("Disk write fault during temp output.");

            bool saveSuccess = store.Save(out var error);

            Assert.False(saveSuccess);
            Assert.NotNull(error);
            Assert.Contains("IOException", error);

            // Verify original file is untouched with identical bytes
            Assert.True(File.Exists(path));
            byte[] currentBytes = File.ReadAllBytes(path);
            Assert.Equal(originalBytes, currentBytes);

            // Verify no stray .tmp files left in directory
            var tmpFiles = Directory.GetFiles(testDir.RootPath, "*.tmp*");
            Assert.Empty(tmpFiles);
        }

        [Fact]
        public void SettingsStore_Save_FailureDuringCommitReplace_PreservesOldTargetFileAndBytes()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var initialStore = new SettingsStore { CustomSettingsPath = path };
            initialStore.GlobalPolicy = GlobalManagementPolicy.Manual;
            initialStore.LaunchOnStartup = false;
            Assert.True(initialStore.Save(out _));

            byte[] originalBytes = File.ReadAllBytes(path);

            var store = SettingsStore.Load(path);
            store.GlobalPolicy = GlobalManagementPolicy.Automated;
            store.SimulatedCommitFailure = () => throw new IOException("File replace commit fault.");

            bool saveSuccess = store.Save(out var error);

            Assert.False(saveSuccess);
            Assert.NotNull(error);
            Assert.Contains("IOException", error);

            Assert.True(File.Exists(path));
            byte[] currentBytes = File.ReadAllBytes(path);
            Assert.Equal(originalBytes, currentBytes);

            var tmpFiles = Directory.GetFiles(testDir.RootPath, "*.tmp*");
            Assert.Empty(tmpFiles);
        }

        [Fact]
        public void SettingsStore_Save_FailureWhenTargetDoesNotExist_DoesNotCreateTargetFile()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path };
            store.GlobalPolicy = GlobalManagementPolicy.Automated;
            store.SimulatedTempWriteFailure = () => throw new IOException("Disk write fault.");

            bool saveSuccess = store.Save(out var error);

            Assert.False(saveSuccess);
            Assert.NotNull(error);
            Assert.False(File.Exists(path));

            var tmpFiles = Directory.GetFiles(testDir.RootPath, "*.tmp*");
            Assert.Empty(tmpFiles);
        }

        [Fact]
        public void StartupManager_EnableAndDisable_UpdatesRegistryKeySuccessfully()
        {
            var fakeRegistry = new FakeStartupRegistry();
            var manager = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");

            bool enableSuccess = manager.EnableStartup(out var enableError);
            Assert.True(enableSuccess, enableError);
            Assert.Null(enableError);
            Assert.True(fakeRegistry.CurrentKey.Values.ContainsKey("DXVK Companion"));
            Assert.Equal("\"C:\\TestApp\\DXVK-Companion.exe\"", fakeRegistry.CurrentKey.Values["DXVK Companion"]);

            bool disableSuccess = manager.DisableStartup(out var disableError);
            Assert.True(disableSuccess, disableError);
            Assert.Null(disableError);
            Assert.False(fakeRegistry.CurrentKey.Values.ContainsKey("DXVK Companion"));
        }

        [Fact]
        public void StartupManager_MissingRunKey_EnableFails_DisableSucceedsIdempotently()
        {
            var fakeRegistry = new FakeStartupRegistry { KeyExists = false };
            var manager = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");

            // Enabling with missing Run key is a reported failure
            bool enableSuccess = manager.EnableStartup(out var enableError);
            Assert.False(enableSuccess);
            Assert.NotNull(enableError);
            Assert.Contains("not found", enableError);

            // Disabling with missing Run key is an idempotent success
            bool disableSuccess = manager.DisableStartup(out var disableError);
            Assert.True(disableSuccess);
            Assert.Null(disableError);
        }

        [Fact]
        public void StartupManager_RegistryAccessErrors_ReportedAsFailures()
        {
            var fakeRegistry = new FakeStartupRegistry { ThrowOnOpen = true };
            var manager = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");

            bool enableSuccess = manager.EnableStartup(out var enableError);
            Assert.False(enableSuccess);
            Assert.NotNull(enableError);
            Assert.Contains("UnauthorizedAccessException", enableError);

            bool disableSuccess = manager.DisableStartup(out var disableError);
            Assert.False(disableSuccess);
            Assert.NotNull(disableError);
            Assert.Contains("UnauthorizedAccessException", disableError);
        }

        [Fact]
        public void SettingsCoordinator_PolicyChange_Success_PersistsAndUpdatesPolicy()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, GlobalPolicy = GlobalManagementPolicy.Manual };
            Assert.True(store.Save(out _));

            var fakeRegistry = new FakeStartupRegistry();
            var startup = new StartupManager(fakeRegistry);
            var coordinator = new SettingsChangeCoordinator(store, startup);

            var result = coordinator.ChangePolicy(GlobalManagementPolicy.Automated);

            Assert.Equal(SettingsOperationStatus.Success, result.Status);
            Assert.Null(result.Message);
            Assert.False(result.ShouldRevertUI);
            Assert.Equal(GlobalManagementPolicy.Automated, result.ActivePolicy);
            Assert.Equal(GlobalManagementPolicy.Automated, coordinator.ActivePolicy);
            Assert.Equal(GlobalManagementPolicy.Automated, store.GlobalPolicy);

            var loaded = SettingsStore.Load(path);
            Assert.Equal(GlobalManagementPolicy.Automated, loaded.GlobalPolicy);
        }

        [Fact]
        public void SettingsCoordinator_PolicyChange_SaveFailure_RetainsPriorPolicyAndRevertsUI()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, GlobalPolicy = GlobalManagementPolicy.Manual };
            Assert.True(store.Save(out _));

            // Inject commit failure
            store.SimulatedCommitFailure = () => throw new IOException("Commit replace failed.");

            var fakeRegistry = new FakeStartupRegistry();
            var startup = new StartupManager(fakeRegistry);
            var coordinator = new SettingsChangeCoordinator(store, startup);

            var result = coordinator.ChangePolicy(GlobalManagementPolicy.Automated);

            Assert.Equal(SettingsOperationStatus.Failure, result.Status);
            Assert.NotNull(result.Message);
            Assert.True(result.ShouldRevertUI);
            Assert.Equal(GlobalManagementPolicy.Manual, result.ActivePolicy);

            // Shared in-memory store remains untouched; automated consumer does not see uncommitted policy
            Assert.Equal(GlobalManagementPolicy.Manual, coordinator.ActivePolicy);
            Assert.Equal(GlobalManagementPolicy.Manual, store.GlobalPolicy);

            // Disk retains old bytes
            var loaded = SettingsStore.Load(path);
            Assert.Equal(GlobalManagementPolicy.Manual, loaded.GlobalPolicy);
        }

        [Fact]
        public void SettingsCoordinator_StartupEnable_RegistryFailure_RetainsPriorStateAndDoesNotSave()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, LaunchOnStartup = false };
            Assert.True(store.Save(out _));

            var fakeRegistry = new FakeStartupRegistry { KeyExists = false };
            var startup = new StartupManager(fakeRegistry);
            var coordinator = new SettingsChangeCoordinator(store, startup);

            var result = coordinator.ChangeLaunchOnStartup(true);

            Assert.Equal(SettingsOperationStatus.Failure, result.Status);
            Assert.NotNull(result.Message);
            Assert.Contains("not confirmed", result.Message);
            Assert.True(result.ShouldRevertUI);
            Assert.False(result.ActiveLaunchOnStartup);

            // In-memory state and disk remain unchanged
            Assert.False(coordinator.ActiveLaunchOnStartup);
            Assert.False(store.LaunchOnStartup);

            var loaded = SettingsStore.Load(path);
            Assert.False(loaded.LaunchOnStartup);
        }

        [Fact]
        public void SettingsCoordinator_StartupDisable_RegistryFailure_RetainsPriorStateAndDoesNotSave()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, LaunchOnStartup = true };
            Assert.True(store.Save(out _));

            var fakeRegistry = new FakeStartupRegistry { ThrowOnOpen = true };
            var startup = new StartupManager(fakeRegistry);
            var coordinator = new SettingsChangeCoordinator(store, startup);

            var result = coordinator.ChangeLaunchOnStartup(false);

            Assert.Equal(SettingsOperationStatus.Failure, result.Status);
            Assert.NotNull(result.Message);
            Assert.Contains("not confirmed", result.Message);
            Assert.True(result.ShouldRevertUI);
            Assert.True(result.ActiveLaunchOnStartup);

            Assert.True(coordinator.ActiveLaunchOnStartup);
            Assert.True(store.LaunchOnStartup);

            var loaded = SettingsStore.Load(path);
            Assert.True(loaded.LaunchOnStartup);
        }

        [Fact]
        public void SettingsCoordinator_StartupEnable_RegistrySuccess_SaveFailure_RetainsAppliedStateWithWarning()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, LaunchOnStartup = false };
            Assert.True(store.Save(out _));

            // Registry succeeds, but file save fails
            var fakeRegistry = new FakeStartupRegistry();
            var startup = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");
            store.SimulatedCommitFailure = () => throw new IOException("Commit replace failed.");

            var coordinator = new SettingsChangeCoordinator(store, startup);

            var result = coordinator.ChangeLaunchOnStartup(true);

            // Registry was updated
            Assert.True(fakeRegistry.CurrentKey.Values.ContainsKey("DXVK Companion"));

            // Status is Warning, UI should NOT revert because registry is active
            Assert.Equal(SettingsOperationStatus.Warning, result.Status);
            Assert.False(result.ShouldRevertUI);
            Assert.NotNull(result.Message);
            Assert.Contains("Windows startup was updated, but saving your preference failed", result.Message);

            // Applied state retained in memory
            Assert.True(result.ActiveLaunchOnStartup);
            Assert.True(coordinator.ActiveLaunchOnStartup);
            Assert.True(store.LaunchOnStartup);
        }

        [Fact]
        public void SettingsCoordinator_StartupDisable_RegistrySuccess_SaveFailure_RetainsAppliedStateWithWarning()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, LaunchOnStartup = true };
            Assert.True(store.Save(out _));

            var fakeRegistry = new FakeStartupRegistry();
            var startup = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");
            // Populate initial registry value
            fakeRegistry.CurrentKey.Values["DXVK Companion"] = "\"C:\\TestApp\\DXVK-Companion.exe\"";

            store.SimulatedCommitFailure = () => throw new IOException("Commit replace failed.");
            var coordinator = new SettingsChangeCoordinator(store, startup);

            var result = coordinator.ChangeLaunchOnStartup(false);

            // Registry value was removed
            Assert.False(fakeRegistry.CurrentKey.Values.ContainsKey("DXVK Companion"));

            // Warning issued, UI not reverted
            Assert.Equal(SettingsOperationStatus.Warning, result.Status);
            Assert.False(result.ShouldRevertUI);
            Assert.NotNull(result.Message);
            Assert.Contains("Windows startup was updated, but saving your preference failed", result.Message);

            // Applied state is false in memory
            Assert.False(result.ActiveLaunchOnStartup);
            Assert.False(coordinator.ActiveLaunchOnStartup);
            Assert.False(store.LaunchOnStartup);
        }

        [Fact]
        public void SettingsCoordinator_RepeatedAttempts_OperatesOnAppliedStateAndSubsequentPolicySavePersists()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, GlobalPolicy = GlobalManagementPolicy.Manual, LaunchOnStartup = false };
            Assert.True(store.Save(out _));

            var fakeRegistry = new FakeStartupRegistry();
            var startup = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");
            var coordinator = new SettingsChangeCoordinator(store, startup);

            // Attempt 1: Enable startup with save failure
            store.SimulatedTempWriteFailure = () => throw new IOException("Temp write failure");
            var r1 = coordinator.ChangeLaunchOnStartup(true);
            Assert.Equal(SettingsOperationStatus.Warning, r1.Status);
            Assert.True(coordinator.ActiveLaunchOnStartup);

            // Unrelated policy change with save now working
            store.SimulatedTempWriteFailure = null;
            var r2 = coordinator.ChangePolicy(GlobalManagementPolicy.Automated);
            Assert.Equal(SettingsOperationStatus.Success, r2.Status);

            // Both automated policy AND startup state are now persisted to disk
            var loaded = SettingsStore.Load(path);
            Assert.Equal(GlobalManagementPolicy.Automated, loaded.GlobalPolicy);
            Assert.True(loaded.LaunchOnStartup);

            // Attempt 2: Disable startup with clean save
            var r3 = coordinator.ChangeLaunchOnStartup(false);
            Assert.Equal(SettingsOperationStatus.Success, r3.Status);
            Assert.False(coordinator.ActiveLaunchOnStartup);

            var loadedFinal = SettingsStore.Load(path);
            Assert.False(loadedFinal.LaunchOnStartup);
        }

        [Fact]
        public void SettingsCoordinator_StartupToggle_CallOrdering_RegistryPrecedesSave()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path };
            var fakeRegistry = new FakeStartupRegistry();
            var startup = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");

            var callOrder = new List<string>();
            fakeRegistry.Operations.Clear();

            store.SimulatedTempWriteFailure = () =>
            {
                callOrder.Add("Store.TempWrite");
            };

            var coordinator = new SettingsChangeCoordinator(store, startup);
            var result = coordinator.ChangeLaunchOnStartup(true);

            // Assert registry SetValue was invoked before Store.TempWrite
            int registrySetIndex = fakeRegistry.Operations.FindIndex(op => op.StartsWith("SetValue"));
            Assert.True(registrySetIndex >= 0);
            Assert.Contains("Store.TempWrite", callOrder);
        }

        [Fact]
        public void SettingsCoordinator_RestorationAndEventReentry_GuardsAgainstRecursion()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, GlobalPolicy = GlobalManagementPolicy.Manual };
            store.SimulatedCommitFailure = () => throw new IOException("Commit failed.");

            var fakeRegistry = new FakeStartupRegistry();
            var startup = new StartupManager(fakeRegistry);
            var coordinator = new SettingsChangeCoordinator(store, startup);

            int invocationCount = 0;
            bool isUpdatingUI = false;

            // Simulate UI event handler pattern
            void HandlePolicyChange(GlobalManagementPolicy requested)
            {
                if (isUpdatingUI) return;
                invocationCount++;

                var res = coordinator.ChangePolicy(requested);
                if (res.ShouldRevertUI)
                {
                    isUpdatingUI = true;
                    try
                    {
                        // Simulates checked changed firing when reverting
                        HandlePolicyChange(res.ActivePolicy);
                    }
                    finally
                    {
                        isUpdatingUI = false;
                    }
                }
            }

            HandlePolicyChange(GlobalManagementPolicy.Automated);

            // Must have executed exactly once because isUpdatingUI suppressed recursion
            Assert.Equal(1, invocationCount);
            Assert.Equal(GlobalManagementPolicy.Manual, coordinator.ActivePolicy);
        }
    }
}
