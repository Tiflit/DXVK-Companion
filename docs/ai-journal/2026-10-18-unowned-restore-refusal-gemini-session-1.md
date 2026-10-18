# Session Journal: Issue #173 / #175 — Refuse Unowned Restore Attempts Without Mutating Files or Reporting Success (Session 1)

- **Date:** 2026-10-18
- **Agent:** Gemini / Antigravity
- **Task:** [Issue #175](https://github.com/Tiflit/DXVK-Companion/issues/175) (follow-up completing [Issue #173](https://github.com/Tiflit/DXVK-Companion/issues/173)) — *Refuse unowned restore attempts without mutating files or reporting success*
- **Base `main` SHA:** `f77bb1743ec93bbe7b8d1ae53282c11b2b79734d`
- **Trusted Policy SHA (`AGENTS.md`, `docs/AI-WORKFLOW-KERNEL.md`, `docs/AI-ENFORCEMENT-REGISTER.md`, `docs/AI-DEVELOPMENT-WORKFLOW.md`):** `f3e9974371cb08d1dd99192bd314a135010594ed`
- **Branch:** `fix/issue-173-unowned-restore-refusal`
- **PR:** [#174](https://github.com/Tiflit/DXVK-Companion/pull/174)

---

## Completed Work

1. **Policy & Baseline Verification:**
   - Acquired and derived policy identity `f3e9974371cb08d1dd99192bd314a135010594ed` matching policy checkpoint on verified `main` (`f77bb1743ec93bbe7b8d1ae53282c11b2b79734d`).
   - Inspected machine dashboard Issue #27 and verified clean working tree.
   - Created branch `fix/issue-173-unowned-restore-refusal` from `origin/main`.

2. **Test-First Reproducers (`UnownedRestoreRefusalTests.cs`):**
   - Added comprehensive reproducer test suite `tests/DXVKCompanion.PhaseA.Tests/UnownedRestoreRefusalTests.cs` covering:
     - Absent library / unowned game directory with `dxvk.conf`: asserts `false`, non-empty `LastRefusalReason`, exact before/after byte and file-set preservation, and no manufactured library ownership records.
     - Absent library with existing DLLs and `.bak` sentinels: asserts `false`, refusal reason, and that `.bak` files are not moved/deleted and target binaries are not overwritten.
     - Pre-mutation lock tests: proves refusal occurs at the decision boundary before any file operations are attempted on locked DLLs, `.bak` files, or config files.
     - Existing installation with empty `ManagedFiles` (`None`, `Managed`, `Restored` initial states): asserts refusal, transition to `RestorationState.AttentionRequired`, persistence of refusal reason via guarded store, and preservation of game directory files.
     - Store uncertain persistence preservation: verifies store uncertainty refuses restore without attempting store writes.
     - Manager `DisableDxvkAsync` integration: verifies refusal return `false`, propagation of refusal reason, and preservation of `profile.DxvkEnabled` without mutation.
     - Manager `RestoreAllAsync` batch integration: verifies unowned active profile in mixed batch is counted in `TotalManaged` and reported as `FailedOrAttentionRequired` while valid managed games restore successfully, preserving error isolation.
     - Positive control: verifies legitimate install restore restores baseline and remains idempotent on subsequent calls.
   - Initial reproducer Cloud CI run `39169251599` (898 passed, 9 failed):
     - **8 real behavioral regressions** on unpatched code: unowned restore proceeding/succeeding/mutating without ownership records.
     - **1 assertion string calibration**: on `RestoreOriginalDllsAsync_WhenInstallationHasEmptyManagedFiles_AndStoreHasUncertainPersistence_RefusesWithoutSaving`, calibrated `Assert.Contains` rather than exact match against store diagnostic formatting.

3. **Rollback Service Boundary Hardening (`DxvkRollback.cs`):**
   - Added early ownership guards in `RestoreOriginalDllsAsync`:
     - If `installation == null`: refuses immediately with `false`, sets non-empty `LastRefusalReason` (`"Refusing rollback on {profile.ExeName}; missing ownership records for game directory '{gameDir}'."`), logs refusal, and returns `false` without modifying files or creating library records.
     - If `installation.ManagedFiles == null || installation.ManagedFiles.Count == 0`: refuses immediately via `RefuseRollback`, transitions `installation.RestorationState` to `AttentionRequired`, sets `LastRefusalReason`, and saves to store (if store does not have uncertain persistence), leaving directory files untouched.
   - Completely removed unsafe legacy fallback block (`else { ... }`) that previously moved `.bak` files, deleted arbitrary `dxvk.conf` files, and returned `true`.
   - Completely removed private helper `RestoreLegacyBakIfExists`.
   - Preserved all public constructors, signatures, and `FileUtils` compatibility.

4. **Test Oracle Corrections & Review Test Strengthening (Issue #175):**
   - Corrected obsolete test in `SafeDeploymentIntegrationTests.cs`: `DxvkRollback_CleansUpDxvkConfFile` -> renamed to `DxvkRollback_UnownedDxvkConf_RefusesRollbackAndPreservesConfFile`, asserting `false`, refusal reason citing missing ownership, and file preservation.
   - Corrected legacy unmanaged-success assertion in `PhaseCManualManagementTests.cs`: in `DxvkInstaller_AdoptExisting_MixedDlls_RefusesAdoption_LeavesFilesAndOwnershipUnchanged_AndRollbackPreservesUnrelatedFile` (line 234), changed `Assert.True(rollbackOk)` to `Assert.False(rollbackOk)` and asserted `Assert.Contains("missing ownership", rollback.LastRefusalReason, StringComparison.OrdinalIgnoreCase)` while preserving all adoption-refusal and file preservation assertions.
   - Strengthened `UnownedRestoreRefusalTests.cs`:
     - In `RestoreOriginalDllsAsync_WhenInstallationExistsWithEmptyManagedFiles_RefusesAndTransitionsToAttentionRequired`: seeded non-default configuration (`DxvkConfiguration`), pinned version (`ManagementPolicy`), and pending action (`PendingAction`); reloaded `GameLibraryStore` from disk; asserted seeded fields survive while `RestorationState.AttentionRequired` and `LastRefusalReason` persist.
     - In `DisableDxvkAsync_WhenUnownedProfile_RefusesAndLeavesProfileEnabledWithoutMutatingFiles`: reloaded `ProfileStore` from disk to verify profile state persistence without relying on in-memory store cache.
     - In `RestoreOriginalDllsAsync_WhenLegitimatelyOwned_RestoresBaselineAndRemainsIdempotent`: seeded pre-existing user `dxvk.conf` with custom user lines (`dxvk.hud = compiler\ncustom.user.setting = true\n`) before install; deployed DXVK; reconstructed store, engine, and rollback from disk; verified native baseline and custom user config lines survive restoration alongside `settings.ini`; verified repeated owned restore succeeds idempotently.

---

## Changed Files

- `src/DXVKCompanion/DXVK/DxvkRollback.cs`
- `tests/DXVKCompanion.PhaseA.Tests/UnownedRestoreRefusalTests.cs`
- `tests/DXVKCompanion.PhaseA.Tests/SafeDeploymentIntegrationTests.cs`
- `tests/DXVKCompanion.PhaseA.Tests/PhaseCManualManagementTests.cs`
- `docs/ai-journal/2026-10-18-unowned-restore-refusal-gemini-session-1.md`

---

## Cloud CI & Verification Status

- **Initial Reproducer CI:** Run `39169251599` (Job `117798676953`) — 898 passed, 9 failed (8 real behavioral regressions on unpatched code, 1 assertion formatting calibration).
- **Production Fix CI:** Run `39169620373` (Job `117799766101`) — 906 passed, 1 failed (the single failure was line 234 in `PhaseCManualManagementTests.cs` authorized for correction in Issue #175).
- **Final Green Review Repairs CI:** Run `39170748698` (Job `117803073501`) on head `3269c9a009da20a8d462479e000aeae9e7351659`:
  - **907 passed, 0 failed** across `DXVKCompanion.PhaseA.Tests` (100% green).
- **PR Hygiene & Scope Checks on PR #174 (Commit `3269c9a`):**
  - `AI task scope`: PASS (`39170748645`)
  - `PR hygiene`: PASS (`39170748724`)
  - `Run Python workflow tests`: PASS (`39170748635`)
  - `Acquire Facts and Render Snapshot`: PASS (`39170746865`)
  - `Publish Dashboard to Dedicated Issue`: PASS (`39170746865`)
