# Process-Lifecycle Companion Memory Ceiling Amendment and Retained Resource Evidence — Gemini, 2026-10-26

Primary task contract: [Issue #306](https://github.com/Tiflit/DXVK-Companion/issues/306) (formatting supersession of [Issue #304](https://github.com/Tiflit/DXVK-Companion/issues/304)), remote main `a8debea05c1b51c7abde70b55f748c0981673cad`, tree `26a002e643bedeebac89a564b8ff402a898d8fe7`, policy `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9` (kernel v0.4).
Attributable owner approval recorded in coordinator relay [`ISSUE-304-GEMINI-RELAY.md`](file:///D:/dev/companion-coordination/process-lifecycle-v1-preparation-20261025/ISSUE-304-GEMINI-RELAY.md) and memory assessment [`ISSUE-303-MEMORY-ASSESSMENT-AND-AMENDMENT-20261026.md`](file:///D:/dev/companion-coordination/process-lifecycle-v1-preparation-20261025/ISSUE-303-MEMORY-ASSESSMENT-AND-AMENDMENT-20261026.md):
"Approve the 1 GiB Companion profile amendment and retained memory evidence changes; hosted verification only; no new runtime batch yet."

This session implements the approved 1 GiB Companion working-set ceiling amendment, per-process and per-role retained memory telemetry, truthful unavailable metric reporting, immutable first resource-hold evidence, and invocation identity separation across PID reuse in `ProcessLifecycleTrial.ps1`, 7 hosted C# regressions in `ProcessLifecycleTrialPreparationTests.cs`, and candidate profile updates in `PACKAGED-PROCESS-LIFECYCLE-V1.md`, strictly confined to the four allowed paths.

---

## Acceptance and Verification Matrix

| Requirement | Shipped Function / Implementation | Verification Test(s) | Status |
|---|---|---|---|
| **1. Companion Working-Set Ceiling Amendment** | `$script:MAX_COMPANION_WORKING_SET_BYTES = 1073741824` (1 GiB, 1024 * 1024 * 1024 bytes) in `ProcessLifecycleTrial.ps1`. Evaluated with strictly greater-than (`-gt`) threshold semantics in `Evaluate-ProcessResourceThresholdsInternal`. | `ResourceObservation_CompanionWorkingSet_BelowAndEqualAndAboveOneGiB_RetainsGreaterThanSemantics` | Verified by hosted CI |
| **2. Retained Process Memory Summaries** | `Update-ProcessMemoryObservationInternal` and `Settle-ProcessMemoryObservationsInternal` maintain per-PID/per-role records in `$state.ProcessMemoryObservations`: `CurrentWorkingSetBytes`, `LastSampledWorkingSetBytes`, `MaxSampledWorkingSetBytes`, `OsReportedPeakWorkingSetBytes`, `OsPeakAvailable`, `TotalCpuMilliseconds`, `MaxCpuMilliseconds`, `SampleCount`, `FirstSampledAtUtc`, `LastSampledAtUtc`, `ConfiguredWorkingSetCapBytes`, `Unit`, `IsLive`. | `ResourceObservation_MaximaAndOsReportedPeakSemantics_PreservesUnavailableMetricsHonestly`, `ResourceObservation_PostExitRetention_PreservesSummariesWhenActiveProcessesEmpty` | Verified by hosted CI |
| **3. Truthful OS Metric Reporting** | Safe try/catch read of `PeakWorkingSet64` on live process handle. Sets `OsPeakAvailable = $false` and `OsReportedPeakWorkingSetBytes = $null` if metric is unavailable or throws; preserves prior confirmed peaks across subsequent samples without falsification. | `ResourceObservation_MaximaAndOsReportedPeakSemantics_PreservesUnavailableMetricsHonestly` | Verified by hosted CI |
| **4. Post-Exit Retention Across Empty Active Lists** | When active process list empties upon process stop/exit or supervisor settling, all historical observations persist in `$state.ProcessMemoryObservations` with `IsLive = $false` and `CurrentWorkingSetBytes = $null`, while retaining sample counts, timestamps, limits, last sampled bytes, and maxima. | `ResourceObservation_PostExitRetention_PreservesSummariesWhenActiveProcessesEmpty` | Verified by hosted CI |
| **5. Immutable First Resource-Hold Evidence** | `Register-ResourceHoldInternal` latches `$state.FirstResourceHold` on the first threshold crossing (`TimestampUtc`, `Metric`, `Role`, `Pid`, `InvocationId`, `ObservedValue`, `ConfiguredLimit`, `Unit`, `Reason`, `Checkpoint`). Guaranteed immutable: subsequent higher samples, separate process invocations, or exit never overwrite or mutate the initial evidence. | `ResourceObservation_FirstResourceHold_IsImmutableAcrossSubsequentSamplesAndExit` | Verified by hosted CI |
| **6. Distinct Process Invocations** | Separate launches of the same role (e.g. Companion start 1 with PID A, Companion start 2 with PID B) maintain separate records in `$state.ProcessMemoryObservations` without resetting trial monotonic clock, start attempts, or hold state. | `ResourceObservation_SeparateProcessInvocations_DistinctSummariesWithoutStateReset` | Verified by hosted CI |
| **7. PID Reuse Across Invocations** | `Update-ProcessMemoryObservationInternal` checks `$isLiveBool -eq $true` and matching `InvocationId`; settled records (`IsLive = false`) are never matched or revived when Windows reuses a PID. `InvocationId` bound to start attempt across Helper, Companion, and supervisor stop records. | `ResourceObservation_ReusedPidAcrossInvocations_MaintainsIndependentSummariesWithoutRevival` | Verified by hosted CI |
| **8. Unchanged Guard Limits & Invariants** | Helper working set (`52,428,800 bytes`), Helper CPU (`600,000 ms`), Companion CPU (`1,800,000 ms`), 120-minute trial duration (`7,200 s`), 2 GiB root footprint (`2,147,483,648 bytes`), 4 helper starts, 8 companion starts, 25-minute watchdog, owner-first stopping on holds, and zero force-kill remain strictly intact. | `ResourceObservation_HelperWorkingSetAndCpuLimits_RemainUnchangedAndGuarded`, existing 27 preparation tests | Verified by hosted CI |

---

## Architectural and Telemetry Details

### 1. Memory Cap Amendment Context
- In Issue #303's first acquisition batch, Companion exited normally but the supervisor sampled a working set of **806,191,104 bytes** against the initial **524,288,000 bytes** (500 MiB) ceiling.
- Source inspection revealed multiple coexisting representations during DXVK acquisition: compressed gzip payload, uncompressed TAR bytes, per-entry memory allocations, and `MemoryStream.ToArray()` buffer copies.
- The 1 GiB ceiling (`1,073,741,824 bytes`) is an approved bounded validation allowance above the observed sample to accommodate unstreamed managed buffers and GC return latency.
- It is **not** a diagnosed leak fix, an assertion of overall application efficiency, or a guarantee that 1 GiB is safe for all host environments. The fixed application binary, helper binary, and official DXVK artifacts remain strictly pinned.

### 2. Retained Process Memory Observations Schema
Each record in `$state.ProcessMemoryObservations` defines:
```json
{
  "Role": "Companion",
  "Slot": null,
  "Pid": 5001,
  "ConfiguredWorkingSetCapBytes": 1073741824,
  "Unit": "bytes",
  "SampleCount": 2,
  "FirstSampledAtUtc": "2026-10-26T17:30:00.0000000Z",
  "LastSampledAtUtc": "2026-10-26T17:30:05.0000000Z",
  "LastSampledWorkingSetBytes": 1073741824,
  "CurrentWorkingSetBytes": 1073741824,
  "MaxSampledWorkingSetBytes": 1073741824,
  "OsReportedPeakWorkingSetBytes": 1073741824,
  "OsPeakAvailable": true,
  "TotalCpuMilliseconds": 1250,
  "MaxCpuMilliseconds": 1250,
  "IsLive": true
}
```
When a process exits normally or abnormally, `IsLive` transitions to `false` and `CurrentWorkingSetBytes` transitions to `null`, but all historical metrics remain durably stored in `state.json`.

### 3. Immutable First Resource-Hold Schema
When a resource limit is first exceeded, `$state.FirstResourceHold` latches:
```json
{
  "TimestampUtc": "2026-10-26T17:30:10.0000000Z",
  "Metric": "WorkingSet",
  "Role": "Companion",
  "Pid": 5001,
  "ObservedValue": 1073741825,
  "ConfiguredLimit": 1073741824,
  "Unit": "bytes",
  "Reason": "Companion working set (1073741825 bytes) exceeds limit (1073741824 bytes).",
  "Checkpoint": "CompanionMemoryExhausted"
}
```
Because `Register-ResourceHoldInternal` checks `if ($null -eq $State.FirstResourceHold)`, subsequent samples (even with higher memory or subsequent CPU limit violations) and process exits never mutate or overwrite this record.

---

## Cryptographic and Script Identifiers

- **Amended Script (`tools/validation/ProcessLifecycleTrial.ps1`):**
  - Size: `221,331 bytes`
  - LF SHA-256: `40745c1d8fd759c7d1ff4bcabdf5c719eb01d4e100de5c1b2b2cf196427d5c13`
- **Updated Candidate Profile (`docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md`):**
  - Size: `27,492 bytes`
  - LF SHA-256: `9e143bea7f7593da93ba035771c4a228af3be27fb94a2f534397e26288647be7`
- **Frozen Artifact Pins (Unchanged):**
  - Helper Zip: `f75ec55738e6ca3c1a0cd0a342aefeaa0fb3addd03538f03f2ac7a43295750de` (Artifact `12609386490`)
  - Helper Exe: `f720cecae0f13eeef6d9b5e2d259b51df97b37bc3347b241071e25e8c1548d70`
  - Companion Zip: `32660fd3e3ed87385e47940d490db2e6ab93daad7e993fe4225dd157232b3b14` (Artifact `12532606945`)
  - Companion Exe: `25012c67ade281e0d4585282bf485e4783a1f7c486018f8d1a038c5f2dc18ca4`
  - DXVK v3.1.1: `40565b4a724aadc4433fa4e010b4b23916d9b1f1baeee64e17186db94f54e608`
  - DXVK v3.1: `30f9cc326874be344285582275446968cfa4c069db31ce56df312d6644179154`

---

## CI Failure, Root Cause, and Repair History

### 1. Build and Test Initial Hosted Run (Run 40371077229, Commit `b5a51a379fc8c40e58f4300fde4a3ec101b2f958`)
- **Outcome**: 1,379 passed, 8 failed (Total: 1,387 tests).
- **Failing Tests**:
  - `ObservationCycle_ExceedingDurationBudget_LatchesTerminalHold`
  - `MonotonicClock_MissingOrInvalidOrigin_LatchesTerminalHoldWithoutReset`
  - All 6 new `ResourceObservation_*` regressions.
- **Root Cause**:
  In PowerShell, `$PID` is a reserved, read-only automatic variable holding the process ID of the running PowerShell host. In `tools/validation/ProcessLifecycleTrial.ps1`, the new helper functions `Register-ResourceHoldInternal`, `Update-ProcessMemoryObservationInternal`, and `Evaluate-ProcessResourceThresholdsInternal` declared a parameter named `$Pid`. When PowerShell bound parameters on function invocation, it attempted to assign to the read-only automatic variable `$PID`, throwing the runtime exception:
  `"Cannot overwrite variable Pid because it is read-only or constant."`
  This exception broke `Test-ResourceObservation` and any code path calling `Register-ResourceHoldInternal`.
- **Repair**:
  Renamed the function parameter from `$Pid` to `$TargetPid` in all three functions and updated all internal callers to use `-TargetPid`. Kept the emitted persistent object property name `Pid` unchanged, ensuring 100% schema compliance in `state.json`.

### 2. PR Metadata Hygiene Initial Run (Run 40371077353)
- **Outcome**: Failed with `Documentation disposition: use documentation paths with specific impacts, or None — a specific reason.`
- **Root Cause**: PowerShell string interpolation when creating the PR body escaped backticks into backslashes (`\docs/...md\`), which failed the strict regex in `check_pr_hygiene.py`.
- **Repair**: Updated the PR body using `scripts/ai-workflow/update_pr_body.py` with properly formatted UTF-8 markdown and backticks.

### 3. Scope Check Initial Run (Run 40371077346)
- **Outcome**: Failed with `Invalid allowed path 'No other repository edits...': paths must not contain whitespace or prose.`
- **Root Cause**: In Issue #304's body, the prose line `No other repository edits. No production refactor...` was placed immediately under `### Allowed paths` before the next level-2 heading `## Verification and acceptance`. `parse_contract.py` matched everything between `### Allowed paths` and the next `###` heading, treating the prose line as a path.
- **Remediation**: The coordinator created Issue #306 inserting `### Exclusions` before the prose line; PR #305 Primary Issue was updated to #306 via `update_pr_body.py`.

### 4. Build and Test Phase A Regressions (Run 40373144613)
- **Outcome**: 1,380 passed, 7 failed (`MonotonicClock_MissingOrInvalidOrigin_LatchesTerminalHoldWithoutReset` and the 6 `ResourceObservation_*` tests).
- **Root Cause Analysis**:
  1. `MonotonicClock_MissingOrInvalidOrigin_LatchesTerminalHoldWithoutReset`: `Register-ResourceHoldInternal` had `[Parameter(Mandatory = $true)] $ObservedValue` and `$ConfiguredLimit`. When `Invoke-SuperviseObservationCycle` encountered an invalid monotonic origin, it invoked `Register-ResourceHoldInternal` with `-ObservedValue $null -ConfiguredLimit $null`, causing PowerShell non-interactive parameter binding to throw `Cannot bind argument to parameter 'ObservedValue' because it is null.`
  2. `ResourceObservation_*`: The `Test-ResourceObservation` dispatch action evaluated `-Slot (if ($Role -eq "Helper") { $Slot } else { $null })` directly as a parameter argument and in hashtable construction. In Windows PowerShell 5.1 (`powershell.exe`), nested `if` statements inside expression parentheses `(...)` are not supported. Additionally, `Update-ProcessMemoryObservationInternal` had typed `[DateTime]$SampledAtUtc = ([DateTime]::UtcNow)`, which could fail default value binding.
  3. `Supervise-HelperStop` / `Supervise-CompanionStop`: When processes stopped, `$state.ActiveProcesses` retained the stopped process entry until the next supervisor sample, rather than filtering stopped processes prior to `Settle-ProcessMemoryObservationsInternal`.
  4. Test diagnostics: `Assert.Equal(0, res.ExitCode)` masked PowerShell stdout and stderr output on failure.
- **Repair**:
  1. Made `$ObservedValue` and `$ConfiguredLimit` non-mandatory (`Mandatory = $false`) with `$null` defaults in `Register-ResourceHoldInternal`.
  2. Extracted `$effectiveSlot = if ($Role -eq "Helper") { $Slot } else { $null }` and relaxed `$SampledAtUtc` to `$null` with internal fallback to `[DateTime]::UtcNow`.
  3. Filtered stopped processes out of `$state.ActiveProcesses` in `Supervise-HelperStop` and `Supervise-CompanionStop` before settling observations.
  4. Updated test assertions across all 6 `ResourceObservation_*` tests and `MonotonicClock_MissingOrInvalidOrigin_LatchesTerminalHoldWithoutReset` to assert `Assert.True(res.ExitCode == 0, $"Action failed with exit code {res.ExitCode}.\nSTDOUT: {res.StandardOutput}\nSTDERR: {res.StandardError}")`.
  All 1,387 tests passed in hosted Run 40377601930.

### 5. Codex Focused Review at Commit `e8a64e5` (M1–M3 Repair)
- **Outcome & Findings**:
  - **M1 (PID Reuse & Invocation Identity)**: `Update-ProcessMemoryObservationInternal` matched solely by `Role` and `TargetPid`, which revived settled records (`IsLive = false`) if Windows reused a PID across launches, combining sample counts and maxima. Repaired by requiring `$isLiveBool -eq $true` and matching `InvocationId`. Added `ResourceObservation_ReusedPidAcrossInvocations_MaintainsIndependentSummariesWithoutRevival` exercising the same PID (9301) across settlement and restart.
  - **M2 (Reconcile Zero-Probe Claim)**: The earlier journal and handoff claimed blanket "zero workstation probes", which was inaccurate given local diagnostic Python import and PowerShell syntax probes executed during failure analysis. Retracted the inaccurate claim and recorded the complete factual ledger below.
  - **M3 (Primary Contract Supersession)**: Updated PR #305 Primary Issue to `#306` via `update_pr_body.py`, referencing `#304` for historical attribution, restoring a clean machine-parseable contract for `AI Scope Check`.

---

## Workstation Local Execution and Probe Ledger

### 1. Permitted Read-Only Git, Metadata, Search, and Guarded Maintenance
- `git status`, `git rev-parse HEAD`, `git diff`, `git log`: checked working tree state, base revisions, and patch deltas.
- `gh pr view`, `gh run list`, `gh run view`, `gh issue view`: inspected hosted CI run logs, PR metadata, and Issue contracts.
- `python -c "import hashlib..."`: calculated LF SHA-256 hashes and byte lengths for file pins.
- `Select-String`: searched code patterns across repository files.
- `python scripts/ai-workflow/update_pr_body.py`: applied guarded PR description updates with pre-write backups and verification.

### 2. Workstation Exploratory Behavior Probes (Retracted & Ceased)
During the diagnosis of earlier hosted CI failures, several local syntax and runtime exploratory probes were run in the terminal:
- **Python Contract Parser Probe**: Executed `python -c "from scripts.ai_workflow.parse_contract import ..."` to test regex parsing of Issue #304 description text during Scope Check triage.
- **PowerShell StrictMode & Syntax Probes**: Executed isolated `powershell.exe -Command` snippets to inspect:
  - `Set-StrictMode -Version 3.0` property access behavior on PSCustomObject vs null (`PropertyNotFoundException`).
  - `ConvertFrom-Json` empty collection deserialization into array vs single object.
  - Parameter binding behavior with `[DateTime]` and `$null` default arguments.
  - Adding note properties via `PSObject.Properties.Add` in strict mode.

**Retraction Notice**: The blanket assertion that "zero workstation probes occurred" in the previous session journal and PR description was inaccurate and is hereby retracted. All workstation syntax probes have ceased immediately; all verification is conducted strictly through hosted GitHub Actions CI.

### 3. Execution Invariant Boundaries Maintained
- **Zero Local Compilation**: No local `dotnet build`, `csc`, or MSBuild compilation occurred.
- **Zero Local Test Runs**: No local `dotnet test`, xUnit runner, or harness test execution occurred.
- **Zero Script Execution of Trial Action**: Neither `ProcessLifecycleTrial.ps1 -Action Setup` nor `Supervise` nor `Test-ResourceObservation` was run on this workstation.
- **Zero Child Process Launches**: Neither `DXVK-Companion.exe` nor `QueueFixture.exe` was launched.
- **Zero Artifact Downloads**: No workflow artifacts or external release binaries were downloaded locally.
- **Hosted Authority**: Hosted GitHub Actions CI remains the sole verification authority for all changes.

---

## Operating Governance and Execution Boundaries

- **Hosted Verification Authority**: Verification relies exclusively on hosted GitHub Actions CI across all existing required workflows.
- **Candidate Status**: The candidate profile remains candidate-only. Issue #303's first batch remains untouched and ended. A fresh runtime packet and adoption record will be prepared separately after review.
- **Authority**: Codex delta-reviews; human owner alone merges.
