# AI Engineering Journal: Exit Registration Cleanup & Lifecycle Release

**Date**: 2026-10-15  
**Session**: Gemini Session 1  
**Primary Issue**: [#121](https://github.com/Tiflit/DXVK-Companion/issues/121)  
**Related Context**: [#118](https://github.com/Tiflit/DXVK-Companion/pull/118), [#120](https://github.com/Tiflit/DXVK-Companion/issues/120)  
**Base Revision**: `e71f9362f9ac71669f0b3ee1a7c4d13540f22230` (`origin/main`)  

---

### 1. Objective & Task Context

Resolve the remaining exit-registration resource-cleanup edge recorded as nonblocking in PR #118 acceptance. The goal is to ensure robust, leak-free, idempotent lifecycle management for `ProcessExitHandler` attachments and integration with `ProcessMonitor`:
- Failed watcher setup releases its registration token and unwinds any partially installed event subscriptions, without fabricating exit events.
- An exit with an unreportable (null/empty) executable path completes registration token and resource cleanup without raising misleading path-bearing events.
- Subscriber exceptions during exit notification do not prevent process disposal or resource release (`finally` guarantee).
- `ProcessMonitor` attachment bookkeeping handles setup failure by unwinding only the failed attachment token without retaining phantom live watchers, and respects synchronous exit without overwriting its completed removal.
- External process ownership is respected across all failure and exit paths.

---

### 2. Changes Made

1. **`src/DXVKCompanion/Monitoring/ProcessExitHandler.cs`**:
   - Factored watcher configuration into internal virtual seams (`SubscribeProcessExit`, `UnsubscribeProcessExit`, `CheckProcessHasExited`, `DisposeProcess`).
   - In `AttachCore`, guarded watcher setup with `try ... catch`. On setup failure (e.g., `Win32Exception` Access Denied on `EnableRaisingEvents`), unwinds any partially registered handler (`UnsubscribeProcessExit`), cancels the registration token via `CancelRegistration`, and returns `Guid.Empty`.
   - In the exit callback handler and synchronous `CheckProcessHasExited` path, wrapped `NotifyExit` in a `try ... finally { DisposeProcess(process); }` block, ensuring process disposal executes even if an event subscriber throws an exception.
   - Updated `NotifyExit` to release the registration token from `_registrations` and claim delivery (`TryClaimDelivery`) before checking `string.IsNullOrEmpty(exePath)`. When the path is unavailable, bookkeeping and resources are cleanly released without firing misleading events.
   - Added internal inspection seams `ActiveRegistrationCount` and `HasRegistration(Guid)` under `_exitLock` to observe registration state without exposing public signatures.
   - Preserved `public void Attach(Process process)`.

2. **`src/DXVKCompanion/Monitoring/ProcessMonitor.cs`**:
   - In `AttachExitHandlerOnce`, captured the token returned from `_exitHandler.AttachCore(proc, token)`. If setup fails (`Guid.Empty` or token mismatch), unwinds only that matching token from `_attachedExitProcesses`.
   - If synchronous exit occurs during setup, `HandleExit` cleans up `_attachedExitProcesses[pid]`, and `AttachExitHandlerOnce` does not overwrite the completed exit with a phantom live attachment.
   - Added internal inspection seam `IsExitWatcherAttached(int processId)` under `_pollLock`.

3. **`tests/DXVKCompanion.PhaseA.Tests/ProcessExitHandlerLifecycleTests.cs`**:
   - Added comprehensive tests for `ProcessExitHandler`:
     - Setup failure before subscription releases registration with zero retained registrations and zero events.
     - Setup failure after partial subscription unwinds subscription and releases registration.
     - Missing-path exit releases registration token and performs disposal without raising misleading path-bearing events.
     - Successful exit delivers exactly once and releases registration; duplicate callback suppressed.
     - Cancellation releases registration and suppresses delayed exit delivery.
     - Subscriber exception does not skip resource cleanup or process disposal (`finally` verified).
     - Synchronous exit delivers and cleans up during setup.

4. **`tests/DXVKCompanion.PhaseA.Tests/ProcessMonitorQualificationTests.cs`**:
   - Added integration tests for `ProcessMonitor`:
     - `AttachExitHandler_SetupFailure_UnwindsAttachmentAndPreservesExternalOwnership`: verifies that when watcher setup fails, attachment is unwound from `_attachedExitProcesses` and external process is not disposed.
     - `AttachExitHandler_SynchronousExitDuringSetup_CleansUpWithoutPhantomLiveAttachment`: verifies that when synchronous exit occurs during attachment setup, attachment state is removed by `HandleExit` and not overwritten with a phantom live watcher.

---

### 3. Verification & Evidence

- **Inspected Evidence**:
  - `src/DXVKCompanion/Monitoring/ProcessExitHandler.cs`
  - `src/DXVKCompanion/Monitoring/ProcessMonitor.cs`
  - `tests/DXVKCompanion.PhaseA.Tests/ProcessExitHandlerLifecycleTests.cs`
  - `tests/DXVKCompanion.PhaseA.Tests/ProcessMonitorQualificationTests.cs`
- **Executed Evidence**:
  - Cloud CI (GitHub Actions) on Windows runners.
  - Local builds/tests/runs: **NOT RUN** per profile instructions (`NOT RUN (pending human authorization)`).
