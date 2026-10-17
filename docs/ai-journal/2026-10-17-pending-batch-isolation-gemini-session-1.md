# AI Engineering Journal: Isolate Queued-Action Batch Failures per Installation

**Date**: 2026-10-17  
**Session**: Gemini Session 1  
**Primary Issue**: [#150](https://github.com/Tiflit/DXVK-Companion/issues/150)  
**Base Revision**: `e2473c386c0c0e3ed718065c2ea657a684092c3b` (`origin/main`)  

---

### 1. Objective & Defect Context

`DxvkManager.ProcessAllPendingActionsAsync` previously handled all queued installations in an unguarded loop. If an exception was thrown during one installation's processing (for instance, if an `OnPendingActionCancelled` subscriber threw an exception during cancellation notification, or if a transient I/O / storage failure occurred), the exception propagated directly out of `ProcessAllPendingActionsAsync`, aborting the batch loop and stranding subsequent safe pending actions (such as an unrelated queued restore).

Acceptance criteria:
- An installation-specific exception does not abort processing of later queued installations. Log enough existing-style context to diagnose that entry; failure reporting must not itself prevent continuation if optional persistence fails.
- Increment the returned processed count only for actions confirmed successful through the normal completion path. Failed/cancelled/skipped entries are not successes.
- Technical failures before operation completion retain pending intent for retry. Do not turn them into terminal compatibility cancellations, clear unrelated queues, or claim file rollback merely because an exception was caught.
- Compatibility cancellation stays terminal and durable, with automatic non-revival preserved. If notification throws after cancellation was persisted, later work still proceeds; a subsequent batch must not retry that cancelled deployment or repeat its transition notification.
- Preserve running-game deferral, Restore's compatibility exemption, ownership/hash/original-backup validation, supported API reassessment and ordinary successful pending-action cleanup.
- Add a deterministic synthetic regression with an incompatible pending deployment first, a throwing cancellation subscriber, and a valid unrelated queued restore later. Assert no forbidden deployment, durable cancellation, later original-baseline restoration, accurate processed count and no cancellation replay.
- Add a bounded technical-error case before action completion (using existing test seams or ordinary synthetic inputs) proving later work proceeds while the failed entry's pending intent remains; include reload assertions where persistence is relevant. No new production fault-injection framework.
- Tests use isolated synthetic stores/files/backup/cache paths, no real games, no external downloads, no source-text assertions or weakened existing assertions.

---

### 2. Implementation

1. **`src/DXVKCompanion/DXVK/DxvkManager.cs`**:
   - Wrapped the body of each installation iteration in `ProcessAllPendingActionsAsync` in a `try ... catch (Exception ex)` block.
   - When an exception occurs during the processing of an installation:
     - Catches the exception to isolate the failure to that single installation.
     - Logs the target display name or installation path, action type, and exception message via `Logger.Log`.
     - Failure reporting is itself protected so logging/persistence failures cannot prevent loop continuation.
     - Does not increment `processed` count (only actions completing via `success == true` increment `processed`).
     - Leaves `PendingAction` intact on technical failure so intent is retained for retry.
     - Does not mutate `LastCancelledAction` or perform spurious rollback on caught exceptions.
     - If cancellation was already persisted by `CancelIncompatiblePendingAction` before an event subscriber threw, cancellation remains terminal and durable across reload, and subsequent batch runs will not re-execute or re-notify.

2. **`tests/DXVKCompanion.PhaseA.Tests/PendingActionBatchIsolationTests.cs`**:
   - `IncompatibleDeployment_ThrowingCancellationSubscriber_DoesNotAbortUnrelatedQueuedRestore`:
     - Verifies an incompatible DX12 deployment with throwing `OnPendingActionCancelled` subscriber does not prevent an unrelated queued DX11 restore from completing.
     - Verifies zero forbidden deployment on game 1, durable cancellation in reloaded store, original baseline restoration on game 2, returned processed count of 1, and no cancellation replay on second batch call.
   - `TechnicalError_BeforeActionCompletion_RetainsPendingIntentAndAllowsLaterActionsToProceed`:
     - Uses existing test seam `profileStore.CustomTempWriter` to simulate a transient I/O error during game 1's profile save.
     - Verifies game 2 completes successfully while game 1 retains its pending intent in memory and across reload.
     - Verifies game 1 completes on subsequent batch run after transient error condition clears.
   - `UnhandledException_DuringInstallationResolution_DoesNotAbortRemainingInstallations`:
     - Verifies an unresolvable or faulty directory path on one installation does not abort remaining queued installations.

---

### 3. Verification & Governance

- Local host execution: `NOT RUN (pending human authorization)` under AGENTS.md.
- Verified via GitHub Actions Cloud CI on Windows runners (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`, `Run Python workflow tests`).
