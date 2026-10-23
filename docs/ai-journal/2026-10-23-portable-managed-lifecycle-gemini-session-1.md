# Portable managed-state lifecycle acceptance — Gemini, 2026-10-23

Task [#284](https://github.com/Tiflit/DXVK-Companion/issues/284), formatting successor [#286](https://github.com/Tiflit/DXVK-Companion/issues/286), base main `46e6f22363cddcfa88d805b588b6ac30173bc9dc`, policy `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9`. Attributed coordinator transcription of owner instruction: "My Antigravity quotas have reset. We can task Gemini with implementation again. Continue." Owner prioritizes complete standalone DXVK functionality/tests and defers UI polish.

Implemented service-level acceptance coverage in `tests/DXVKCompanion.PhaseA.Tests/PortableManagedLifecycleAcceptanceTests.cs` for the specification's portable-folder move requirement (§47 Phase F, item 59). Created documentation `docs/validation/PORTABLE-MANAGED-LIFECYCLE.md`.

## Implementation & Repair Cycle Details

Responding to Codex's review on PR #285, completed repairs within the original allowed paths:
1. **Parameterized Baseline Variants**: Converted `PortableManagedLifecycle_RelocatedBundle_PreservesBaselinesAndRestoresByteIdentical` to a theory testing both `preexistingNativeBaseline: true` and `false`. The absent baseline exercises a clean-slate game with only `Game.exe` and an empty subdirectory; verifies that `d3d11.dll`, `dxgi.dll`, and `dxvk.conf` are deployed with `Missing` original states and restored by deletion, returning the game directory to its exact original resource set.
2. **Complete Resource Sets & Empty Directories**: Replaced flat file snapshots with `ResourceSnapshot` capturing all relative directories (including empty `Data/EmptySubDir`) and file byte dictionaries, ensuring exact structural and binary preservation across checkpoints. Asserted per-file expected managed identities, disabled config ownership hashes, and cleared active per-file versions (`record.ManagedDxvkVersion == null`) after restore.
3. **Persisted Settings & Cache Records**: Saved nondefault `SettingsStore` (`GlobalPolicy = Automated`) and `CacheStore` records before relocation; verified persistence after destination reconstruction.
4. **Persisted Selector Intent**: Tested update through `RequestUpdateByPathAsync(profile)` without `targetVersion` method overrides, relying on persisted `ManagementPolicy.PinVersion("v2.6")`.
5. **Fresh Service Stack Reconstruction**: Implemented `ConstructServiceStack` to reconstruct all services (`CacheStore`, `DxvkGithubClient`, `ProfileStore`, `GameLibraryStore`, `MultiFileTransactionEngine`, `DxvkInstaller`, `DxvkRollback`, `DxvkManager`) from destination paths after each reload without reusing surviving service instances.
6. **Exact Key-Assignment Parsing**: Implemented `ParseConfigAssignments` to verify exact removal of managed `dxvk.hud` and `dxvk.maxFrameRate` assignments while verifying that lookalike prefix keys (`dxvk.hudExtra = full`, `dxvk.maxFrameRateTarget = 120`) and user settings are preserved.
7. **Theory Corrupt/Missing Backup**: Preserved and updated to use `ConstructServiceStack` and `SnapshotResources`.

## Verification & Execution Record

- **Tests First Policy**: No production code defects were demonstrated within the actually exercised scenarios; production files remain untouched.
- **Hosted CI Evidence**:
  - Run 40038051824 attempt 1: 1,277/1,278 passed; `AutomaticDiscoveryStartupTests.DisabledDiscovery_DoesNotBypassRealActivityChecksForEnableOrRestore(restore:false, unknown:true)` threw an `IOException` (store lease acquisition timeout) during setup under concurrent test execution.
  - Run 40038051824 attempt 2: 1,278/1,278 passed at the same checkout.
  - Run 40040983312 attempt 1 (repair cycle): failed compilation on `ReleaseInfo.TagName` (nonexistent property on `ReleaseInfo`); corrected to `ReleaseInfo.Version` for subsequent attempt.
  - Run 40041159941 attempt 1: 1,277/1,279 passed; `confRecord2.ManagedDxvkVersion` asserted `"v2.6"` instead of `null` (`dxvk.conf` is a configuration record whose version is null per `DxvkInstaller.cs:601`); corrected to `Assert.Null(confRecord2.ManagedDxvkVersion)`.
  - Workflow tests, PR hygiene, scope checks, and dashboard runs passed.
- **Diagnostics & Scope**:
  - Reported local script diagnostic deviation during session 1 did not involve host application suites. Hosted CI remains the sole test execution environment; zero local test/app/helper execution.
  - Successor contract #286 adds the `### Scope notes` heading to resolve machine-parsed allowed paths on live PR runs.
  - Fixture releases and digests represent synthetic official-shaped metadata and archive validation rather than independent upstream authenticity.
