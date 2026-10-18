# Session Journal: Issue #173 — Refuse Unowned Restore Attempts Without Mutating Files or Reporting Success (Session 1)

- **Date:** 2026-10-18
- **Agent:** Gemini / Antigravity
- **Task:** [Issue #173](https://github.com/Tiflit/DXVK-Companion/issues/173) — *Refuse unowned restore attempts without mutating files or reporting success*
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
   - Pushed initial reproducer commit and verified expected red cloud CI run `39169251599` (898 passed, 9 failed with exact expected assertion failures).

3. **Rollback Service Boundary Hardening (`DxvkRollback.cs`):**
   - Added early ownership guards in `RestoreOriginalDllsAsync`:
     - If `installation == null`: refuses immediately with `false`, sets non-empty `LastRefusalReason` (`"Refusing rollback on {profile.ExeName}; missing ownership records for game directory '{gameDir}'."`), logs refusal, and returns `false` without modifying files or creating library records.
     - If `installation.ManagedFiles == null || installation.ManagedFiles.Count == 0`: refuses immediately via `RefuseRollback`, transitions `installation.RestorationState` to `AttentionRequired`, sets `LastRefusalReason`, and saves to store (if store does not have uncertain persistence), leaving directory files untouched.
   - Completely removed unsafe legacy fallback block (`else { ... }`) that previously moved `.bak` files, deleted arbitrary `dxvk.conf` files, and returned `true`.
   - Completely removed private helper `RestoreLegacyBakIfExists`.
   - Preserved all public constructors, signatures, and `FileUtils` compatibility.

4. **Test Oracle Correction (`SafeDeploymentIntegrationTests.cs`):**
   - Corrected obsolete test `DxvkRollback_CleansUpDxvkConfFile` -> renamed to `DxvkRollback_UnownedDxvkConf_RefusesRollbackAndPreservesConfFile`.
   - Replaced previous unsafe expectation (`Assert.True(rollbackOk); Assert.False(File.Exists(confPath));`) with strict safety verification:
     - `Assert.False(rollbackOk);`
     - `Assert.NotNull(rollback.LastRefusalReason);`
     - `Assert.Contains("missing ownership", rollback.LastRefusalReason, StringComparison.OrdinalIgnoreCase);`
     - `Assert.True(File.Exists(confPath));`
     - `Assert.Equal(confContent, File.ReadAllText(confPath));`

---

## Changed Files

- `src/DXVKCompanion/DXVK/DxvkRollback.cs`
- `tests/DXVKCompanion.PhaseA.Tests/UnownedRestoreRefusalTests.cs`
- `tests/DXVKCompanion.PhaseA.Tests/SafeDeploymentIntegrationTests.cs`
- `docs/ai-journal/2026-10-18-unowned-restore-refusal-gemini-session-1.md`

---

## Cloud CI & Verification Status

- **Initial Reproducer CI:** Run `39169251599` (Job `117798676953`) — 898 passed, 9 failed, confirming the exact bug boundary on the unpatched codebase.
- **Fix Commit CI:** Run `39169620373` (Job `117799766101`) — 906 passed, 1 failed:
  - All 10 tests in `UnownedRestoreRefusalTests.cs` passed.
  - The corrected test oracle `DxvkRollback_UnownedDxvkConf_RefusesRollbackAndPreservesConfFile` in `SafeDeploymentIntegrationTests.cs` passed.
  - 896 existing tests passed.
  - Single remaining failure: `DxvkInstaller_AdoptExisting_MixedDlls_RefusesAdoption_LeavesFilesAndOwnershipUnchanged_AndRollbackPreservesUnrelatedFile` in `tests/DXVKCompanion.PhaseA.Tests/PhaseCManualManagementTests.cs` (line 234: `Assert.True(rollbackOk); // Existing successful unmanaged no-op restore`). This test was written under the prior behavior when unmanaged/unowned restore returned `true`. Under Issue #173 Requirement 1, `RestoreOriginalDllsAsync` now strictly refuses unowned restores with `false`, causing line 234 to fail.
- **Workflow Checks on PR #174:**
  - `AI task scope`: PASS (`39169935605`)
  - `PR hygiene`: PASS (`39169935540`)
  - `Run Python workflow tests`: PASS (`39169620393`)
  - `Acquire Facts and Render Snapshot`: PASS (`39169935840`)
  - `Publish Dashboard to Dedicated Issue`: PASS (`39169618488`)

---

## Exact Minimum Additional Path Report

Per the task contract ("*Report exact minimum additional paths if an unmet acceptance actually needs them*"):
- **Additional Path Needed:** `tests/DXVKCompanion.PhaseA.Tests/PhaseCManualManagementTests.cs`
- **Location:** Line 234 in `DxvkInstaller_AdoptExisting_MixedDlls_RefusesAdoption_LeavesFilesAndOwnershipUnchanged_AndRollbackPreservesUnrelatedFile`
- **Rationale:** Similar to `SafeDeploymentIntegrationTests.DxvkRollback_CleansUpDxvkConfFile` which was authorized for oracle correction in Issue #173, `PhaseCManualManagementTests.cs` line 234 asserts `Assert.True(rollbackOk); // Existing successful unmanaged no-op restore` on an unowned directory. Updating this line to assert `Assert.False(rollbackOk);` (and verifying `LastRefusalReason`) aligns it with Issue #173 Requirement 1 and will bring the test suite to 907/907 passing (100% green).
- **Status:** Paused dependent modification pending authorized task scope expansion to add `tests/DXVKCompanion.PhaseA.Tests/PhaseCManualManagementTests.cs` to Issue #173 `### Allowed paths`.
