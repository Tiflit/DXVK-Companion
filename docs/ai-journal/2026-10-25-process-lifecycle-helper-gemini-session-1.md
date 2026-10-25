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
   - Pre-existing stop marker refusal: refuses startup if `<FIXTURE_ROOT>/control/stop-N.request` already exists (exit `5`). Only `ERROR_FILE_NOT_FOUND` allows startup; missing control path returns 4 before readiness.
   - Signals readiness on stdout: writes `READY\n`.
   - Polling loop: checks assigned marker at 250ms intervals.
   - Normal stopping: regular-file marker produces `STOPPED\n` on stdout and clean exit `0`.
   - Error detection: directory marker causes exit `6`; unexpected access error or missing control directory (`ERROR_PATH_NOT_FOUND`) causes exit `7`.
   - Finite execution cap: 25-minute watchdog produces `WATCHDOG\n` on stdout and exit `8`.
   - Invariant: helper never writes, creates, modifies, or deletes any files or directories on disk. No child process spawning, dynamic library loading, networking, or graphics API calls.

2. **Hosted Build & Smoke Workflow (`.github/workflows/process-lifecycle-helper-cpu.yml`)**:
   - `pull_request` trigger confined to the four allowed paths.
   - `permissions: contents: read`, 10-minute timeout on `windows-latest`.
   - Discovers preinstalled MSVC via `vswhere.exe`.
   - Compiles with `/std:c++17 /EHsc /W4 /WX /O1 /MT`.
   - Inspects headers, dependents, and imports via `dumpbin`:
     - Verifies `machine (x64)`.
     - Fail-closed dependent parser asserts only `KERNEL32.dll` dependent; rejects unknown DLLs and graphics DLLs (`d3d9`, `d3d10`, `d3d11`, `d3d12`, `dxgi`, `vulkan`).
     - Import symbol parser normalizes hint/ordinal rows, accounting for toolchain CRT symbols and asserting zero networking, process-spawning, or dynamic-loading imports.
     - Parser negative controls verify rejection of hint-prefixed forbidden APIs and unexpected DLLs against data fixtures.
   - Executes 17 cloud-owned smoke test cases:
     - 7 argument validation cases (no args, wrong flag, missing slot, slot 0, slot 5, slot 99, extra args -> exit `2`).
     - 3 layout validation cases (wrong parent dir -> exit `3`, renamed exe -> exit `3`, missing control dir -> exit `4`).
     - 4 lifecycle validation cases (READY emitted, stays alive, regular-file marker -> exit `0`; pre-existing marker refusal -> exit `5`; slot independence -> exit `0`; directory marker -> exit `6`).
     - 1 disappearing control directory regression case (renaming control dir after READY -> exit `7` promptly, not watchdog).
     - 2 harness negative controls (failure after READY triggers normal stop in finally; bounded timeout detection without hanging).
   - Supervised strictly via owned process handles and marker files; zero `taskkill` or `Stop-Process`.
   - Retains `helper-smoke-failure-diagnostics` on failure; uploads `process-lifecycle-helper-x64` on success.
   - Produces machine-readable `smoke-results.json` with UTC and monotonic timings.
   - Safely reads PR head/base from GitHub event payload, records MSVC compiler version, measures total artifact size against 100MiB cap, and produces `provenance.json`.

3. **Validation Specification (`docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md`)**:
   - Documents helper architecture, layout, lifecycle states, exit codes, and resource invariants.
   - Details hosted CI workflow, fail-closed inspection, and smoke testing.
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
- **Attempt 2 Commit**: `0f90f387b931c0da038cc9d49ac1f0879fa11eab` (tree `378d2cfdadd211bf046b3d4ddb8f5432c76d6cf0`)
  - Scope Check Run `40214956445`: Passed in 9s.
  - PR Hygiene Run `40214956442`: Passed in 9s.
  - Workflow Tests Run `40214956320`: Passed in 16s (386 Python tests).
  - Helper CPU Checks Run `40214956485`: All 14 smoke tests passed cleanly; failed in subsequent `Generate provenance and inventory report` step with PowerShell `ParserError` on `"$provenancePath:"` where the trailing colon was parsed as a variable scope delimiter.
- **Attempt 3 Commit**: `84ae986971e4ba073a261a71b397165268150fe4` (tree `f7cb047f9cc11d37214571032f476d84acf70e75`)
  - Scope Check Run `40215087767`: Passed in 8s.
  - PR Hygiene Run `40215087931`: Passed in 5s.
  - Workflow Tests Run `40215087753`: Passed in 12s (386 Python tests).
  - Build and Test Run `40215087762`: Passed in 2m13s (1,309/1,309 Phase A tests passed).
  - Helper CPU Checks Run `40215087742`: Passed in 25s. All 14 smoke test cases passed. Uploaded artifact `process-lifecycle-helper-x64` (ID `12606839091`).
- **Codex Grouped Review on PR #300 (Comment 6400920107)**:
  - Identified four blocking in-scope requirements (R1-R4):
    - R1: Harden smoke execution lifecycle with bounded stdout/stderr draining, try/finally normal stopping on failure/timeout, retention of failure diagnostics on failure, and two harness negative controls.
    - R2: Fail-closed import/dependency gate asserting strictly `KERNEL32.dll`, symbol row hint normalization, toolchain CRT symbol accounting, and four parser negative controls against data fixtures.
    - R3: Correct artifact provenance (read PR head/base from GitHub event payload safely), record compiler/toolset identity, assert measured size against 100MiB cap, and correct approval link.
    - R4: Fail immediately on disappearing control directory (exit 7) instead of polling until watchdog, with cloud-owned regression case.
- **Repair Cycle (R1-R4)**:
  - `tools/validation/QueueFixture.cpp`: Only `ERROR_FILE_NOT_FOUND` is accepted as absent marker before READY; any other error (e.g. `ERROR_PATH_NOT_FOUND`) returns 4. In polling loop, `ERROR_PATH_NOT_FOUND` returns exit `7` immediately.
  - `.github/workflows/process-lifecycle-helper-cpu.yml`: Implemented fail-closed dependency parser (`KERNEL32.dll` only), symbol hint normalization, and 4 parser negative controls. Implemented `Invoke-FixtureCase` with bounded async stdout/stderr draining, try/finally normal stop, failure diagnostics retention (`helper-smoke-failure-diagnostics`), and added `DisappearingControlDir_Slot4` plus 2 harness negative controls (total 17 smoke tests). Safely read PR head/base from event payload, recorded compiler version, and verified artifact size against 100MiB bound.
  - `docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md`: Corrected recorded owner approval link to `https://github.com/Tiflit/DXVK-Companion/issues/299#issuecomment-6400475314` and updated specification to describe hardened gate, lifecycle, and R4 exit behavior.
- **Repair Cycle Attempts and Resolutions**:
  - **Attempt 4 (`7777713`)**: Failed at 0s due to unindented lines in multi-line here-strings inside workflow YAML `run: |` blocks.
  - **Attempt 5 (`073dce9`)**: Replaced here-strings with indented string arrays. Import check failed closed on `GetModuleHandleExW` and `LoadLibraryExW` linked by MSVC `/MT` static CRT (`libcmt.lib`).
  - **Attempt 6 (`d699f0b`)**: Explicitly accounted for all 73 legitimate helper + `/MT` static CRT symbols, blocking unaccounted symbols, direct `LoadLibrary[AW]`, and process spawning. Import check and 4 parser negative controls passed. In smoke testing, `ValidLifecycle_Slot1` timed out because `[System.Threading.Tasks.Task]::WaitAny(@($task), 4000)` returned integer `0` (task index 0), which PowerShell evaluated as `!0 == $true`. In addition, reading `ExitCode` after `$proc.Dispose()` recorded `-1` in `smoke-results.json`.
  - **Attempt 7**: Replaced all 7 `Task.WaitAny` single-task calls with `$task.Wait(4000)` (which returns a native `[bool]`), and captured `$recordedExitCode` before disposing `$proc`.
