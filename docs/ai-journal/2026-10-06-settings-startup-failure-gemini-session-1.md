# Session Record: 2026-10-06 — Issue #59 / #61 Truthful Settings and Startup Failure Reporting

- **Date / Timestamp**: 2026-10-06 20:55:00 UTC
- **Agent Role & Model**: Gemini (Implementer)
- **Task / Issue**: Issue #61 (supersedes #59) — `[AI] Settings/startup failure repair — corrected contract, continuing #59`
- **Starting Head**: `49075cb1f2934a0ff9c1106d560cd02e747bb95d` (`origin/main`)
- **Initial PR Head**: `3796ec6289a6ae93757f226a1d983236a16d21b3` (PR #60)
- **Branch / Worktree**: `fix/issue-59-settings-startup-truthful-reporting` (`D:\dev\DXVK-Companion-issue-59`)

### Policy Source & Decision Governance

- **Decision Governance Block Status**: `DECIDED` ([Issue #61](https://github.com/Tiflit/DXVK-Companion/issues/61), superseding [#59](https://github.com/Tiflit/DXVK-Companion/issues/59)).
- **Human Approval Source**: Clearly attributed coordinator transcription of the human developer's 2026-10-06 16:25 America/Toronto response to the recommendation: **“I approve”**.
- **Scope of Approval**: Bounded repair behavior for settings persistence and startup-toggle failure reporting. Does not authorize local test execution or automatic merging.

### Summary of Changed Sections & Components

1. **`src/DXVKCompanion/Storage/SettingsStore.cs`**:
   - Added explicit success and error propagation via `public bool Save(out string? errorMessage)` alongside backward-compatible parameterless `Save()`.
   - Implemented a temporary-write and atomic replace strategy in the application directory: writes to a unique `.tmp.<guid>` file in the target directory and replaces the target via `File.Move(..., overwrite: true)`.
   - Preserves prior valid settings on ordinary caught write and replace failures: if an exception is thrown during temporary output or final commit replace, the prior valid target file and its original bytes remain untouched.
   - Ensures that if no prior settings file existed, a failed save leaves no target file.
   - Safely cleans operation-owned temporary files upon caught failure.
   - Added `[JsonIgnore]` test seams (`CustomSettingsPath`, `SimulatedTempWriteFailure`, `SimulatedCommitFailure`, `CustomTempWriter`, `CustomCommitReplace`) and a `Clone()` helper, ensuring serializer compatibility without leaking test seams into JSON.
   - Sanitized user-facing error strings to avoid exposing sensitive personal paths.

2. **`src/DXVKCompanion/Utils/StartupManager.cs`**:
   - Extracted `IStartupRegistry` and `IStartupRegistryKey` interfaces with a production `WindowsStartupRegistry` implementation.
   - Added explicit outcome propagation: `public bool EnableStartup(out string? errorMessage)` and `public bool DisableStartup(out string? errorMessage)`.
   - Implemented precise failure semantics:
     - Missing Run key on enable is a reported failure (`"Windows startup registry key was not found."`).
     - Missing Run key or missing value on disable is an idempotent success.
     - Registry access or security exceptions are reported as failures.
   - Kept quoted executable path format `$"\"{_exePath}\""` and parameterless overloads for backward compatibility.

3. **`src/DXVKCompanion/UI/SettingsChangeCoordinator.cs`**:
   - Created headless orchestration component managing state consistency between settings persistence and startup registration.
   - For management policy changes: persists the proposed policy via a clone before committing the shared in-memory property or UI selection. On save failure, retains the previously active policy so consumers (such as `TrayApp`) never observe an uncommitted policy.
   - For startup toggle: attempts the registry operation first.
     - On registry failure: retains previous preference/UI state, aborts file save, and explains that the change was not confirmed.
     - On registry success: updates in-memory `LaunchOnStartup` and attempts file persistence.
     - On registry success followed by save failure: retains the applied startup state in memory/UI, avoids pretending rollback, and reports a warning that Windows startup was updated but saving preference failed.
   - Tracked unresolved save outcome (`_unresolvedStartupSaveWarning`) so same-state/no-op requests retry persistence rather than falsely erasing warnings. Subsequent successful saves clear the warning.
   - Extracted production `UiReentrancyGuard` helper to suppress recursive event handler re-entry.

4. **`src/DXVKCompanion/UI/SettingsWindow.cs`**:
   - Delegated event handling and state logic to `SettingsChangeCoordinator`.
   - Added an in-window `_statusLabel` (red for errors, orange for warnings, cleared on success) providing truthful feedback without modal dialogs or tray balloons.
   - Uses `UiReentrancyGuard` to suppress recursive `CheckedChanged` events when reverting controls on failed operations.

5. **`tests/DXVKCompanion.PhaseA.Tests/SettingsAndStartupTests.cs`**:
   - Initial head contained 16 new Fact tests; Revised head contains 22 comprehensive unit and orchestration tests using `SyntheticTestDirectory` and `FakeStartupRegistry`.
   - Covered positive save/load roundtrips, partial temporary write failure with temp cleanup and byte preservation, commit replace failure, missing prior file behavior, startup manager enable/disable, missing Run key enable failure vs disable idempotent success, missing value disable idempotent success, registry access exceptions, registry SetValue and DeleteValue faults, policy change commit ordering, registry failure preventing file save, registry success with file save failure warning, same-state request retry and warning retention, already-persisted no-op positive control, unrelated policy save clearing warnings, shared trace proving registry operations precede save, and `UiReentrancyGuard` recursion suppression.

### Verification & Evidence

- **Scope & Code Inspection**: Verified all modifications adhere strictly to the 6 allowed paths. Zero application dependencies, `.csproj` files, or workflow files were touched.
- **Fail-Closed Privacy Scan**: Verified zero personal paths, tokens, or credentials exist in modified files.
- **Local Verification Attempt & Execution Status**:
  - Local build attempt: Executed `dotnet build DXVK-Companion.sln` on host. Result: Exited with code 1 (`No .NET SDKs were found; runtimes only installed at C:\Program Files\dotnet\shared`).
  - Local test suites: Honestly `NOT RUN (pending human authorization)` under the Local Test Execution Authorization Gate in `AGENTS.md`. No local test suites, integration tests, or application binaries were executed.
- **Cloud CI Readiness**: All 22 tests designed with injectable fakes and synthetic directories to execute deterministically in GitHub-hosted cloud CI (`Build and Test`). Cloud run on initial head passed 280 tests (264 prior + 16 new).

### Limitations

- Preservation of prior valid settings applies to ordinary caught write and replace failure boundaries; it does not claim multi-resource transactional atomicity or OS crash durability.
- Headless orchestration tests verify state transitions, call sequencing, and error propagation, but interactive WinForms GUI rendering on a physical desktop is qualified as source-inspected only.

### Out-of-Scope Findings

Out-of-scope findings: none.

---

## Revision 1: Addressing Coordinator Review Findings (2026-10-06)

- **Contract Continuity ([Issue #61](https://github.com/Tiflit/DXVK-Companion/issues/61))**:
  - Addressed coordinator contract formatting correction where prose was misplaced inside Issue #59's Allowed paths list. Issue #61 replaces that formatting, preserving the six allowed paths, approval, and criteria.
  - Updated PR #60's sole Primary Issue and closing reference to `Closes #61`, retaining contextual link to #59.
- **R1 — Production Operations in Failure & Ordering Regressions**:
  - `StartupToggle_CallOrdering_SharedTraceProvesRegistryPrecedesSave`: Replaced separate lists with a single shared trace list, asserting `OpenRunKey -> SetValue -> Save:TempWriter -> Save:CommitReplace -> DisposeKey` in exact time order.
  - Partial Temp Write & Commit Faults: Replaced pre-write throws with `CustomTempWriter` (writes actual partial content to temp file before throwing, asserting temp file cleanup and target byte preservation) and `CustomCommitReplace` (completes temp file write before throwing during commit replace).
  - Registry SetValue and DeleteValue Faults: Added orchestration tests exercising SetValue failure on enable and DeleteValue failure on disable, verifying no file save occurs and UI reverts.
  - Missing Value on Disable: Added positive idempotent success test when Run key exists but value is absent.
  - Production Reentrancy Guard: Replaced local test delegate with production `UiReentrancyGuard` in `SettingsChangeCoordinator.cs` used directly by `SettingsWindow.cs`. Qualified interactive GUI re-entry as source-inspected only.
- **R2 — Unresolved Save Tracking on Same-State Requests**:
  - In `SettingsChangeCoordinator`, tracked `_unresolvedStartupSaveWarning`.
  - Same-state `ChangeLaunchOnStartup` or `ChangePolicy` calls now retry persistence rather than falsely returning Success and clearing user-visible warnings.
  - Unrelated successful policy saves persist current settings and clear the unresolved warning.
  - Added already-persisted no-op positive control returning Success directly.
- **Evidence Corrections**:
  - Corrected test counts: 16 Fact tests on initial head; 22 Fact tests on revised head.
  - Recorded attempted local `dotnet build` outcome (no SDK on host).
  - Reaffirmed local tests `NOT RUN (pending human authorization)`.
- **Next Owner**: ChatGPT (Coordinator Verification).
