# Process-lifecycle helper CPU build, smoke validation and hosted artifact — Gemini, 2026-10-25

Task [#299](https://github.com/Tiflit/DXVK-Companion/issues/299), base main `1cc3339e9eadaa443f6776928cce57953644e3d9`, tree `3bd58fca82de527e92c670652aa028569807cb02`, policy `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9`. Attributed coordinator transcription of direct owner decision recorded in Issue #299 comment: "Approved" (explicit approval for Issue #299 contract, four allowed paths, read-only hosted helper-build/smoke workflow, and Gemini implementation/testing followed by Codex review).

Implemented standalone native x64 CPU-only helper `QueueFixture.cpp`, read-only hosted build/smoke workflow `process-lifecycle-helper-cpu.yml`, validation specification `PACKAGED-PROCESS-LIFECYCLE-V1.md`, and this activity journal.

## Implementation Details

1. **Helper Source (`tools/validation/QueueFixture.cpp`)**:
   - Native x64 console executable (`static_assert(sizeof(void*) == 8)`).
   - Statically linked CRT (`/MT`), no external dependencies beyond `KERNEL32.dll`.
   - Exact supported invocation: `QueueFixture.exe --slot N` (single digit N in `1..4`).
   - Fixed layout validation: executable must reside at `<FIXTURE_ROOT>/target/QueueFixture.exe`. Rejects directory traversal (`..`), names other than `QueueFixture.exe`, or parent directories other than `target` (exit `3`).
   - Supervisory control: checks that supervisor created `<FIXTURE_ROOT>/control/` (exit `4` if missing).
   - Pre-existing stop marker refusal: refuses startup if `<FIXTURE_ROOT>/control/stop-N.request` already exists (exit `5`).
   - Signals readiness on stdout: writes `READY\n`.
   - Polling loop: checks assigned marker at 250ms intervals.
   - Normal stopping: regular-file marker produces `STOPPED\n` on stdout and clean exit `0`.
   - Error detection: directory marker causes exit `6`; unexpected access error causes exit `7`.
   - Finite execution cap: 25-minute watchdog produces `WATCHDOG\n` on stdout and exit `8`.
   - Invariant: helper never writes, creates, modifies, or deletes any files or directories on disk. No child process spawning, dynamic library loading, networking, or graphics API calls.

2. **Hosted Build & Smoke Workflow (`.github/workflows/process-lifecycle-helper-cpu.yml`)**:
   - `pull_request` trigger confined to the four allowed paths.
   - `permissions: contents: read`, 10-minute timeout on `windows-latest`.
   - Discovers preinstalled MSVC via `vswhere.exe`.
   - Compiles with `/std:c++17 /EHsc /W4 /WX /O1 /MT`.
   - Inspects headers, dependents, and imports via `dumpbin`:
     - Verifies `machine (x64)`.
     - Verifies only `KERNEL32.dll` dependent; rejects graphics DLLs (`d3d9`, `d3d10`, `d3d11`, `d3d12`, `dxgi`, `vulkan`).
     - Verifies zero networking, process-spawning, or dynamic-loading imports.
   - Executes 14 cloud-owned smoke test cases:
     - Argument validation (no args, wrong flag, missing slot, slot 0, slot 5, slot 99, extra args -> exit `2`).
     - Layout validation (wrong parent dir -> exit `3`, renamed exe -> exit `3`, missing control dir -> exit `4`).
     - Lifecycle validation (READY emitted, stays alive across observation interval, regular-file marker causes STOPPED and exit `0`).
     - Refuse relaunch (stop marker present -> exit `5`).
     - Slot independence (slot 2 starts and stops normally despite slot 1 marker present).
     - Directory marker handling (directory marker while running -> exit `6`).
     - Supervised strictly via owned process handles and marker files; zero `taskkill` or `Stop-Process`.
   - Produces machine-readable `smoke-results.json` with UTC and monotonic timings.
   - Produces `provenance.json` binding source hash, compiler, PR head/base, commit, tree, run ID, and file inventory.
   - Uploads artifact `process-lifecycle-helper-x64` via `actions/upload-artifact@v6`.

3. **Validation Specification (`docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md`)**:
   - Documents helper architecture, layout, lifecycle states, exit codes, and resource invariants.
   - Details hosted CI workflow and smoke testing.
   - Defines strict Stage B boundary: prerequisites for future host profile adoption, explicit disclosure of packaged Companion process discovery / inspection and lack of privacy filtering in Manual mode, and known-record queue semantics (queued Enable may be represented as pending Install on enabled targets).

## Verification & Execution Record

- **Mode**: Cloud-only hosted CI. Zero local builds, tests, or helper executions.
- **Base commit**: `1cc3339e9eadaa443f6776928cce57953644e3d9` (tree `3bd58fca82de527e92c670652aa028569807cb02`)
- **Policy identity**: `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9`
- **Branch**: `feat/issue-299-process-lifecycle-helper`
- **Initial Commit**: `7a55ad532153bd1443b33f255c95cbaa56c462bb` (tree `8635ad4b98ca8c25c48ab2358ccae1b39c6031bb`)
- **Initial Hosted CI Results**:
  - Scope Check Run `40214665886`: Passed in 11s.
  - PR Hygiene Run `40214665793`: Passed in 10s.
  - Workflow Tests Run `40214666113`: Passed in 18s (386 Python tests).
  - Build and Test Run `40214665848`: Passed in 1m45s (1,309/1,309 Phase A tests passed).
  - Helper CPU Checks Run `40214666035`: Failed during smoke tests with `PSInvalidOperationException: There is no Runspace available to run scripts in this thread` because `Process.add_OutputDataReceived` invokes PowerShell script blocks on ThreadPool threads without an active Runspace.
- **Repair**:
  - Replaced event handler `add_OutputDataReceived` with synchronous `StandardOutput.ReadToEnd()` for immediate exit test cases, and `.NET` `StandardOutput.ReadLineAsync()` tasks for interactive lifecycle cases, completely eliminating PowerShell thread pool Runspace affinity errors.
