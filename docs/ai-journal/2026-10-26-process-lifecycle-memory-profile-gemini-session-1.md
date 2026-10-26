# Process-Lifecycle Companion Memory Ceiling Amendment and Retained Resource Evidence — Gemini, 2026-10-26

Task [Issue #304](https://github.com/Tiflit/DXVK-Companion/issues/304), remote main `a8debea05c1b51c7abde70b55f748c0981673cad`, tree `26a002e643bedeebac89a564b8ff402a898d8fe7`, policy `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9` (kernel v0.4).
Attributable owner approval recorded in coordinator relay [`ISSUE-304-GEMINI-RELAY.md`](file:///D:/dev/companion-coordination/process-lifecycle-v1-preparation-20261025/ISSUE-304-GEMINI-RELAY.md) and memory assessment [`ISSUE-303-MEMORY-ASSESSMENT-AND-AMENDMENT-20261026.md`](file:///D:/dev/companion-coordination/process-lifecycle-v1-preparation-20261025/ISSUE-303-MEMORY-ASSESSMENT-AND-AMENDMENT-20261026.md):
"Approve the 1 GiB Companion profile amendment and retained memory evidence changes; hosted verification only; no new runtime batch yet."

This session implements the approved 1 GiB Companion working-set ceiling amendment, per-process and per-role retained memory telemetry, truthful unavailable metric reporting, and immutable first resource-hold evidence in `ProcessLifecycleTrial.ps1`, 6 new hosted C# regressions in `ProcessLifecycleTrialPreparationTests.cs`, and candidate profile updates in `PACKAGED-PROCESS-LIFECYCLE-V1.md`, strictly confined to the four allowed paths.

---

## Acceptance and Verification Matrix

| Requirement | Shipped Function / Implementation | Verification Test(s) | Status |
|---|---|---|---|
| **1. Companion Working-Set Ceiling Amendment** | `$script:MAX_COMPANION_WORKING_SET_BYTES = 1073741824` (1 GiB, 1024 * 1024 * 1024 bytes) in `ProcessLifecycleTrial.ps1`. Evaluated with strictly greater-than (`-gt`) threshold semantics in `Evaluate-ProcessResourceThresholdsInternal`. | `ResourceObservation_CompanionWorkingSet_BelowAndEqualAndAboveOneGiB_RetainsGreaterThanSemantics` | Verified by hosted CI |
| **2. Retained Process Memory Summaries** | `Update-ProcessMemoryObservationInternal` and `Settle-ProcessMemoryObservationsInternal` maintain per-PID/per-role records in `$state.ProcessMemoryObservations`: `CurrentWorkingSetBytes`, `LastSampledWorkingSetBytes`, `MaxSampledWorkingSetBytes`, `OsReportedPeakWorkingSetBytes`, `OsPeakAvailable`, `TotalCpuMilliseconds`, `MaxCpuMilliseconds`, `SampleCount`, `FirstSampledAtUtc`, `LastSampledAtUtc`, `ConfiguredWorkingSetCapBytes`, `Unit`, `IsLive`. | `ResourceObservation_MaximaAndOsReportedPeakSemantics_PreservesUnavailableMetricsHonestly`, `ResourceObservation_PostExitRetention_PreservesSummariesWhenActiveProcessesEmpty` | Verified by hosted CI |
| **3. Truthful OS Metric Reporting** | Safe try/catch read of `PeakWorkingSet64` on live process handle. Sets `OsPeakAvailable = $false` and `OsReportedPeakWorkingSetBytes = $null` if metric is unavailable or throws; preserves prior confirmed peaks across subsequent samples without falsification. | `ResourceObservation_MaximaAndOsReportedPeakSemantics_PreservesUnavailableMetricsHonestly` | Verified by hosted CI |
| **4. Post-Exit Retention Across Empty Active Lists** | When active process list empties upon process stop/exit or supervisor settling, all historical observations persist in `$state.ProcessMemoryObservations` with `IsLive = $false` and `CurrentWorkingSetBytes = $null`, while retaining sample counts, timestamps, limits, last sampled bytes, and maxima. | `ResourceObservation_PostExitRetention_PreservesSummariesWhenActiveProcessesEmpty` | Verified by hosted CI |
| **5. Immutable First Resource-Hold Evidence** | `Register-ResourceHoldInternal` latches `$state.FirstResourceHold` on the first threshold crossing (`TimestampUtc`, `Metric`, `Role`, `Pid`, `ObservedValue`, `ConfiguredLimit`, `Unit`, `Reason`, `Checkpoint`). Guaranteed immutable: subsequent higher samples, separate process invocations, or exit never overwrite or mutate the initial evidence. | `ResourceObservation_FirstResourceHold_IsImmutableAcrossSubsequentSamplesAndExit` | Verified by hosted CI |
| **6. Distinct Process Invocations** | Separate launches of the same role (e.g. Companion start 1 with PID A, Companion start 2 with PID B) maintain separate records in `$state.ProcessMemoryObservations` without resetting trial monotonic clock, start attempts, or hold state. | `ResourceObservation_SeparateProcessInvocations_DistinctSummariesWithoutStateReset` | Verified by hosted CI |
| **7. Unchanged Guard Limits & Invariants** | Helper working set (`52,428,800 bytes`), Helper CPU (`600,000 ms`), Companion CPU (`1,800,000 ms`), 120-minute trial duration (`7,200 s`), 2 GiB root footprint (`2,147,483,648 bytes`), 4 helper starts, 8 companion starts, 25-minute watchdog, owner-first stopping on holds, and zero force-kill remain strictly intact. | `ResourceObservation_HelperWorkingSetAndCpuLimits_RemainUnchangedAndGuarded`, existing 27 preparation tests | Verified by hosted CI |

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
  - Size: `212,635 bytes`
  - LF SHA-256: `220bd7ff0ba930c24351b2b88f5fb30ce0f4d15e5f36ecb422e23d6aa884af87`
- **Updated Candidate Profile (`docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md`):**
  - LF SHA-256: `7bf0baf0e1f7c2bbfd2a2ab8a847f367df3a3fc8725becd4b61389ee4f51b424`
- **Frozen Artifact Pins (Unchanged):**
  - Helper Zip: `f75ec55738e6ca3c1a0cd0a342aefeaa0fb3addd03538f03f2ac7a43295750de` (Artifact `12609386490`)
  - Helper Exe: `f720cecae0f13eeef6d9b5e2d259b51df97b37bc3347b241071e25e8c1548d70`
  - Companion Zip: `32660fd3e3ed87385e47940d490db2e6ab93daad7e993fe4225dd157232b3b14` (Artifact `12532606945`)
  - Companion Exe: `25012c67ade281e0d4585282bf485e4783a1f7c486018f8d1a038c5f2dc18ca4`
  - DXVK v3.1.1: `40565b4a724aadc4433fa4e010b4b23916d9b1f1baeee64e17186db94f54e608`
  - DXVK v3.1: `30f9cc326874be344285582275446968cfa4c069db31ce56df312d6644179154`

---

## Operating Governance and Execution Boundaries

- **Zero Host Execution**: No local workstation compilation, tests, probes, app/helper starts, dot-sourcing, or artifact downloads occurred on this workstation.
- **Hosted Verification Authority**: Verification relies exclusively on hosted GitHub Actions CI across all existing required workflows.
- **Candidate Status**: The candidate profile remains candidate-only. Issue #303's first batch remains untouched and ended. A fresh runtime packet and adoption record will be prepared separately after review.
- **Authority**: Codex delta-reviews; human owner alone merges.
