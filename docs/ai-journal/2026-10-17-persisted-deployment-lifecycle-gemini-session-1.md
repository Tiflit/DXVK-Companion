# AI Engineering Journal: Prove Persisted Install-Update-Restore Lifecycle

**Date**: 2026-10-17  
**Session**: Gemini Session 1  
**Primary Issue**: [#154](https://github.com/Tiflit/DXVK-Companion/issues/154)  
**Base Revision**: `f1e547d3ffd199c4974132b8cef80d107bdc1375` (`origin/main`)  

---

### 1. Objective & Scope

Issue [#154](https://github.com/Tiflit/DXVK-Companion/issues/154) validates the persisted installation lifecycle through the application's manager layer:
- Install version A (`2.5`) through `DxvkManager`'s supported entry point (`RequestEnableByPathAsync`).
- Reconstruct fresh stores and collaborators from disk.
- Update to version B (`2.6`) through the manager's real update request (`RequestUpdateByPathAsync`) using an isolated preseeded release cache.
- Reload stores from disk and restore the original baseline through the manager (`RequestDisableByPathAsync`).
- Repeat restore and perform a normal `ManagedFileInspector` scan after another store reload to verify stable baseline validation without target mutation or spurious attention/refusal.
- Companion negative path: replace a managed target with foreign bytes before restore; assert that the manager refuses prior to any target mutation, preserves all target/config/backup bytes, retains active management and refusal evidence, and persists failure across reloads.
- Zero network requests asserted via a rejecting/counting `HttpMessageHandler`.
- Boundaries: Test-only and journal changes. No production modifications, local host builds/tests, app/game execution, or archive reconstruction.

---

### 2. Implementation

1. **`tests/DXVKCompanion.PhaseA.Tests/PersistedDeploymentLifecycleAcceptanceTests.cs`**:
   - `PersistedLifecycle_InstallUpdateRestore_PreservesBaselinesAndRestoresByteIdentical`:
     - Creates isolated synthetic directories for game, storage, and DXVK source releases using `SyntheticTestDirectory`.
     - Game starts with native `d3d11.dll`, absent `dxgi.dll`, and preexisting `dxvk.conf` containing distinctive user comments and custom settings (`dxgi.syncInterval = 1`, `dxvk.hud = compiler`). Captures original file identities.
     - Creates distinguishable synthetic release trees for versions `2.5` and `2.6` (each with `x64` payloads).
     - Installs version `2.5` using `manager.RequestEnableByPathAsync`: asserts deployed `2.5` payloads, persisted `2.5` version in library and profile stores, `RestorationState.Managed`, and original baseline backups for `d3d11.dll` and `dxvk.conf`.
     - Reconstructs all stores from disk, preseeds release cache with `2.6`, and updates via `manager.RequestUpdateByPathAsync`: asserts deployed `2.6` payloads, updated `2.6` managed version, and that original baselines and backups still reference the original pre-installation content rather than version `2.5` wrapper files.
     - Reconstructs stores from disk and restores original baseline via `manager.RequestDisableByPathAsync`: asserts byte-identical restoration of native `d3d11.dll` and preexisting `dxvk.conf`, clean removal of `dxgi.dll`, cleared `ManagedDxvkVersion`, `RestorationState.Restored`, and disabled profile (with historical profile version permitted).
     - Reconstructs stores from disk, repeats restore, and scans with `ManagedFileInspector`: asserts successful no-op validation, no target mutation, `scanChanged == false`, no refusal or attention flags, and consistent file states.
     - Verifies `RejectingCountingHandler.RequestCount == 0`.
   - `PersistedLifecycle_WhenManagedTargetTamperedBeforeRestore_RefusesWithoutMutation_PreservesStateAndEvidence`:
     - Follows the install A -> reload -> update B -> reload lifecycle.
     - Tampers with `d3d11.dll` using foreign bytes before requesting restore.
     - Snapshots complete relative file sets and byte identities (`Sha256`, `Size`, `Content`) for both the synthetic game directory and backup directory before restore, asserting non-vacuous inclusion of all expected targets (`Game.exe`, `d3d11.dll`, `dxgi.dll`, `dxvk.conf`) and both DLL and config original baseline backups (`d3d11.dll` and `dxvk.conf`).
     - Asserts restore fails (`DxvkActionResult.Failed`), sets `RestorationState.AttentionRequired`, and populates `LastRefusalReason` with ownership mismatch evidence.
     - Asserts zero target/backup mutation: complete game and backup directory snapshots (relative path sets, SHA-256 hashes, file sizes, and raw byte contents) are identical before and after refusal; foreign `d3d11.dll` bytes are untouched, `dxgi.dll` remains present, `dxvk.conf` is preserved, and both DLL and configuration backup files in the backup directory are completely unmodified.
     - Asserts active management is retained (`profile.DxvkEnabled == true`, `installation.ManagedDxvkVersion == "2.6"`).
     - Reconstructs fresh stores from disk and confirms failure and active management are persisted.
     - Verifies zero HTTP requests made.

---

### 3. Verification & Governance

- Scope check: Strictly tests and journal (`PersistedDeploymentLifecycleAcceptanceTests.cs`, `2026-10-17-persisted-deployment-lifecycle-gemini-session-1.md`).
- Local host execution: `NOT RUN (pending human authorization)` under AGENTS.md.
- Cloud CI: Complete verification via GitHub Actions on Windows runner.
