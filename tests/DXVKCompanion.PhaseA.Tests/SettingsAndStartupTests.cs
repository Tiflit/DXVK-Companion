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

        public List<string> Operations { get; }

        public FakeStartupRegistry(List<string>? sharedTrace = null)
        {
            Operations = sharedTrace ?? new List<string>();
        }

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
        public void SettingsStore_Save_FailureDuringPartialTempWrite_PreservesOldTargetFileAndBytes_AndCleansTemp()
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

            // Deterministic fault injection: writes partial output to temp file then throws
            store.CustomTempWriter = (tempPath) =>
            {
                File.WriteAllText(tempPath, "{\"Incomplete\": true, \"Truncated\":");
                throw new IOException("Simulated disk error after partial temp write.");
            };

            bool saveSuccess = store.Save(out var error);

            Assert.False(saveSuccess);
            Assert.NotNull(error);
            Assert.Contains("IOException", error);

            // Verify original target file is untouched with identical bytes
            Assert.True(File.Exists(path));
            byte[] currentBytes = File.ReadAllBytes(path);
            Assert.Equal(originalBytes, currentBytes);

            // Verify operation-owned temporary file was cleaned up
            var tmpFiles = Directory.GetFiles(testDir.RootPath, "*.tmp*");
            Assert.Empty(tmpFiles);
        }

        [Fact]
        public void SettingsStore_Save_FailureDuringCommitReplace_PreservesOldTargetFileAndBytes_AndCleansTemp()
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

            // Deterministic fault injection: temp write completes, but final commit replace fails
            store.CustomCommitReplace = (tempPath, targetPath) =>
            {
                Assert.True(File.Exists(tempPath));
                throw new IOException("Simulated commit replace fault.");
            };

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
            store.CustomTempWriter = (tempPath) =>
            {
                File.WriteAllText(tempPath, "{\"Corrupt\": true");
                throw new IOException("Simulated write fault.");
            };

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

            bool enableSuccess = manager.EnableStartup(out var enableError);
            Assert.False(enableSuccess);
            Assert.NotNull(enableError);
            Assert.Contains("not found", enableError);

            bool disableSuccess = manager.DisableStartup(out var disableError);
            Assert.True(disableSuccess);
            Assert.Null(disableError);
        }

        [Fact]
        public void StartupManager_MissingValueOnDisable_SucceedsIdempotently()
        {
            var fakeRegistry = new FakeStartupRegistry { KeyExists = true };
            var manager = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");

            Assert.False(fakeRegistry.CurrentKey.Values.ContainsKey("DXVK Companion"));
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
        public void StartupManager_SetValueFailure_ReportedAsFailure()
        {
            var fakeRegistry = new FakeStartupRegistry();
            fakeRegistry.CurrentKey.ThrowOnSetValue = true;
            var manager = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");

            bool enableSuccess = manager.EnableStartup(out var enableError);
            Assert.False(enableSuccess);
            Assert.NotNull(enableError);
            Assert.Contains("UnauthorizedAccessException", enableError);
        }

        [Fact]
        public void StartupManager_DeleteValueFailure_ReportedAsFailure()
        {
            var fakeRegistry = new FakeStartupRegistry();
            fakeRegistry.CurrentKey.Values["DXVK Companion"] = "\"C:\\TestApp\\DXVK-Companion.exe\"";
            fakeRegistry.CurrentKey.ThrowOnDeleteValue = true;
            var manager = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");

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

            store.CustomCommitReplace = (_, _) => throw new IOException("Commit replace failed.");

            var fakeRegistry = new FakeStartupRegistry();
            var startup = new StartupManager(fakeRegistry);
            var coordinator = new SettingsChangeCoordinator(store, startup);

            var result = coordinator.ChangePolicy(GlobalManagementPolicy.Automated);

            Assert.Equal(SettingsOperationStatus.Failure, result.Status);
            Assert.NotNull(result.Message);
            Assert.True(result.ShouldRevertUI);
            Assert.Equal(GlobalManagementPolicy.Manual, result.ActivePolicy);

            Assert.Equal(GlobalManagementPolicy.Manual, coordinator.ActivePolicy);
            Assert.Equal(GlobalManagementPolicy.Manual, store.GlobalPolicy);

            var loaded = SettingsStore.Load(path);
            Assert.Equal(GlobalManagementPolicy.Manual, loaded.GlobalPolicy);
        }

        [Fact]
        public void SettingsCoordinator_StartupEnable_RegistrySetValueFailure_RetainsPriorStateAndDoesNotSave()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, LaunchOnStartup = false };
            Assert.True(store.Save(out _));

            var fakeRegistry = new FakeStartupRegistry();
            fakeRegistry.CurrentKey.ThrowOnSetValue = true;
            var startup = new StartupManager(fakeRegistry);
            var coordinator = new SettingsChangeCoordinator(store, startup);

            var result = coordinator.ChangeLaunchOnStartup(true);

            Assert.Equal(SettingsOperationStatus.Failure, result.Status);
            Assert.NotNull(result.Message);
            Assert.Contains("not confirmed", result.Message);
            Assert.True(result.ShouldRevertUI);
            Assert.False(result.ActiveLaunchOnStartup);

            Assert.False(coordinator.ActiveLaunchOnStartup);
            Assert.False(store.LaunchOnStartup);

            var loaded = SettingsStore.Load(path);
            Assert.False(loaded.LaunchOnStartup);
        }

        [Fact]
        public void SettingsCoordinator_StartupDisable_RegistryDeleteValueFailure_RetainsPriorStateAndDoesNotSave()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, LaunchOnStartup = true };
            Assert.True(store.Save(out _));

            var fakeRegistry = new FakeStartupRegistry();
            fakeRegistry.CurrentKey.Values["DXVK Companion"] = "\"C:\\TestApp\\DXVK-Companion.exe\"";
            fakeRegistry.CurrentKey.ThrowOnDeleteValue = true;
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

            var fakeRegistry = new FakeStartupRegistry();
            var startup = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");
            store.CustomCommitReplace = (_, _) => throw new IOException("Commit replace failed.");

            var coordinator = new SettingsChangeCoordinator(store, startup);

            var result = coordinator.ChangeLaunchOnStartup(true);

            Assert.True(fakeRegistry.CurrentKey.Values.ContainsKey("DXVK Companion"));

            Assert.Equal(SettingsOperationStatus.Warning, result.Status);
            Assert.False(result.ShouldRevertUI);
            Assert.NotNull(result.Message);
            Assert.Contains("Windows startup was updated, but saving your preference failed", result.Message);

            Assert.True(result.ActiveLaunchOnStartup);
            Assert.True(coordinator.ActiveLaunchOnStartup);
            Assert.True(store.LaunchOnStartup);
            Assert.True(coordinator.HasUnresolvedStartupSave);
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
            fakeRegistry.CurrentKey.Values["DXVK Companion"] = "\"C:\\TestApp\\DXVK-Companion.exe\"";

            store.CustomCommitReplace = (_, _) => throw new IOException("Commit replace failed.");
            var coordinator = new SettingsChangeCoordinator(store, startup);

            var result = coordinator.ChangeLaunchOnStartup(false);

            Assert.False(fakeRegistry.CurrentKey.Values.ContainsKey("DXVK Companion"));

            Assert.Equal(SettingsOperationStatus.Warning, result.Status);
            Assert.False(result.ShouldRevertUI);
            Assert.NotNull(result.Message);
            Assert.Contains("Windows startup was updated, but saving your preference failed", result.Message);

            Assert.False(result.ActiveLaunchOnStartup);
            Assert.False(coordinator.ActiveLaunchOnStartup);
            Assert.False(store.LaunchOnStartup);
            Assert.True(coordinator.HasUnresolvedStartupSave);
        }

        [Fact]
        public void SettingsCoordinator_SameStateRequest_AfterSaveFailure_RetainsWarningUntilPersistenceSucceeds()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, LaunchOnStartup = false };
            Assert.True(store.Save(out _));

            var fakeRegistry = new FakeStartupRegistry();
            var startup = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");

            // Registry succeeds, but save fails
            store.CustomCommitReplace = (_, _) => throw new IOException("Commit replace failed.");
            var coordinator = new SettingsChangeCoordinator(store, startup);

            var r1 = coordinator.ChangeLaunchOnStartup(true);
            Assert.Equal(SettingsOperationStatus.Warning, r1.Status);
            Assert.True(coordinator.HasUnresolvedStartupSave);

            // Repeat request with same state while save still failing
            var r2 = coordinator.ChangeLaunchOnStartup(true);
            Assert.Equal(SettingsOperationStatus.Warning, r2.Status);
            Assert.NotNull(r2.Message);
            Assert.True(coordinator.HasUnresolvedStartupSave);

            // Now fault is resolved; repeat same-state request retries persistence and succeeds
            store.CustomCommitReplace = null;
            var r3 = coordinator.ChangeLaunchOnStartup(true);
            Assert.Equal(SettingsOperationStatus.Success, r3.Status);
            Assert.Null(r3.Message);
            Assert.False(coordinator.HasUnresolvedStartupSave);

            var loaded = SettingsStore.Load(path);
            Assert.True(loaded.LaunchOnStartup);
        }

        [Fact]
        public void SettingsCoordinator_SameStateRequest_AfterDisableSaveFailure_RetainsWarningUntilPersistenceSucceeds()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, LaunchOnStartup = true };
            Assert.True(store.Save(out _));

            var fakeRegistry = new FakeStartupRegistry();
            var startup = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");
            fakeRegistry.CurrentKey.Values["DXVK Companion"] = "\"C:\\TestApp\\DXVK-Companion.exe\"";

            store.CustomCommitReplace = (_, _) => throw new IOException("Commit replace failed.");
            var coordinator = new SettingsChangeCoordinator(store, startup);

            var r1 = coordinator.ChangeLaunchOnStartup(false);
            Assert.Equal(SettingsOperationStatus.Warning, r1.Status);

            // Same state repeat attempt retains warning
            var r2 = coordinator.ChangeLaunchOnStartup(false);
            Assert.Equal(SettingsOperationStatus.Warning, r2.Status);

            // Resolve fault and retry same state
            store.CustomCommitReplace = null;
            var r3 = coordinator.ChangeLaunchOnStartup(false);
            Assert.Equal(SettingsOperationStatus.Success, r3.Status);
            Assert.False(coordinator.HasUnresolvedStartupSave);

            var loaded = SettingsStore.Load(path);
            Assert.False(loaded.LaunchOnStartup);
        }

        [Fact]
        public void SettingsCoordinator_AlreadyPersistedNoOp_ReturnsSuccessDirectly()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, GlobalPolicy = GlobalManagementPolicy.Manual, LaunchOnStartup = false };
            Assert.True(store.Save(out _));

            var fakeRegistry = new FakeStartupRegistry();
            var startup = new StartupManager(fakeRegistry);
            var coordinator = new SettingsChangeCoordinator(store, startup);

            // Ordinary no-op policy request returns success without write faults
            store.CustomTempWriter = (_) => throw new InvalidOperationException("Should not write on clean no-op.");
            var r1 = coordinator.ChangePolicy(GlobalManagementPolicy.Manual);
            Assert.Equal(SettingsOperationStatus.Success, r1.Status);

            // Ordinary no-op startup request returns success without write faults
            var r2 = coordinator.ChangeLaunchOnStartup(false);
            Assert.Equal(SettingsOperationStatus.Success, r2.Status);
        }

        [Fact]
        public void SettingsCoordinator_UnrelatedPolicySave_ClearsUnresolvedStartupSaveWarning()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var store = new SettingsStore { CustomSettingsPath = path, GlobalPolicy = GlobalManagementPolicy.Manual, LaunchOnStartup = false };
            Assert.True(store.Save(out _));

            var fakeRegistry = new FakeStartupRegistry();
            var startup = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");
            var coordinator = new SettingsChangeCoordinator(store, startup);

            // Enable startup with save failure
            store.CustomCommitReplace = (_, _) => throw new IOException("Initial save fault.");
            var r1 = coordinator.ChangeLaunchOnStartup(true);
            Assert.Equal(SettingsOperationStatus.Warning, r1.Status);
            Assert.True(coordinator.HasUnresolvedStartupSave);

            // Clear fault; unrelated policy change succeeds
            store.CustomCommitReplace = null;
            var r2 = coordinator.ChangePolicy(GlobalManagementPolicy.Automated);
            Assert.Equal(SettingsOperationStatus.Success, r2.Status);
            Assert.False(coordinator.HasUnresolvedStartupSave);

            // Verify both policy and startup were persisted
            var loaded = SettingsStore.Load(path);
            Assert.Equal(GlobalManagementPolicy.Automated, loaded.GlobalPolicy);
            Assert.True(loaded.LaunchOnStartup);
        }

        [Fact]
        public void SettingsCoordinator_StartupToggle_CallOrdering_SharedTraceProvesRegistryPrecedesSave()
        {
            using var testDir = new SyntheticTestDirectory();
            string path = testDir.GetPath("settings.json");

            var sharedTrace = new List<string>();

            var store = new SettingsStore { CustomSettingsPath = path, LaunchOnStartup = false };
            store.CustomTempWriter = (tempPath) =>
            {
                sharedTrace.Add("Save:TempWriter");
                File.WriteAllText(tempPath, "{}");
            };
            store.CustomCommitReplace = (tempPath, targetPath) =>
            {
                sharedTrace.Add("Save:CommitReplace");
                File.Move(tempPath, targetPath, overwrite: true);
            };

            var fakeRegistry = new FakeStartupRegistry(sharedTrace);
            var startup = new StartupManager(fakeRegistry, @"C:\TestApp\DXVK-Companion.exe");
            var coordinator = new SettingsChangeCoordinator(store, startup);

            var result = coordinator.ChangeLaunchOnStartup(true);

            Assert.Equal(SettingsOperationStatus.Success, result.Status);

            // Verify ordered call sequence: OpenRunKey -> SetValue -> Save:TempWriter -> Save:CommitReplace -> DisposeKey
            int openIndex = sharedTrace.IndexOf("OpenRunKey(writable=True)");
            int setValueIndex = sharedTrace.FindIndex(s => s.StartsWith("SetValue"));
            int tempWriterIndex = sharedTrace.IndexOf("Save:TempWriter");
            int commitIndex = sharedTrace.IndexOf("Save:CommitReplace");

            Assert.True(openIndex >= 0, "OpenRunKey was not called.");
            Assert.True(setValueIndex > openIndex, "SetValue must follow OpenRunKey.");
            Assert.True(tempWriterIndex > setValueIndex, "Save:TempWriter must follow SetValue.");
            Assert.True(commitIndex > tempWriterIndex, "Save:CommitReplace must follow Save:TempWriter.");
        }

        [Fact]
        public void UiReentrancyGuard_SuppressesRecursiveExecution()
        {
            var guard = new UiReentrancyGuard();
            int outerExecutions = 0;
            int innerExecutions = 0;
            bool innerAttemptResult = true;

            guard.TryExecute(() =>
            {
                outerExecutions++;
                Assert.True(guard.IsExecuting);

                // Nested execution should be suppressed
                innerAttemptResult = guard.TryExecute(() =>
                {
                    innerExecutions++;
                });
            });

            Assert.False(guard.IsExecuting);
            Assert.Equal(1, outerExecutions);
            Assert.Equal(0, innerExecutions);
            Assert.False(innerAttemptResult);
        }
    }
}
