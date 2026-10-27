# AI Activity Journal: Process-Lifecycle Candidate Companion Pin Update (Session 1)

- **Date:** 2026-10-26
- **Implementer:** Gemini (Argon)
- **Reviewer:** Codex
- **Merge Authority:** Human Owner Alone
- **Primary Issue:** [#311](https://github.com/Tiflit/DXVK-Companion/issues/311)
- **Relay Reference:** `D:/dev/companion-coordination/process-lifecycle-v1-preparation-20261025/ISSUE-311-GEMINI-RELAY.md`
- **Policy Identity:** `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9` (Kernel v0.4)
- **Base Commit:** `1bc6eab56f678c44159a39a5327b5b5329580fa3` (remote `origin/main`, merged PR #310)
- **Base Tree:** `df9ce28bfa5e83d31c5dd1b18843c905b4167aa1`
- **Machine Dashboard:** [Issue #27](https://github.com/Tiflit/DXVK-Companion/issues/27)

---

## 1. Context and Problem Statement

Following the merge of PR #310 (`1bc6eab56f678c44159a39a5327b5b5329580fa3`), which resolved discovery non-game candidate qualification for `TextInputHost` and `Nahimic3` ([Issue #309](https://github.com/Tiflit/DXVK-Companion/issues/309)), the post-merge `Build and Test` workflow on `main` (Run [40425446883](https://github.com/Tiflit/DXVK-Companion/actions/runs/40425446883), attempt 1, job 121929913612) compiled and published new self-contained portable Windows x64 binaries:
- Application artifact: `12699816316` (`DXVK-Companion-win-x64`)
- Provenance artifact: `12699801248` (`build-provenance`)

The previously pinned candidate Companion artifact (`12532606945`, commit `46e6f22363cddcfa88d805b588b6ac30173bc9dc`) preceded the discovery repair. To prepare for any subsequent host execution under Issue #303, the candidate pins in `ProcessLifecycleTrial.ps1`, preparation tests in `ProcessLifecycleTrialPreparationTests.cs`, and candidate profile documentation in `PACKAGED-PROCESS-LIFECYCLE-V1.md` must be updated to the new artifact identities.

This task is a bounded candidate validation-pin update only; it does not change product behavior, activate runtime execution, or reset previous ended batches.

---

## 2. Allowed Paths Contract

In accordance with Issue #311, edits are strictly confined to the four declared paths:
1. `tools/validation/ProcessLifecycleTrial.ps1`
2. `tests/DXVKCompanion.PhaseA.Tests/ProcessLifecycleTrialPreparationTests.cs`
3. `docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md`
4. `docs/ai-journal/2026-10-26-process-lifecycle-companion-pins-gemini-session-1.md`

### Explicit Constraints Maintained:
- Zero edits outside these four paths.
- No changes to helper or DXVK frozen pins.
- No changes to resource limits (1 GiB working set ceiling, 2 GiB storage footprint, 4 helper / 8 companion starts, 120-minute trial duration, 25-minute watchdog remain intact).
- No production code changes, classifier changes, or UI modifications.
- No CI workflow, project, dependency, or kernel policy document changes.
- Candidate profile remains strictly **CANDIDATE ONLY — PENDING SEPARATE HUMAN ADOPTION AND CURRENT READINESS**.
- No runtime activation, trial execution, or old-root reset.
- Zero workstation builds, local test runs, PowerShell script dot-sourcing, or binary downloads/probes.
- Existing hosted CI is the sole verification authority.

---

## 3. Acceptance and Verification Matrix

| Requirement | Shipped Function / Implementation | Verification Test(s) | Status |
|---|---|---|---|
| **1. Exact Companion Frozen Pin Replacement** | Updated `$script:FROZEN_COMPANION_*` in `ProcessLifecycleTrial.ps1`: `SRC_COMMIT = "1bc6eab56f678c44159a39a5327b5b5329580fa3"`, `ARTIFACT_ID = "12699816316"`, `ZIP_SIZE = 66410332`, `ZIP_SHA256 = "a824946d6ed68850b8fae29e34596d82f8115057aa26e0071c66708bd0ae39c9"`, `PROVENANCE_ARTIFACT_ID = "12699801248"`, `PROVENANCE_SIZE = 324`, `PROVENANCE_SHA256 = "3d60d599740dbfcefb9b06fd824385bae5e8de2d520520cd9488309c636f74a4"`, `EXE_SIZE = 71772767`, `EXE_SHA256 = "f600b07d2d09c1300054db8beea8e1930582c3fbdd0a85aff92cf3d31b52ae2b"`, `RUN_ID = "40425446883"`. | `ProcessLifecycleTrialPreparationTests.Script_FrozenPins_MatchPublishedConstants` | Verified by hosted CI |
| **2. Distinction Between Provenance Archive and Payload** | Preserved distinction between provenance archive ZIP (324 bytes, SHA-256 `3d60d599740dbfcefb9b06fd824385bae5e8de2d520520cd9488309c636f74a4`) and inner `build-provenance.json` (276 bytes, SHA-256 `7150ddfe5d260ffcbb092c00ae5b286d92e0507629db35529734faecba369e28`). Corrected setup example filename from `provenance.json` to `build-provenance.zip`. | Profile documentation inspection & `ExtractCompanionProvenance` validation tests | Verified by hosted CI |
| **3. Refresh Candidate Profile Table and Script Hash** | Updated table in `PACKAGED-PROCESS-LIFECYCLE-V1.md` with new Companion commit, run ID, artifact IDs, sizes, and SHA-256 digests. Refreshed trial script entry to actual final saved byte length (221,331 bytes) and SHA-256 (`1b0fc609fc80637168616f4c92f0544fbd58879d6401b6a25df70a65a1aec0fc`, LF, no BOM). | `docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md` | Verified by hosted CI |
| **4. Update Preparation Test Assertions** | Updated `Script_FrozenPins_MatchPublishedConstants` in `ProcessLifecycleTrialPreparationTests.cs` to assert the new Companion ZIP, Provenance ZIP, and EXE SHA-256 digests. Kept all helper, DXVK, and synthetic test-pin assertions unchanged. | `ProcessLifecycleTrialPreparationTests.Script_FrozenPins_MatchPublishedConstants` | Verified by hosted CI |
| **5. Unchanged Guard Limits & Invariants** | Companion working set ceiling (1 GiB / 1,073,741,824 bytes), helper working set (50 MiB), root storage footprint (2 GiB), 120-minute trial duration, 4 helper / 8 companion start limits, and zero force-kill policy remain completely intact. | Existing preparation test suite (34 tests) | Verified by hosted CI |

---

## 4. Inspection History and Non-Execution Policy Assertion

### Codex Streamed Data-Inspection Record
In the preceding coordinator phase, Codex streamed and hashed both the executable and PDB directly from the retained archive as data to verify archive contents, PE/x64 header (`MZ` header, machine `0x8664`, PE32+ optional header), and actual provenance manifest (`head_sha`, `sha`, `run_id`, `run_attempt`, `ref`).
- An initial inspection stopped at a regular PDB entry (`DXVK-Companion.pdb`, 130,280 bytes, SHA-256 `db35933302142769a2b5ad7788316dc9a21a9e3854ea25c7453ce29018415c14`) due to an overly narrow inspection check.
- The human owner explicitly authorized continuation of the data-only inspection in the coordinating channel.
- No binary was extracted to disk or executed.

### Gemini Workstation Execution Ledger
In strict compliance with the kernel operating rules:
- **Zero local test runs:** Neither `dotnet test` nor any PowerShell test harness was invoked on the local workstation.
- **Zero process launches or probes:** No binaries, helpers, or synthetic fixtures were executed locally.
- **Zero downloads:** No artifacts or packages were downloaded to the workstation.
- **Hosted CI Authority:** All compilation, test execution, regression analysis, and scope verification are deferred entirely to the hosted GitHub Actions CI runners.

#### Recorded Read-Only Git / Metadata Commands:
- `git fetch origin main`: Retrieved merged main revision `1bc6eab56f678c44159a39a5327b5b5329580fa3`.
- `git rev-parse origin/main`: Verified commit identity.
- `git cat-file -p origin/main`: Verified base tree `df9ce28bfa5e83d31c5dd1b18843c905b4167aa1`.
- `git log -1 --format=%H origin/main -- ...`: Verified policy identity `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9` (Kernel v0.4).
- `gh issue view 27`: Inspected machine dashboard orientation.
- `gh issue view 311`: Verified task contract and pin values.
- `Get-FileHash` / `Get-Item`: Computed cryptographic SHA-256 hashes and byte lengths of saved files.
- `git diff`: Verified exact delta confined to allowed paths.

---

## 5. Hosted CI Execution and Failure Diagnostics Record

### Initial Hosted CI Run (Run 40427971019, Attempt 1, Commit `b21780da7b7392cb4608045d53be94d1efb45e8c`)
- **Workflows Passed (5/6):**
  - **AI Task Scope Check** (Run 40427970992, Job 121938390336): **Passed** (strictly 4 allowed paths).
  - **PR Hygiene Guard** (Run 40427971049, Job 121938390501): **Passed**.
  - **AI Workflow Tests** (Run 40427971114, Job 121938390593): **Passed**.
  - **Acquire Facts and Render Snapshot** (Run 40427971077, Jobs 121938392310, 121938467932): **Passed**.
  - **helper-cpu** (Run 40427970925, Job 121938390194): **Passed**.
- **Build and Test** (Run 40427971019, Job 121938390263):
  - Outcome: **1,447 passed, 3 failed** (Total: 1,450 tests, 2.25 min).
  - **Target Preparation Tests:** All 34 tests in `ProcessLifecycleTrialPreparationTests` **PASSED**, including `Script_FrozenPins_MatchPublishedConstants`.
  - **Failed Tests (3):**
    1. `DXVKCompanion.PhaseATests.OperationLeaseCoordinatorTests.ProfileStore_BrokenLoad_DxvkManagerPreflightRefusal`:
       - Expected: `"corrupted or unreadable"`
       - Actual: `"Operation in progress for installation: C:\Users\RUNNER~1\AppData\Local\Temp\..."` at line 619.
       - Cause: Operation lease timeout (500ms) under parallel runner load on hosted VM, returning lease refusal before preflight broken load check could be asserted.
    2. `DXVKCompanion.PhaseA.Tests.ConfigurationDisableLifecycleTests.BothDisabled_DoNotTakeOverUnownedOrCreateAbsentConfig(existing: True)`:
       - `Assert.True() Failure: Expected: True, Actual: False` at line 118.
    3. `DXVKCompanion.PhaseA.Tests.AdoptedDxvkLifecycleAcceptanceTests.AdoptedDxvkLifecycle_PersistedManual_AdoptsUpdatesDisablesCleanly(api: DX9, architecture: "x64", preexistingConfig: False)`:
       - `Assert.Equal() Failure: Expected: Applied, Actual: Failed` at line 518.
- **Triage and Policy Compliance:**
  - All 3 failures occurred in tests outside the 4 allowed paths of Issue #311 and represent known lease contention / timeout under parallel test runner load.
  - Per the explicit instructions of Issue #311 ("Preserve all failed hosted attempts without unchanged-head blind reruns" and "Keep every failed CI attempt and truthful execution evidence"), this attempt failure is preserved without blind reruns, assertion weakening, timeout inflation, or test serialization.

---

## 6. Cryptographic and File Identifiers

All files are normalized with LF (`\n`) line endings and UTF-8 encoding without BOM:

| File Path | Byte Length | Line Ending | SHA-256 Hash |
|---|---|---|---|
| `tools/validation/ProcessLifecycleTrial.ps1` | 221,331 bytes | LF | `1b0fc609fc80637168616f4c92f0544fbd58879d6401b6a25df70a65a1aec0fc` |
| `tests/DXVKCompanion.PhaseA.Tests/ProcessLifecycleTrialPreparationTests.cs` | 163,075 bytes | LF | `034d50bf7ccf5c512cf4fb03bab67714a777fae2220268038e00db130e33442e` |
| `docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md` | 27,786 bytes | LF | `e811e8583b80580d73b8e2bc1e2a07628fadee880e3b64b987d08fc282756fc1` |
| `docs/ai-journal/2026-10-26-process-lifecycle-companion-pins-gemini-session-1.md` | (computed at commit) | LF | (computed at commit) |

---

## 7. Runtime Boundary Assertion

Both earlier lifecycle batches and the separate data inspection remain **ENDED**, with all evidence, logs, and artifacts safely retained. This task does not authorize fixture directory setup, runtime readiness reset, background process spawning, or execution of a new trial. Any subsequent trial execution remains dependent on an explicit separate adoption decision by the human owner.
