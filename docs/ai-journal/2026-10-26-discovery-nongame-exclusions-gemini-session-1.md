# AI Activity Journal: Discovery Non-Game Exclusions Repair (Session 1)

- **Date:** 2026-10-26
- **Implementer:** Gemini (Argon)
- **Reviewer:** Codex
- **Merge Authority:** Human Owner Alone
- **Primary Issue:** [#309](https://github.com/Tiflit/DXVK-Companion/issues/309)
- **Relay Reference:** `D:/dev/companion-coordination/process-lifecycle-v1-preparation-20261025/ISSUE-309-GEMINI-RELAY.md`
- **Policy Identity:** `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9` (Kernel v0.4)
- **Base Commit:** `407aab9be4b3743000070088893b074ce22304c5` (remote `origin/main`)
- **Base Tree:** `8e76b669506a85c6dce97c160024a847b8f1cc4c`
- **Machine Dashboard:** [Issue #27](https://github.com/Tiflit/DXVK-Companion/issues/27)

---

## 1. Context and Problem Statement

During preparation and runtime trial execution on host environments, the process monitor encountered utility processes that render top-level windows and load DirectX 11 graphics modules (`d3d11.dll`), specifically:
1. `TextInputHost.exe` (Windows text input service host / touch keyboard component).
2. `Nahimic3.exe` (OEM audio utility component / OSD hook).

Under the baseline implementation of `GameDetector`, these utility processes were not in the exclusion set (`ExcludedProcessNames`). Consequently, when running on hosts where these components create windows and load Direct3D, `ProcessMonitor` qualified them as newly discovered games, creating profiles and adding them to the game library store.

### Requirements of Issue #309
Owner-approved Issue #309 specifies:
1. Exclude the exact process names `TextInputHost` and `Nahimic3` from new game discovery.
2. Preserve existing case-insensitive matching and optional `.exe` extension normalization.
3. Narrow scope: Do NOT introduce substring, vendor-wide, directory-wide, or wildcard exclusions. Similar-but-not-equal names (e.g., `TextInputHostGame`, `Nahimic3Game`, `TextInput`, `Nahimic`, `Nahimic2`) must remain eligible as positive controls.
4. Preserve known-record pending-action exit attachment: If a known installation in `GameLibraryStore` has an active pending action, the exit handler must attach upon process detection *before* name, window, or graphics qualification checks occur.
5. Invariants preserved: No changes to anti-cheat exclusions, monitor polling/scheduling, exit-handler instance identity, startup switches, UI, or frozen artifact pins.

---

## 2. Allowed Paths Contract

In accordance with Issue #309, edits are strictly confined to the following four paths:
1. `src/DXVKCompanion/Monitoring/GameDetector.cs`
2. `tests/DXVKCompanion.PhaseA.Tests/GameDetectorExclusionTests.cs`
3. `tests/DXVKCompanion.PhaseA.Tests/ProcessMonitorQualificationTests.cs`
4. `docs/ai-journal/2026-10-26-discovery-nongame-exclusions-gemini-session-1.md`

### Explicit Constraints Maintained:
- Zero edits outside these four allowed paths.
- No production per-fixture whitelist, classifier redesign, or deletion of stored library records.
- No CI/workflow, project/dependency, policy, startup switch, or frozen-artifact pin edits.
- Zero local test/build runs, script imports, PowerShell probes, app/helper launches, or artifact downloads.
- Existing hosted CI is the sole verification authority.

---

## 3. Acceptance and Verification Matrix

| Requirement | Shipped Function / Implementation | Verification Test(s) | Status |
|---|---|---|---|
| **1. Exact Exclusions for TextInputHost and Nahimic3** | Added `"textinputhost"` and `"nahimic3"` to `ExcludedProcessNames` HashSet in `GameDetector.cs`. Extracted `IsGameProcessName(string? processName)` pure predicate. | `GameDetectorExclusionTests.ExcludedProcessNames_ExactAndCaseInsensitive_AndWithExtension_AreExcluded` | Verified by hosted CI |
| **2. Case-Insensitive & Extension Normalization** | `IsGameProcessName` trims whitespace, strips optional trailing `.exe` (case-insensitive via `StringComparison.OrdinalIgnoreCase`), and matches against `ExcludedProcessNames` (`StringComparer.OrdinalIgnoreCase`). | `GameDetectorExclusionTests.ExcludedProcessNames_ExactAndCaseInsensitive_AndWithExtension_AreExcluded` (tested with mixed-case and all-caps variations with and without `.exe`) | Verified by hosted CI |
| **3. Positive Controls for Similar Names** | Exact string matching prevents accidental overbroad filtering. Names containing substrings (e.g. `TextInput`, `TextInputHostApp`, `Nahimic`, `Nahimic3Game`, `Nahimic2`, `NahimicVR`) remain eligible. | `GameDetectorExclusionTests.SimilarProcessNames_NotExactMatches_RemainEligibleAsPositiveControls`, `ProcessMonitorQualificationTests.ProcessMonitor_SimilarNames_AdmittedToNewDiscovery_AsPositiveControls` | Verified by hosted CI |
| **4. Existing System & Launcher Exclusions Intact** | All prior exclusions in `ExcludedProcessNames` (`system`, `svchost`, `explorer`, `steam`, `epicgameslauncher`, etc.) remain fully functional. | `GameDetectorExclusionTests.ExistingSystemAndLauncherExclusions_RemainExcluded` | Verified by hosted CI |
| **5. Null / Whitespace Handling** | `IsGameProcessName` safely returns `false` for `null`, empty string, or whitespace-only inputs without throwing. | `GameDetectorExclusionTests.NullOrWhitespace_ReturnsFalse` | Verified by hosted CI |
| **6. Integration Qualification Rejection** | In `ProcessMonitor`, processes named `TextInputHost` or `Nahimic3` having a window and DX11 modules are rejected by `detector.IsGameProcess`, firing 0 detection events, creating 0 profiles, and populating 0 library installations. | `ProcessMonitorQualificationTests.ProcessMonitor_ExcludedNames_TextInputHostAndNahimic3_NotAdmittedToNewDiscovery_EvenWithWindowAndApi` | Verified by hosted CI |
| **7. Pending Action Exit Attachment Invariant** | When an installation in `GameLibraryStore` has an active `PendingAction`, `ProcessMonitor.PollProcessesOnce()` calls `AttachExitHandlerOnce` before checking `isGame` or window presence. Excluded process names with pending actions have exit handlers attached, but are not admitted to new game discovery. | `ProcessMonitorQualificationTests.ProcessMonitor_ExcludedNames_KnownPendingAction_ExitWatcherAttachedBeforeNameExclusion` | Verified by hosted CI |
| **8. Process Instance Evaluation Seam** | Added `internal Func<Process, string>? ProcessNameResolver { get; set; }` to `GameDetector` so `IsGameProcess(Process)` and `ProcessMonitor` can evaluate production logic without requiring spawned OS processes or mock doubles. | `GameDetectorExclusionTests.IsGameProcess_ProcessInstance_EvaluatesThroughShippedPredicate` | Verified by hosted CI |

---

## 4. Architectural Design and Invariants

### 1. Pure Predicate Extraction in `GameDetector`
Previously, `IsGameProcessCore(Process process)` directly extracted `process.ProcessName.ToLowerInvariant()`. To facilitate direct testability and separation of concerns, the logic was refactored:
```csharp
internal static bool IsGameProcessName(string? processName)
{
    if (string.IsNullOrWhiteSpace(processName))
        return false;

    string name = processName.Trim();
    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        name = Path.GetFileNameWithoutExtension(name);

    return !ExcludedProcessNames.Contains(name);
}
```
`IsGameProcessCore` delegates to `IsGameProcessName(ProcessNameResolver?.Invoke(process) ?? process.ProcessName)`.

### 2. ProcessMonitor Pipeline Order
In `ProcessMonitor.cs` (lines 308-338):
1. The monitor resolves `exePath` for the process.
2. It queries `_libraryStore.FindInstallationForExecutable(exePath)`.
3. If an installation exists and has a non-null `PendingAction`, it calls `AttachExitHandlerOnce(proc, startTime, exePath)` immediately.
4. Only *after* this pending exit-attachment step does the monitor evaluate `_detector.IsGameProcess(proc)` and window presence.
5. If `IsGameProcess` returns `false` (e.g. for `TextInputHost` or `Nahimic3`), the process is rejected from new qualification, while any pre-existing pending action remains safely guarded by the exit watcher.

This ordering guarantees that lifecycle management and restoration invariants are never bypassed by discovery filter changes.

---

## 5. Execution Ledger and Non-Execution Policy Assertion

In strict accordance with the kernel rules and Issue #309's mandate:
- **Zero local builds or test executions:** Neither `dotnet build`, `dotnet test`, nor any script execution was invoked on the local machine.
- **Zero process probes:** No processes, helpers, or synthetic fixtures were launched locally.
- **Hosted CI Authority:** All compilation, test execution, regression analysis, and scope verification are deferred entirely to the hosted GitHub Actions CI runners.

### Recorded Read-Only Git / Metadata Commands:
- `git status`: Verified clean working copy and untracked file state.
- `git diff`: Inspected code and test changes against `origin/main`.
- `Get-FileHash`: Computed cryptographic SHA-256 hashes of modified and created files.
- `Get-Item`: Verified byte lengths and LF formatting.

---

## 6. Cryptographic and File Identifiers

| File Path | Byte Length | Line Ending | SHA-256 Hash |
|---|---|---|---|
| `src/DXVKCompanion/Monitoring/GameDetector.cs` | 6,308 bytes | LF | `74D999410A01C26CE2D195869B0F244A41C8CE5E7E7E9402640FC248FCCD6108` |
| `tests/DXVKCompanion.PhaseA.Tests/GameDetectorExclusionTests.cs` | 4,539 bytes | LF | `FCA57393E2EDE451E41C6134D7A5C26740482135CC502D9B5C23204F09518E38` |
| `tests/DXVKCompanion.PhaseA.Tests/ProcessMonitorQualificationTests.cs` | 98,963 bytes | LF | `5DC97890CCEB7330133F0ECA2A3F5F2DD465C98402A3F39158DC7F40F1D301C7` |
| `docs/ai-journal/2026-10-26-discovery-nongame-exclusions-gemini-session-1.md` | (computed at commit) | LF | (computed at commit) |
