# Session Record: 2026-10-04 — Issue #13 Implementation (Session 2 Bounded Revision)

- **Date / Timestamp**: 2026-10-04 00:20 America/Toronto (2026-10-04 04:20 UTC)
- **Agent Role & Model**: Gemini (Implementer / Antigravity)
- **Task / Issue**: [PR #23](https://github.com/Tiflit/DXVK-Companion/pull/23) bounded revision for [Issue #13](https://github.com/Tiflit/DXVK-Companion/issues/13) — [AI] F8: Reapply must back up pre-existing files for newly required DLLs
- **Revision Starting Head**: [`1f3f0f0d875940ae2f28e279d1c95ca980432298`](https://github.com/Tiflit/DXVK-Companion/commit/1f3f0f0d875940ae2f28e279d1c95ca980432298)
- **Failing Collision Reproducer Head**: [`91f022a01460360a0f82df3bc4a73752e245a499`](https://github.com/Tiflit/DXVK-Companion/commit/91f022a01460360a0f82df3bc4a73752e245a499)
- **Branch / Worktree**: `fix/issue-13-reapply-original-baseline` (`D:\dev\DXVK-Companion-issue-13`)

### Purpose & Context
Execute bounded revision requested by ChatGPT coordinator in PR #23:
1. Address Finding V1: repair new-baseline backup collision where `ReapplyAsync` could reuse pre-existing backup B without validation, resulting in Restore overwriting native file A with unrelated bytes B.
2. Address Finding V2: correct durable citation of initial reproducer SHA to `976a364a6973c4f6142c4d90da1c36ba5fa09fc9` and qualify status in `docs/AI-PILOT-LOG.md`.

### Decisions & Rationale
- Added 4 test fixtures to `PhaseDReapplyOriginalBaselineTests.cs` before repair: 3 reproducing collision failure on newly required DLL, config, and Unknown record state; 1 control verifying established baselines during repeated normal Reapply.
- Verified in CI (run 37176271677) that all 3 collision tests failed as expected while 72 tests passed.
- Implemented `ResolveSafeNewBackupRelativePath` in `DxvkInstaller.cs`: if default backup path `<installationId>/<fileName>` already exists with different bytes, select collision-safe path `<installationId>/<nameWithoutExt>.baseline-<hashPrefix8><ext>`. If safe path also has mismatched content, refuse Reapply safely.
- Added backup integrity checks refusing Reapply if an established backup was tampered or corrupted.
- In `ReapplyAsync` transaction commit, preserved established `OriginalSha256` for `FileOriginalState.Existing` files, updating it only for newly established or previously Unknown records.
- Corrected reproducer SHA in session 1 journal and phrased F8 in `docs/AI-PILOT-LOG.md` as reproduced with proposed fix in open PR #23.

### Evidence & Measurements
- Collision reproducer CI run: 37176271677 failed 3 tests, confirming defect V1.
- Workflow tests: 68/68 passed locally.
- Human interventions: 0; Status queries: 0; Repeated investigations: 0; Blocked: No.
- Scope: changes strictly within Issue #13 allowed paths.
