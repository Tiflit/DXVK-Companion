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

- **Repaired Script (`tools/validation/ProcessLifecycleTrial.ps1`) SHA-256:** `76dec6fdeacdcf0cd94a1cc4b85be7abde3914939e5342aa4055c2a2535d4a53`
- **Helper Artifact:** ID `12609386490`, ZIP SHA256 `f75ec55738e6ca3c1a0cd0a342aefeaa0fb3addd03538f03f2ac7a43295750de`, EXE SHA256 `f720cecae0f13eeef6d9b5e2d259b51df97b37bc3347b241071e25e8c1548d70`
- **Companion Candidate:** ID `12532606945`, ZIP SHA256 `32660fd3e3ed87385e47940d490db2e6ab93daad7e993fe4225dd157232b3b14`, EXE SHA256 `25012c67ade281e0d4585282bf485e4783a1f7c486018f8d1a038c5f2dc18ca4`

## External SDK Setup Blocker and Proposed CI Repair

In both `main` postmerge run `40224152390` (attempts 1 and 2) and PR #302 run `40227401067`, `actions/setup-dotnet@v6` failed before restore/build/tests due to HTTP 409 responses from Microsoft's download endpoint for `dotnet-runtime-10.0.14-win-x64.zip` and `dotnet-sdk-8.0.427-win-x64.zip`.

Per relay instructions, this issue cannot be patched under Issue #301 (which strictly permits only four paths). For coordinator/owner review, the following minimal, separately scoped workflow repair is proposed:
- In `.github/workflows/build-and-test.yml`, configure `actions/setup-dotnet@v6` to use a pinned SDK version (e.g. `dotnet-version: '8.0.403'` or `8.0.x` with an explicit fallback/mirror or runner-installed SDK) rather than resolving to the transient 409 endpoint.

