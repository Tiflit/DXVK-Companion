# AI Activity Journal: Post-Merge Lifecycle CI Failure Diagnostics (Session 1)

- **Date:** 2026-10-26
- **Implementer:** Gemini (Argon)
- **Reviewer:** Codex
- **Merge Authority:** Human Owner Alone
- **Primary Issue:** [#307](https://github.com/Tiflit/DXVK-Companion/issues/307)
- **Relay Reference:** `D:/dev/companion-coordination/process-lifecycle-v1-preparation-20261025/ISSUE-307-GEMINI-DIAGNOSTICS-RELAY.md`
- **Policy Identity:** `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9` (Kernel v0.4)
- **Base Commit:** `e11f67e654aa3a9f6a5d2ac4499dac621e0483ab` (merged PR #305)
- **Base Tree:** `430dcf3177b34e244af7ef42a035ba327712a244`

---

## 1. Context and Problem Statement

Following the merge of PR #305 (`e11f67e654aa3a9f6a5d2ac4499dac621e0483ab`), which passed all 1,388 unit and integration tests across 6 hosted CI workflows in run 40381600570, the post-merge `Build and Test` workflow on `main` (run 40383187314, attempt 1, job 121784508110) failed 2 tests (1,386 passed, 2 failed):
1. `IncompatiblePendingActionLifecycleTests.NotificationSignal_FiresOnceOnCancellationTransition_DoesNotRepeatOnSubsequentExitOrStartup`:
   - Failure: `Assert.Single() Failure: The collection was empty` at line 235.
2. `ManagedFileInspectorRestoredTests.RestoredLifecycle_OriginallyExisting_MissingFile_FlagsAttention_AndPersistsAcrossScans`:
   - Failure: `Assert.True() Failure: Expected: True, Actual: False` at line 221 during initial setup (`await installer.ApplyToGameAsync(profile, release)`).

Runtime batch 2 is prepared but strictly held pending resolution and diagnostics of these CI failures.

---

## 2. Established Facts vs. Plausible Hypotheses

### Established Facts
1. **Diagnostic Masking in `NotificationSignal_FiresOnceOnCancellationTransition_DoesNotRepeatOnSubsequentExitOrStartup`:**
   - In Act 1, `manager.ApplyPendingAsync(exePath)` is asserted only via `Assert.False(result1)`.
   - `ApplyPendingAsync` returns `false` both when successfully executing `CancelIncompatiblePendingAction` AND when an earlier precondition fails (such as directory non-existence, operation lease timeout, store modification check refusal, or process activity inspection refusal).
   - Because `Assert.False(result1)` passed unconditionally, any earlier refusal prevented `CancelIncompatiblePendingAction` from running, leaving `receivedNotifications` empty.
   - The downstream assertion `Assert.Single(receivedNotifications)` subsequently threw an empty collection failure, masking the earliest failed stage and any recorded refusal reasons.
2. **Diagnostic Masking in `RestoredLifecycle_OriginallyExisting_MissingFile_FlagsAttention_AndPersistsAcrossScans`:**
   - At line 221, `installer.ApplyToGameAsync(profile, release)` returned `false`.
   - The test used a bare `Assert.True(await installer.ApplyToGameAsync(...))`, providing zero diagnostic context.
   - When `ApplyToGameAsync` returns `false`, it sets `LastRefusalReason` and records `LeaseCoordinator.LastAcquisitionError` and `LastAcquisitionDiagnostic`. The bare assertion discarded all of this evidence.

### Plausible Hypotheses (Unproven)
- **Lease Contention under Parallel Runner Load:**
  - Both tests involve operation scope leases with a 500ms timeout (`TimeSpan.FromMilliseconds(500)`).
  - In `IncompatiblePendingActionLifecycleTests`, the default `OperationLeaseCoordinator.Default` uses `%LocalAppData%\DXVKCompanion\locks`, which is shared across parallel test runners on the hosted VM. Under heavy runner CPU scheduling contention, lease acquisition or directory setup can exceed 500ms.
  - In `ManagedFileInspectorRestoredTests`, `SyntheticSafetyFixture.CreateInstaller` and `SyntheticSafetyFixture.CreateRollback` create separate coordinator instances referencing the same synthetic coordination directory.
- **Store Modification Permission Check or Activity Refusal:**
  - `TryCheckCanModifyLibrary` or process activity inspection could return false under specific runner process states.
- *Status:* These hypotheses remain unproven until explicit diagnostic evidence is captured on hosted CI or reproduced.

---

## 3. Allowed Paths Contract

In accordance with Issue #307, this assignment strictly modifies only:
- `tests/DXVKCompanion.PhaseA.Tests/IncompatiblePendingActionLifecycleTests.cs`
- `tests/DXVKCompanion.PhaseA.Tests/ManagedFileInspectorRestoredTests.cs`
- `docs/ai-journal/2026-10-26-main-lifecycle-ci-diagnostics-gemini-session-1.md`

### Explicit Constraints Maintained:
- Zero edits to production code, shared test fixtures (`SyntheticSafetyFixture`), CI workflows, dependencies, or policy documents.
- Zero increases to lease timeouts, zero test serialization, zero retry assertion loops, zero test skips or relaxing of assertions.
- Zero weakening or removal of existing assertions (success, exactly-once notification, no-repeat, restoration, persistence, and attention-required assertions are all preserved).
- Sanitized diagnostics: all synthetic paths, user profiles, and temp directories are cleanly sanitized (`<gameRoot>`, `<storageRoot>`, `<userProfile>`, `<temp>`) with zero machine secrets or environment dumps.
- Hosted CI verification only; zero local test execution or script probes.

---

## 4. Diagnostic Enhancements Implemented

### In `IncompatiblePendingActionLifecycleTests.cs`:
- Added private static helpers `SanitizeDiagnostic` and `BuildApplyPendingDiagnostic`.
- Captured `manager.LastRefusalReason`, `LeaseCoordinator.LastAcquisitionError`, `OperationLeaseCoordinator.LastAcquisitionDiagnostic`, `installation.PendingAction`, `installation.LastCancelledAction`, `installation.LastCancellationReason`, `installation.LastCancellationOutcome`, `installation.LastRefusalReason`, `installation.RestorationState`, and `notificationCount`.
- In `NotificationSignal_FiresOnceOnCancellationTransition_DoesNotRepeatOnSubsequentExitOrStartup`:
  - Act 1: `Assert.False(result1, ...)` includes diagnostic context.
  - Assert 1: Pre-checked `Assert.True(receivedNotifications.Count == 1, ...)` reporting earliest failed stage diagnostics before `Assert.Single(receivedNotifications)`.
  - Act 2 & 3: Preserved assertions and attached diagnostic context to exit and startup triggers.
- Applied consistent diagnostic messages across setup and operation assertions in neighboring tests (`ExitTrigger_...`, `StartupTrigger_...`, `PersistedReload_...`, `NonRevival_...`, `FreshRequest_...`, `PublicRequestOrchestration_...`, and `RestoreInvariant_...`).

### In `ManagedFileInspectorRestoredTests.cs`:
- Added private static helpers `SanitizeDiagnostic`, `BuildInstallerDiagnostic`, and `BuildRollbackDiagnostic`.
- Captured `installer.LastRefusalReason`, `rollback.LastRefusalReason`, `LeaseCoordinator.LastAcquisitionError`, `OperationLeaseCoordinator.LastAcquisitionDiagnostic`, `profile.Api`, `profile.Architecture`, `release.Version`, `installation.RestorationState`, `installation.ConflictFlags`, `installation.LastRefusalReason`, and synthetic executable/DLL file existence.
- In `RestoredLifecycle_OriginallyExisting_MissingFile_FlagsAttention_AndPersistsAcrossScans`:
  - Initial setup: Replaced bare `Assert.True` with explicit stage-labeled diagnostics distinguishing initial installation and initial rollback failures from the inspector feature test.
  - Scan 1 & Scan 2: Added scan-level diagnostic reporting (`scanChanged`, `RestorationState`, `ManagedFileState`, and file existence).
- Applied consistent stage-labeled diagnostics across initial setup calls in all tests in the file (`Install_Restore_Inspect_...`, `RestoredLifecycle_OriginallyMissing_...`, `RestoredLifecycle_OriginallyExisting_ModifiedFile_...`, `RestoredLifecycle_PostRestoreMismatch_...`, `RestoredLifecycle_ConflictFlagsPresent_...`, and `ActiveManagement_ProtectionsPreserved_...`).

---

## 5. Factual Execution Ledger

| Operation | Tool / Invocation | Description | Impact |
|---|---|---|---|
| Git Status Check | `git status` | Verify clean working tree on startup | Verified clean on prior branch |
| View Issue #307 | `gh issue view 307` | Inspect Issue #307 contract and constraints | Acquired contract scope and boundaries |
| Git Fetch | `git fetch origin main` | Fetch latest upstream main revision | Fetched `origin/main` |
| Rev Parse | `git rev-parse origin/main` | Verify commit identity `e11f67e6...` | Matched verified baseline |
| Tree Parse | `git cat-file -p origin/main` | Verify tree identity `430dcf31...` | Matched verified tree |
| Policy Check | `git log -1 origin/main` | Verify policy hash `c40eb13d...` | Matched Kernel v0.4 |
| Branch Creation | `git checkout -b feat/issue-307-lifecycle-test-diagnostics origin/main` | Create bounded diagnostic feature branch | Created branch tracking `origin/main` |
| Source Inspection | `read_file` / `git grep` | Inspect failure lines, `DxvkManager`, `DxvkInstaller`, `OperationLeaseCoordinator` | Traced failure paths and exposed diagnostics |
| LF Conversion | PowerShell UTF8 without BOM write | Ensure clean LF line endings on disk | Normalized file line endings |
| Edit File | `edit_file` | Add diagnostic helpers and stage assertions to `IncompatiblePendingActionLifecycleTests.cs` | Added failure-path diagnostics |
| Edit File | `edit_file` | Add diagnostic helpers and stage assertions to `ManagedFileInspectorRestoredTests.cs` | Added failure-path diagnostics |
| Write Journal | `write_file` | Create session journal | Created this document |

*Local Test Execution:* Exactly 0 tests executed locally. Zero PowerShell script dot-sourcing, exploratory syntax probes, helper/app launches, or artifact downloads. Hosted CI is the sole verification authority.

---

## 6. File Checksums and Line-Ending Verification

All 3 files are normalized with LF (`\n`) line endings and UTF-8 encoding without BOM:

| Path | Bytes | SHA-256 |
|---|---|---|
| `tests/DXVKCompanion.PhaseA.Tests/IncompatiblePendingActionLifecycleTests.cs` | 37247 | `664cd1f83ef691668daf2a8cf03911447945d3471f6cedcc4ae89dcea338d83e` |
| `tests/DXVKCompanion.PhaseA.Tests/ManagedFileInspectorRestoredTests.cs` | 35985 | `2874c491e88a08fc8227ece964d7ddf973b2f2265769c55caca833e3ebe3fc6d` |
| `docs/ai-journal/2026-10-26-main-lifecycle-ci-diagnostics-gemini-session-1.md` | (computed at commit) | (computed at commit) |

