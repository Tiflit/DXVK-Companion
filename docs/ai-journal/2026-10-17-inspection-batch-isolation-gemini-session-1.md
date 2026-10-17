# AI Engineering Journal: Isolate Managed-File Inspection Failures per Installation

**Date**: 2026-10-17  
**Session**: Gemini Session 1  
**Primary Issue**: [#152](https://github.com/Tiflit/DXVK-Companion/issues/152)  
**Base Revision**: `76ab598525f3456b65f1011c06c325ff66fb0999` (`origin/main`)  

---

### 1. Objective & Defect Context

`TrayApp` startup executes `ManagedFileInspector.InspectAll` before calling `ProcessAllPendingActionsAsync`. Previously, `InspectAll` iterated over `_store.GetAll()` calling `InspectInstallation` in an unguarded loop. If an exception occurred during the inspection of any single installation (e.g. file-identity / hashing error, I/O permissions failure, or storage persistence error), the exception escaped `InspectAll`. This aborted remaining inspections and prevented the subsequent `ProcessAllPendingActionsAsync` phase from ever being reached, stranding safe pending actions such as an unrelated queued restore.

Acceptance criteria:
- An exception from one `InspectInstallation` call does not abort inspection of later installations or escape from `InspectAll` solely because that entry failed.
- A failed inspection must not be represented as verified/healthy. Mark that installation `AttentionRequired` with an explanatory inspection-failure reason, retaining active version, ownership/hash/backup records and pending intent. Best-effort recording/reporting must itself tolerate persistence/logging failure and permit batch continuation.
- Do not mutate game files, deploy, restore, clear pending actions, change APIs or rewrite baselines as an exception-handling response. Do not claim that catching an exception rolled back any earlier partial in-memory inspection updates.
- Preserve normal managed/restored baseline inspection, external-change classification, existing stale-action invalidation and recovery behavior on successful inspection. Count only normal `InspectInstallation` true results as detected external-change entries; inspection errors are not fabricated external changes.
- Add a deterministic synthetic test: first inspection throws using an existing seam (`InspectInstallation` is already virtual), later real inspection detects a changed target. Assert continuation/count, attention and retained evidence on failure, correct later detection and unchanged game/backup bytes.
- Verify failure-record persistence through fresh store reload when storage works, and continued batch behavior when best-effort failure persistence also throws. Use existing storage test seams, not a new production injection framework.
- Add a focused synthetic integration check of the existing startup order (`InspectAll` then `ProcessAllPendingActionsAsync`), showing an unrelated valid queued restore can complete after the earlier inspection failure. Existing queued restore ownership/backup validation must remain authoritative; do not bypass validation for failed inspections.
- All stores, caches, files and backups are synthetic/isolated. Do not weaken existing assertions. No real games, downloads or GUI execution.

---

### 2. Implementation

1. **`src/DXVKCompanion/Storage/ManagedFileInspector.cs`**:
   - In `InspectAll()`, wrapped each installation's inspection invocation in a guarded `try ... catch (Exception ex)` block:
     - Catches exceptions thrown during inspection of that installation.
     - Logs the target display name or installation path and exception message via `Logger.Log`, wrapped in a try/catch so logging failures do not prevent continuation.
     - Does not increment `changesDetected` (failed inspections are not counted as detected external changes).
     - If the installation reference is non-null:
       - Sets `RestorationState = RestorationState.AttentionRequired` to ensure it is not represented as healthy or verified.
       - Sets `LastRefusalReason` with an explanatory failure message (`Inspection failed: {ex.Message}`).
       - Retains active version (`ManagedDxvkVersion`, `ManagedDxvkArchitecture`), ownership/hash/backup records (`ManagedFiles`), and pending intent (`PendingAction`).
       - Does not mutate game files, deploy, restore, clear pending actions, change APIs, or rewrite baselines.
       - Best-effort persists the failure state via `_store.Save(installation)`, guarded by a try/catch block so persistence failures also do not prevent batch continuation.
     - The loop continues to inspect all remaining installations.
   - In `InspectInstallation()`, implemented inspector-owned reason retirement:
     - When an installation achieves genuinely complete successful verification (`allFilesVerifiedConsistent && installation.ConflictFlags == InstallationConflictFlags.None`):
       - Recovers `RestorationState` to `Managed` or `Restored`.
       - If `LastRefusalReason` starts with `"Inspection failed"` (case-insensitive), clears `LastRefusalReason` to `null`.
       - Unrelated refusal reasons (e.g. restore refusal, adoption refusal, deployment refusal) are strictly preserved.
       - Unresolved states (missing directories, missing/unusable file evidence, remaining conflict flags) do not clear the failure reason.

2. **`tests/DXVKCompanion.PhaseA.Tests/ManagedFileInspectorBatchIsolationTests.cs`**:
   - `FirstInspectionThrows_ContinuationInspectsLaterTargetAndDetectsExternalChange`:
     - Overrides `InspectInstallation` using existing virtual method seam on `ManagedFileInspector` to simulate an inspection exception on Game 1.
     - Verifies both installations are inspected (`InspectCalls == 2`).
     - Verifies count is 1 (only Game 2's external change is counted; Game 1 is not counted).
     - Verifies Game 1 is marked `AttentionRequired` with `LastRefusalReason` containing the failure message.
     - Verifies Game 1 retains active DXVK version (`2.5`), managed files, and pending action intent.
     - Verifies Game 1's game directory files and backup directory bytes remain strictly unchanged.
     - Verifies Game 2's external change is correctly detected and marked `AttentionRequired` / `ExternallyChanged`.
     - Verifies failure state and external change persistence across a fresh store reload.
   - `InspectionFailure_PersistenceFailure_PermitsContinuedBatchProcessing`:
     - Uses existing storage test seam `store.CustomTempWriter` to simulate a storage I/O write error when saving Game 1's failure state.
     - Verifies Game 2 is still inspected and its external change detected and persisted.
   - `StartupOrder_InspectionFailure_DoesNotPreventSubsequentPendingActionRestoreFromCompleting`:
     - Replicates `TrayApp.InitializeStartupStateAsync` startup order (`InspectAll` followed by `ProcessAllPendingActionsAsync`).
     - Game 1 has an inspection failure and a queued restore on a tampered target DLL.
     - Game 2 has a valid queued restore on a consistent managed installation with intact backup.
     - Verifies `InspectAll` completes without throwing.
     - Verifies `ProcessAllPendingActionsAsync` restores Game 2 successfully, returning processed count of 1.
     - Verifies authoritative ownership/backup validation on Game 1 is NOT bypassed: Game 1 restore is refused due to target hash mismatch, and tampered files are not restored.
   - `TransientFailure_PersistedReload_HealthyRescan_RetiresInspectionFailureReason`:
     - Verifies transient failure persists `AttentionRequired` and `LastRefusalReason = "Inspection failed..."` across store reload.
     - Verifies subsequent healthy rescan achieves complete verification, restores state to `Managed`, and retires `LastRefusalReason` to `null`.
     - Verifies retired healthy state is durably persisted across fresh store reload.
   - `UnresolvedInspection_DoesNotRetireInspectionFailureReason`:
     - Verifies negative cases: missing directories, missing/tampered files, and active conflict flags do NOT clear the inspection failure reason.
   - `UnrelatedRefusalReason_PreservedAcrossHealthySuccessfulInspection`:
     - Verifies unrelated refusal reasons (e.g. adoption/restore refusals) are preserved intact across a healthy successful inspection.

---

### 3. Verification & Governance

- Addressed Codex review on PR #153: implemented inspector-owned reason retirement lifecycle upon complete successful verification, preserved unrelated refusal reasons, and added regression/negative test coverage.
- Local host execution: `NOT RUN (pending human authorization)` under AGENTS.md.
- Verified via GitHub Actions Cloud CI on Windows runners (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`, `Run Python workflow tests`).
