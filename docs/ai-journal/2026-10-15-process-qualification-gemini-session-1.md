# Gemini Session Journal: Process Qualification Gate & Bounded Backoff

- **Date**: 2026-10-15 (UTC) / 2026-10-14 (Local)
- **Implementer**: Gemini (Advanced Agentic Coding)
- **Authority**: Human decision under Issue #116 (comment 6183550508); task contract assigned in [Issue #117](https://github.com/Tiflit/DXVK-Companion/issues/117); grouped review repairs under PR #118.
- **Contract / Task**: [Issue #117](https://github.com/Tiflit/DXVK-Companion/issues/117) — `[Feature] Gate Process Qualification to Observed Graphics APIs and Add Bounded Backoff`.
- **Base Revision**: `11375695a0747300ead20d5b12abb063345894b1` (`origin/main`).
- **Branch**: `feature/process-qualification-gate` (PR #118).

---

### 1. Observable Metrics

- **Human Decision Interventions**: 0 (routine implementation under approved contract #117).
- **Human Status Queries**: 0.
- **Repeated Investigations / Session Drift**: 0.
- **Blocker Requiring Additional Session**: No.
- **Repair Iterations**: 1 grouped review repair batch addressing compilation and lifecycle findings (R1-R7).
- **Provider Token / Human Wall-Clock Time**: Not measured.

---

### 2. Implementation & Repair Summary

1. **R1 — Repository API Compliance & Compilation Repairs**:
   - Resolved compiler failure in test suites by importing `DXVKCompanion.Utils` for `PeParser`.
   - Repaired API invocations to actual repository signatures: `AntiCheatAssessment.None()`, `ProfileStore.GetAll()`, `GameLibraryStore.GetAll()`, and `Save(inst)`.
   - Used synthetic file paths for `ProfileStore` (`games.json`) rather than directory paths.
   - Fixed missing closing brace in `src/DXVKCompanion/Monitoring/ProcessMonitor.cs`.

2. **R2 — Process Ownership and Real Exit Notification**:
   - Established explicit process instance ownership: processes attached to `ProcessExitHandler` are retained in `_attachedExitProcesses` and owned/disposed exclusively by `ProcessExitHandler` upon real process termination.
   - Unattached process instances created during production polling enumeration are disposed promptly via `DisposeIfOwned`, while externally injected enumerator processes (e.g. in test suites) retain external lifecycle ownership without premature disposal.
   - Enforced single-attachment per process instance, preventing duplicate attachments between pending-action checks and qualification events.
   - Verified exit notification end-to-end via `RaiseProcessExited` in unit tests.

3. **R3 — Non-Overlapping Coordination and Disposal Boundary**:
   - Guarded polling and assessment passes with `_pollLock` via `Monitor.TryEnter` to ensure zero overlapping evaluations from concurrent timer callbacks.
   - Implemented definitive disposal boundary (`_disposed` check and dictionary clearing under lock), ensuring no new state or events can be emitted after monitor disposal.

4. **R4 — Coherent Tray Admission Path**:
   - Replaced fragile `_lastHandledPid` suppression with authoritative qualification handling: `TrayApp` consumes `OnQualifiedGameDetected` exclusively.
   - Removed duplicate subscription to `OnGameDetected` and unqualified fallback reclassification routes.
   - Extracted `TrayApp.ProcessGameAdmissionAsync` allowing comprehensive, headless verification of game profile creation, library recording, and deployment compatibility without GUI dependencies.

5. **R5 — Comprehensive Lifecycle and Known-Record Tracking**:
   - Made transient caching instance-aware using start-time identity across `_nonGamePids`, `_windowRetries`, and `_candidates`, properly resetting state when PIDs are reused by the operating system.
   - Retired missing processes across all collections upon disappearance from enumeration.
   - Attached exit tracking for known installations with queued pending actions regardless of window presence or initial graphics qualification.

6. **R6 — Provenance Tracking Across All Results**:
   - Updated `ApiClassifier` to preserve `EvidenceSource: "RuntimeIncomplete"` and `Medium` confidence when partial module sets are found during incomplete enumerations.
   - Carried incomplete runtime enumeration notices into static PE import fallback results.
   - Guaranteed that null snapshots and thrown assessment exceptions consume candidate backoff budgets via `AdvanceAfterAssessment` and log bounded diagnostic summaries distinguishing unresolved inspection from completed no-evidence observations.

7. **R7 — Internal Boundary Protection and Seam Preservation**:
   - Reverted `ModuleScanResult`, `ProcessQualification`, and `ScanLoadedGraphicsModules` to `internal` accessibility, strictly adhering to the architectural boundary.
   - Preserved non-virtual public methods on `GameDetector` and `ProcessExitHandler`, providing `internal virtual` core seams for test fakes.
   - Guarded against recursive override loops in `ModuleScanner` when derived classes override `GetLoadedGraphicsModules`.
   - Repaired test compilation references (`DXVKCompanion.Safety` namespace import and unused variable cleanup in `ProcessMonitorQualificationTests`).

8. **R8 — Exit Watcher Ownership, Reentrancy Boundaries, Identity Lifecycle & Headless Fakes (A-D)**:
   - **Section A**: Removed premature poller disposal of missing processes in `ProcessMonitor`, allowing `ProcessExitHandler` to deliver queued exit notifications cleanly without racing against poller cleanup. Added internal `ProcessInstanceExited` event with PID-instance binding and exactly-once delivery. On `Dispose()`, monitor unhooks exit events, cleans state, and disposes attached processes only if monitor-owned (not externally injected).
   - **Section B**: Enforced `_isPolling` reentrancy guard under `_pollLock` to exclude same-thread reentry from injected scanners or callbacks. Added explicit `_disposed` checks immediately following `CreateSnapshot` and preceding each individual event callback (`OnSnapshotDetected`, `OnQualifiedGameDetected`, `OnGameDetected`), ensuring in-flight disposal cleanly aborts qualification and publication.
   - **Section C**: Handled transition between unresolved (`DateTime.MinValue`) and known process start times: in-progress candidates adopt known timestamps without restarting budget; qualified or expired candidates reset upon PID reuse or timestamp change, preventing inheritance of terminal states. Ensured exited and disappeared processes clean up `_windowRetries`.
   - **Section D**: Introduced `FakeHttpMessageHandler` for `HttpClient` in `DxvkGithubClient` and `DxvkInstaller` to completely eliminate live network requests during headless testing. Added test seams `autoEnableDispatcher` and `autoReapplyDispatcher` to `TrayApp.ProcessGameAdmissionAsync` to verify single-dispatch on DX11 and zero-dispatch on Unknown/DX12/Vulkan without live installer execution. Provided synthetic `RawModuleEnumerator` seam in `ModuleScanner` to avoid live host module enumeration.
   - Added 12 new deterministic tests and modernized headless admission orchestration test covering all requested scenarios in `tests/DXVKCompanion.PhaseA.Tests/ProcessMonitorQualificationTests.cs`.

9. **R9 — Immutable Registration Token Binding & Unresolved Identity Isolation (Comment 6196151368)**:
   - Bound an immutable `Guid registrationToken` to each exit watcher attachment in `ProcessExitHandler` with token-based deduplication (`_notifiedTokens`), eliminating cross-instance suppression when PIDs are reused.
   - Updated `ProcessMonitor.HandleExit` to verify matching registration token against `_attachedExitProcesses[pid]`, ensuring stale delayed exit deliveries from an older process instance cannot evict a newer instance's attachment or candidate state.
   - Guarded candidate qualification and admission against unresolved start times (`startTime != DateTime.MinValue`), withholding qualification until identity is established while advancing failure/backoff budgets.
   - Prevented silent inheritance of terminal candidate states (`IsQualified` / `IsExpired`) when start times are unresolved or when PIDs are reused with unresolved identity.
   - Added deterministic unit tests for stale exit delivery survival on reused PID, terminal candidate reset with unresolved identity, and admission withholding until resolution.

---

### 3. Inspected vs. Executed Evidence

- **Inspected Evidence**:
  - `src/DXVKCompanion/Monitoring/ModuleScanner.cs`, `ApiClassifier.cs`, `ProcessMonitor.cs`, `GameDetector.cs`, `ProcessExitHandler.cs`.
  - `src/DXVKCompanion/UI/TrayApp.cs`, `src/DXVKCompanion/Program.cs`.
  - `src/DXVKCompanion/DXVK/DxvkCompatibility.cs`.
  - `tests/DXVKCompanion.PhaseA.Tests/PhaseBDetectionTests.cs`, `PhaseEUIIntegrationTests.cs`, `SyntheticTestDirectory.cs`.
  - `docs/spec/DXVK-COMPANION-SPEC.md`.
- **Executed Evidence**:
  - GitHub Actions Cloud CI on Windows runners (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`).
  - Local product builds, tests, and application launches were strictly **NOT RUN** on the host per contract instructions.

---

### 4. Technical Limitations & Bounded Guarantees

- **Bounded Observation Window**: Backoff assessment is restricted to 12 evaluations over 120 seconds. Late-loading graphics modules initialized after 120 seconds or via dynamic mid-game level transitions remain undetected without continuous runtime module hooking (explicitly out of scope).
- **Desktop Application Qualification**: Windowed desktop applications loading Direct3D or Vulkan runtimes qualify at this boundary; detection does not claim heuristic game content detection.
