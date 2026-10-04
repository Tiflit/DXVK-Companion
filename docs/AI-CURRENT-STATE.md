# Current AI Development State & Handoff Dashboard

> **Orientation Snapshot (Live Recheck Required)**: This document is an active orientation aid and point-in-time dashboard for the multi-agent workflow. It does not replace live GitHub state. Every role must recheck live PR metadata, branch heads, and GitHub Actions status before taking action.
>
> **Selective Reading Rule**: Routine sessions must NOT read the entire historical pilot log or journal archive. To conserve context and avoid drift, read `AGENTS.md`, this file (`docs/AI-CURRENT-STATE.md`), and the assigned GitHub Issue contract. Consult specific historical records in `docs/AI-PILOT-LOG.md` or `docs/ai-journal/` only when investigating a related finding or explicit dependency.

- **Last Updated**: 2026-10-03 America/Toronto (2026-10-04 UTC)
- **Active Base Branch**: `origin/main` at commit [`0dcc2bd88d113363b075169e867fccec8892f58c`](https://github.com/Tiflit/DXVK-Companion/commit/0dcc2bd88d113363b075169e867fccec8892f58c)
- **Active Task**: [Issue #20](https://github.com/Tiflit/DXVK-Companion/issues/20) (Post-#19 cleanup: privacy audit, concise documentation and fresh-session handoff)
- **Active Branch**: `docs/issue-20-workflow-cleanup`

---

## 1. Repository Branch & PR Inventory

| PR / Branch | Last checked head (recheck live) | Target Base | Status | Description / Notes |
|---|---|---|---|---|
| **PR #19** (`workflow/review-packet-provenance`) | `68aa480` | `main` (`e7b6e06`) | **MERGED** | Provenance hardening, centralized TRX attribution, scope grammar enforcement. Merged at `0dcc2bd`. |
| **PR #10** (`docs/pilot-2-current-checkpoint`) | `0be0f11` | `main` | **MERGED** | Preserved final Pilot #2 review, workflow handoff, and pilot outcomes. |
| **PR #8** (`automation/ai-workflow-foundation`) | `38b4d83` | `main` | **MERGED** | Initial GitHub-native workflow foundations. |
| **PR #4** (`pilot/companion-version-ordering`) | `944abc08c8722bdfc3b13bf7fda8fdd5a8a25b65` | `main` | **OPEN** | CompanionVersion numeric ordering (Issue #5). Rollout rechecks passed ([Hygiene 37173538904](https://github.com/Tiflit/DXVK-Companion/actions/runs/37173538904), [Scope 37173544858](https://github.com/Tiflit/DXVK-Companion/actions/runs/37173544858)). Awaiting fresh integration CI & human merge. |
| **PR #9** (`issue-6-prevent-dx12-vulkan-deployment`) | `c4d0f84` | `main` | **OPEN** | Prevent DXVK deployment for DX12/Vulkan (Issue #6). Rollout rechecks passed ([Hygiene 37173552820](https://github.com/Tiflit/DXVK-Companion/actions/runs/37173552820), [Scope 37173557225](https://github.com/Tiflit/DXVK-Companion/actions/runs/37173557225)). Awaiting fresh integration CI & human merge. |
| **PR #21** (`docs/issue-20-workflow-cleanup`) | `3ac97bea69f78e9a12290bbf7e10f7dcfdd9c0f4` | `main` (`0dcc2bd`) | **OPEN (Awaiting final CI and human merge)** | Issue #20 post-#19 cleanup, privacy audit, handoff dashboard. Revised packet inspected ([Packet Run 37174628859](https://github.com/Tiflit/DXVK-Companion/actions/runs/37174628859) on [Build/Test 37174562145](https://github.com/Tiflit/DXVK-Companion/actions/runs/37174562145); 67/67 tests, 8/8 returned patches). This row records the inspected revision; subsequent editorial head and CI are in live PR metadata. |

---

## 2. Active Work Queue & Ownership

| Work | Owner | Dependency / Decision | Next Action |
|---|---|---|---|
| **#20 cleanup and rollout** | ChatGPT verifies; human merges | Current `origin/main` | Check final PR #21 head/CI after coordinator editorial corrections, then human merge decision. |
| **#13 original-baseline safety** | Gemini investigation; ChatGPT verifies | Prove suspected defect on exact base; PR #9 dependency is not assumed | First product investigation after #20; construct synthetic reproducer before fix. |
| **#4 / #9 completion** | Gemini gathers CI; human decides | Fresh integration evidence against current `main` | Refresh integration CI without mixing unrelated changes; record tested pair and request human merge. |
| **#12 spec authority** | Human decides; Gemini prepares/implements | Phase-plan / legacy-import confirmation | Prepare brief decision summary; canonicalize spec references only after human confirmation. |
| **#14 shared-directory policy** | Human decides; ChatGPT clarifies; Gemini implements | #9 compatibility base, #12 spec location, policy decision | Prepare per-executable vs installation-wide options and test implications. |
| **#15 reassessment** | Gemini; ChatGPT verifies | #9 base and #12 normative spec location | Establish source freshness and queued-action evidence. |
| **#16 incompatible lifecycle** | Gemini; ChatGPT verifies | Terminal/parked/auto-resume decision; coordinate with #15 | Short options brief, then lifecycle tests and implementation. |
| **#17 / #18 coverage** | Gemini; targeted ChatGPT evidence check | #9 merged or explicitly approved stack; #18 spec docs depend on #12 | Run independent small sessions once prerequisite base exists. |

---

## 3. Model Role Allocation & Handoff Protocol

### Active Allocation
- **Gemini**: Primary implementer. Executes code edits, test suites, local verification, focused revisions, and bounded repairs in dedicated git worktrees.
- **ChatGPT**: Verification, architecture, reproduction analysis, and arbitration layer. Conducts independent checks and arbitrates reviewer findings.
- **Claude**: Audit and material-risk layer. Reserved for occasional independent audits and high-risk architectural/safety decisions to conserve quota.
- **Human**: Retains final merge authority, policy governance, and architectural specification decisions.

### Fresh-Session Operating Instructions

#### For Gemini (Implementer)
1. Read [AGENTS.md](../AGENTS.md) and this file (`docs/AI-CURRENT-STATE.md`).
2. Read the assigned GitHub Issue (`gh issue view <number>`). Note acceptance criteria and `### Allowed paths`.
3. Verify git status, fetch `origin/main`, and work in an isolated worktree.
4. Write failing regression fixtures first when addressing a defect.
5. Run task-relevant tests:
   - For application/test changes: `dotnet test tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj`.
   - For workflow automation changes: `python -m unittest discover -s tests/ai-workflow -v`.
6. Push your branch, open a PR with required headings (`Primary Issue`, `Summary`, `Scope`, `Verification`, `Documentation`), and verify that GitHub Actions CI checks complete successfully.
7. Record a factual session entry in a new file in `docs/ai-journal/` (e.g. `YYYY-MM-DD-<issue>-gemini-session-<n>.md`). Do not edit the shared journal index if it is outside your task's allowed paths.

#### For ChatGPT (Verifier / Arbitrator)
1. Inspect the PR and its generated review packet artifact (`review_packet.md` + `full-diff.diff`).
2. Verify tested checkout provenance, TRX test counts, and boundary compliance against the Issue contract.
3. If an unresolved defect is identified, formulate an exact reproducer and request a single bounded repair cycle.
4. Avoid expanding scope; distinguish blocking defects from non-blocking follow-up items.

#### For Claude (Auditor)
1. When explicitly engaged for a high-risk audit, review the full durable finding definitions in [docs/AI-PILOT-LOG.md](AI-PILOT-LOG.md) and the generated review packet.
2. Focus on safety invariants, bypass routes, untrusted input handling, and test efficacy.
3. State whether test counts and execution logs were directly verified or accepted from platform metadata.

---

## 4. Observational Measurement Rules

To evaluate workflow efficiency without burdening developers or purchasing telemetry tools:
- **Observable metrics**: In session journal entries, record observable counts:
  - Human decision/action interventions (prompting an action, choosing an option).
  - Human status queries (asking for progress or explanation—tracked separately from action interventions).
  - Repeated investigations caused by missing context or session drift.
  - Journal entry word count.
  - Whether a blocker required an additional session.
- **Unmeasured attributes**: Provider token/quota consumption and elapsed human clock time are recorded as `"not measured"` unless directly available from observable tooling.
- **Review cadence**: After three completed task handoffs, the coordinator reviews available journal entries to identify recurring friction points.

---

## 5. Unresolved Architectural & Governance Decisions

1. **Issue #12 (Normative Specification Authority)**: Precedence between `DXVK-COMPANION-SPEC-REVISED2.md` and phase documentation (such as `A1-UPDATED.md` and legacy import notes) requires human confirmation. The repository currently retains both roots without unilateral canonicalization.
2. **Issue #13 (Original-Baseline Safety Defect)**: Suspected pre-existing defect where `Reapply` might overwrite a native DLL baseline requires a synthetic reproducer on an exact base before proposing changes.
3. **Issue #14 (Shared-Directory Multi-Executable Policy)**: Architectural decision regarding whether DXVK installation should be scoped per-executable or across entire shared installation directories.
4. **Repository Protection Rulesets**: GitHub Actions workflows (`ai-scope-check`, `ai-pr-hygiene`, `build-and-test`) currently run as status checks. Enabling mandatory branch protection rulesets remains a human administrative choice.
