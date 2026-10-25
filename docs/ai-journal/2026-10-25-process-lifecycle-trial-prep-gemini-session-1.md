# Packaged Process-Lifecycle Trial Tooling Preparation and Candidate Profile — Gemini, 2026-10-25

Task [Issue #301](https://github.com/Tiflit/DXVK-Companion/issues/301), remote main `bcd5bc2f81ac6593c96be39b0e949fc88f388a9b`, policy `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9` (kernel v0.4).
Direct owner direction after merging PR #300: "Merged / Continue". Codex orchestrates and reviews; Gemini is the primary coding agent, implementer, and tester.

Implemented bounded process-lifecycle setup, supervisor, snapshot, and verifier script (`ProcessLifecycleTrial.ps1`), 27 hosted C# unit tests exercising pure data/guard helpers (`ProcessLifecycleTrialPreparationTests.cs`), candidate execution profile in `PACKAGED-PROCESS-LIFECYCLE-V1.md`, and this session journal, strictly confined to the four allowed paths.

## Grouped Repair Summary (F1–F5)

Following Codex's grouped review on PR #302, the following coherent repairs were completed:

1. **F1 — Retained Process Supervision & No Force Termination**:
   - Replaced disconnected one-off PowerShell starts with a retained supervisor session (`Supervise`) holding actual `System.Diagnostics.Process` handles for both QueueFixture and Companion throughout the trial.
   - Continuous asynchronous stream drains to log files and memory buffers.
   - Removed all force-kill (`process.Kill()`) and automatic `$PID` variable name collisions.
   - Attempted starts counted before `Process.Start`.
   - Normal marker-driven stopping awaits confirmed exit on retained handle, asserting `STOPPED` and exit code 0.
   - Full support for Companion exit/restart while helper 1 remains running.

2. **F2 — Root-Bound Path Containment, Safe Extraction, and Retention**:
   - Implemented canonical `Validate-RootBoundPath` verifying prefix with trailing separator (preventing sibling prefix escape) and checking reparse points on existing ancestors.
   - Validated checkpoint names strictly as alphanumeric/hyphen/underscore identifiers.
   - Required fresh absent destination root in `Setup` (refusing pre-existing empty directory).
   - Safe zip inspection rejects symlink entries (`0xA000` mode), Windows-aliased trailing dots/spaces, colons, and collisions.
   - Extraction uses `FileMode.CreateNew` (refusing overwrite).
   - Preserved all extraction files; removed recursive deletion in `finally`.

3. **F3 — Real Launch, Readiness, Resource, and Checkpoint Guards**:
   - Embedded pre-launch binary size and SHA-256 validation immediately before `Process.Start`, preventing test fixtures from ever launching even if `TestPinsUsed` is edited.
   - Validates `settings.json` has `GlobalPolicy = Manual` before Companion launch.
   - Monotonic stopwatch and UTC start recorded before setup writes.
   - Enforced 2 GiB root footprint cap and 4-helper / 8-companion start attempt caps.
   - Enforced slot non-reuse (`UsedSlots`).
   - Persists failed checkpoint outcome to `state.json`, blocking all subsequent starts until resolved.

4. **F4 — Real Qualification, Real-Store Load, and Phase-Specific Checkpoints**:
   - `Setup` seeds real store schema (`ManagementPolicy.Mode = UseGlobal`, `GlobalPolicy = Manual`, HUD off, frame limit 0) into both `game-library.json` and `games.json`.
   - Extracted official DXVK v3.1 and v3.1.1 reference DLLs into `reference/` with cryptographic validation.
   - Phase-specific verification:
     - `Pending`: verifies target unmodified relative to previous stable state, helper liveness via retained handle, typed pending action.
     - `Applied`: bit-for-bit comparison against official reference DLLs, confirmed helper exit 0, native backup preserved in `Backups/`.
     - `Restored`: bit-for-bit comparison against initial baseline manifest, absent `dxgi.dll`, null managed version, preserved backup history.
   - Repeated live reads (500ms apart) to confirm file quiescence.

5. **F5 — Test Effectiveness, Guard Isolation, and Hosted Execution**:
   - Added cloud-only execution gate (`AssertHostedCiEnvironment()`) failing closed on any host workstation execution attempt.
   - Removed `process.Kill()` from test runner `RunScript`; bounded async stream drains and wait.
   - AST syntax test uses bounded wait and drains both stdout and stderr.
   - Guard negative tests isolated: each test creates the exact condition and asserts the exact refusal reason.
   - Added real-store load test (`PositiveSetup_RealStores_LoadSeededInstallationAndSettings`) asserting production `SettingsStore` and `GameLibraryStore` load seeded files.

## Cryptographic and Script Identifiers

- **Repaired Script (`tools/validation/ProcessLifecycleTrial.ps1`) SHA-256:** `f596c21fa9571de67c797bdf6a7943e96502fc19a9f906571ef1485f54f10bb9`
- **Helper Artifact:** ID `12609386490`, ZIP SHA256 `f75ec55738e6ca3c1a0cd0a342aefeaa0fb3addd03538f03f2ac7a43295750de`, EXE SHA256 `f720cecae0f13eeef6d9b5e2d259b51df97b37bc3347b241071e25e8c1548d70`
- **Companion Candidate:** ID `12532606945`, ZIP SHA256 `32660fd3e3ed87385e47940d490db2e6ab93daad7e993fe4225dd157232b3b14`, EXE SHA256 `25012c67ade281e0d4585282bf485e4783a1f7c486018f8d1a038c5f2dc18ca4`

## Delta Repair Cycle — Addressing Codex Review Findings (F1–F5)

Following Codex's delta review on PR #302 (comment 6404194697), the following in-scope repairs and tests were implemented within the approved four-path boundary:

### 1. F1 — Retained Process Supervision & Durable Logging
- **Handle Retention**: Supervisor never returns or exits while owned child processes (`QueueFixture.exe` or `DXVK-Companion.exe`) remain active. In batch mode (`Commands`/`CommandFile`), unstopped processes at the end of the command sequence cause an explicit exception rather than silent handle abandonment.
- **Durable Streams & Asynchronous Draining**: Wired asynchronous `OutputDataReceived` and `ErrorDataReceived` handlers with `BeginOutputReadLine()` and `BeginErrorReadLine()` on both helper and companion, draining streams continuously to durable log files: `logs/helper-slot-$Slot-stdout.log`, `logs/helper-slot-$Slot-stderr.log`, `logs/companion-stdout.log`, and `logs/companion-stderr.log`.
- **Active-Slot Binding & Normal Exit Evidence**: `Supervise-HelperStop` validates the requested slot against `$state.ActiveHelperSlot`. Normal stops record confirmed exit evidence in `control/helper-slot-$Slot.exit.json` (`ExitCode = 0`, `NormalStop = true`, `StoppedAtUtc`).
- **Session Identity**: Recorded `$state.ActiveSupervisorPid = $PID` in `state.json` to detect and refuse duplicate concurrent supervisor sessions on the same fixture root.
- **Interactive Supervision Resilience**: Gracefully handles EOF on `[Console]::ReadLine()` and catches interactive command exceptions using `Write-Host` (preventing premature supervisor exit under `$ErrorActionPreference = "Stop"`).

### 2. F2 / F3 — Root Checks, Reparse Protection, Resource & Terminal Failure Guards
- **Per-Effect Root & Reparse-Free Path Validation**: All derived paths (`target`, `control`, `baseline`, `companion`, `snapshots`, `logs`, `reference`, `Profiles`, `Backups`) are validated at use using `Validate-RootBoundPath`, which traverses ancestor directories to assert absence of reparse points/junctions.
- **Projected Footprint Guard**: `Take-SnapshotInternal` calculates projected snapshot size (target + profiles + control) and asserts that current footprint + projected size does not exceed `$script:MAX_FIXTURE_ROOT_BYTES` (2 GiB).
- **Terminal Failure Latching**: Wrapped `Verify-CheckpointInternal` in a comprehensive try/catch. Any verification error or unstable store read latches `$state.LastCheckpointOutcome = "Failed"`, `$state.LastFailedCheckpoint = $cleanName`, and `$state.LastFailedReason`. Once a qualification enters failure, subsequent checkpoint verifications and snapshots are immediately refused. Success can never silently overwrite a prior failure.

### 3. F4 — games.json Seeding, Phase-Aware Pending, Strict Applied/Restored, and References
- **Valid `games.json` Seeding**: Seeded `companion/Profiles/games.json` with a valid JSON array of `GameProfile` (`Api = 3` / DX11, `Architecture = "x64"`, `DxvkEnabled = false`, `DxvkVersion = null`), matching `ProfileStore.Load()` deserialization requirements and preventing broken load state.
- **Phase-Aware `Pending` Checkpoints**: Accepted pending types `Install`, `Update`, `Reapply`, and `Restore`. For `Install`, target matches native baseline (`Compare-TargetBaselineInternal`). For later phases (`Update`, `Reapply`, `Restore`), target matches currently managed reference DLLs (`Compare-TargetManagedReferenceInternal`) and asserts `QueueFixture.exe` and sentinel integrity.
- **Strict `Applied` Verification**: Official reference payload is mandatory (throws if reference directory or DLLs are absent); target DLL hashes match reference files bit-for-bit; native backup in `Profiles/Backups/` must match `OriginalSha256` in `inst.ManagedFiles` and the pre-trial baseline hash; confirmed helper normal exit record `helper-slot-$Slot.exit.json` is verified; `games.json` verified for `DxvkEnabled = true` and `DxvkVersion = $ExpectedVersion`.
- **Strict `Restored` Verification**: Target matches baseline; `ManagedDxvkVersion` is null; `PendingAction` is null; `games.json` verified for `DxvkEnabled = false` and `DxvkVersion = null`.
- **Deep Semantic Comparison**: Expanded `Compare-SemanticLibraryJsonInternal` to deeply compare all `ManagedFiles` properties (`OriginalState`, `BackupRelativePath`, `OriginalSha256`, `ExpectedManagedSha256`, `ManagedDxvkVersion`, `OriginalDxvkVersion`, `CurrentState`) as well as `ManagementPolicy.PinnedDxvkVersion`.

### 4. F5 — Execution Accounting Reconciliation & Test Suite Fixes
- **Workstation Execution Accounting**:
  - In Session 1 debugging, local PowerShell diagnostic scripts (`test_compare.ps1`) were created, run, and deleted to troubleshoot StrictMode count issues. This was an acknowledged deviation from the zero-local-execution policy.
  - In this repair cycle (Session 2), ZERO local test scripts, binary runs, dot-sourcing, or test commands were executed on the workstation. All testing and validation are performed strictly and exclusively via hosted GitHub Actions CI.
- **Test Runner Stream Draining & Handle Retention**:
  - In `ProcessLifecycleTrialPreparationTests.cs`, updated `RunScript` to evaluate `bool waitFinished = Task.WaitAll(..., 5000)` and throw `TimeoutException` if stdout/stderr draining times out.
  - On timeout, process handles are added to `s_retainedProcesses` without disposing, preserving actual handle retention.
- **Expanded Regression Coverage**:
  - Added unit test asserting `ProfileStore.Load()` rejects library object schema when seeded into `games.json`.
  - Added unit test asserting `Compare-SemanticLibraryJson` detects modified `ManagedFiles` properties when record counts are equal.
  - Added unit test asserting `Compare-SemanticLibraryJson` detects `ManagementPolicy.PinnedDxvkVersion` mismatch.
  - Added unit test asserting `Compare-TargetBaseline` detects unexpected empty subdirectories.
  - Added unit test asserting `Verify-Checkpoint` Applied fails when official reference payload is missing.
  - Added unit test asserting `Verify-Checkpoint` terminal failure latching blocks subsequent qualifications.
  - Added unit test asserting `Take-Snapshot` refuses snapshot capture when fixture is in terminal failure state.

## Hosted CI Status

In PR #302 hosted run `40239300282` (job `121307463123`), all 1,336 tests passed under .NET SDK 8.0 without any CI modifications. Following F1-F5 additions in run `40241960837`, 4 tests failed due to PowerShell StrictMode and encoding specifics, resolved in this delta pass:
1. `PositiveSetup_RealStores_LoadSeededInstallationAndSettings`: In .NET, `Encoding.UTF8` emits a 3-byte UTF-8 BOM (`0xEF, 0xBB, 0xBF`), which caused `System.Text.Json` deserialization in `ProfileStore.Load()` to throw `JsonException: '0xEF' is an invalid start of a value`. Fixed by standardizing on `New-Object System.Text.UTF8Encoding $false` (`$script:Utf8NoBom`) across all file writes and reads.
2. `CompareTargetBaseline_DetectsUnexpectedEmptyDirectory`: In PowerShell `Set-StrictMode -Version 3.0`, accessing `.Count` on a single `DirectoryInfo` object returned by `Get-ChildItem -Directory` threw `The property 'Count' cannot be found on this object.`. Fixed by wrapping results in `@(...)` and utilizing `Get-SafeCount`.
3. `CompareTargetBaseline_DetectsMutationDuringPendingCheckpoint`: On a freshly initialized baseline with 0 subdirectories, `Get-ChildItem -Directory` returned `$null`, causing `.Count` to throw under StrictMode. Fixed by wrapping in `@(...)` and using `Get-SafeCount`.
4. `VerifyCheckpoint_Applied_RefusesWhenOfficialReferenceIsMissing`: The `Applied` verification order checked the helper exit record before validating the official reference directory and DLLs. Refactored `Applied` verification to validate official reference directory and DLL existence first.

All verification continues strictly and exclusively on hosted GitHub Actions CI.


