# Current AI Development State & Handoff Orientation

> **Two-Layer Handoff System**:
> 1. **Live Automated Dashboard (Machine-Published)**: Real-time facts (current main SHA, open PRs, CI identities, tested checkouts) are automatically maintained in the dedicated GitHub Issue: `[AI Dashboard] Current Repository State & Handoff Orientation` (find via `gh issue list --search "[AI Dashboard]"`).
> 2. **Curated Repository Orientation (This Document)**: Curated work queue, role allocation, architectural decisions, and handoff protocols. It is updated during authorized documentation tasks or via `scripts/ai-workflow/update_dashboard.py`.
>
> **Selective Reading Rule**: Routine agent sessions must NOT read the entire historical pilot log or journal archive. To conserve context and avoid drift, follow the 4-step startup route in [AGENTS.md](../AGENTS.md).

- **Last Updated**: 2026-10-04 UTC
- **Active Base Branch**: `origin/main` at commit [`ce74e1e1caba1ee5197788c945826648d4f5a752`](https://github.com/Tiflit/DXVK-Companion/commit/ce74e1e1caba1ee5197788c945826648d4f5a752)
- **Active Task**: [Issue #25](https://github.com/Tiflit/DXVK-Companion/issues/25) (Automate compact GitHub handoffs and refresh current-state orientation)
- **Active Branch**: `workflow/issue-25-compact-handoffs`

---

## 1. Merged Pilot PR Inventory & Milestones

All foundational pilot features, bugfixes, and CI harnesses are merged into `origin/main`:

| PR / Branch | Merged Head | Target Base | Status | Milestone Summary |
|---|---|---|---|---|
| **PR #9** (`issue-6-prevent-dx12-vulkan-deployment`) | `60bd512` | `main` (`230c8ae`) | **MERGED** | Prevent DXVK deployment for DX12/Vulkan (Issue #6). Merged at `ce74e1e`. |
| **PR #4** (`pilot/companion-version-ordering`) | `a24934d` | `main` (`4f44059`) | **MERGED** | CompanionVersion numeric ordering (Issue #5). Merged at `230c8ae`. |
| **PR #24** (`audit/issue-22-privacy-coverage`) | `b9c1559` | `main` (`51691ee`) | **MERGED** | Historical privacy audit, rule redaction, and durable evidence annex (Issue #22). Merged at `4f44059`. |
| **PR #23** (`fix/issue-13-reapply-original-baseline`) | `f8331d3` | `main` (`0dcc2bd`) | **MERGED** | Reapply original-baseline capture and backup preservation defect repair + 8 regressions (Issue #13). Merged at `51691ee`. |
| **PR #21** (`docs/issue-20-workflow-cleanup`) | `8d9c2d6` | `main` (`0dcc2bd`) | **MERGED** | Post-#19 cleanup, connector journaling, and dashboard orientation (Issue #20). Merged at `f7a5cb9`. |
| **PR #19** (`workflow/review-packet-provenance`) | `68aa480` | `main` (`e7b6e06`) | **MERGED** | Review packet provenance hardening, TRX parsing, blocking scope/hygiene gates (Issue #11). Merged at `0dcc2bd`. |
| **PR #10** (`docs/pilot-2-current-checkpoint`) | `0be0f11` | `main` | **MERGED** | Preserved final Pilot #2 review, workflow handoff, and pilot outcomes. |
| **PR #8** (`automation/ai-workflow-foundation`) | `38b4d83` | `main` | **MERGED** | Initial GitHub-native workflow foundations. |

---

## 2. Active Work Queue & Ownership

| Work | Owner | Dependency / Decision | Next Action |
|---|---|---|---|
| **#25 handoff automation** | Gemini implements; ChatGPT verifies; human merges | Current `origin/main` (`ce74e1e`) | Open PR with dashboard generator script, workflow, tests, and documentation. |
| **#12 spec authority** | Human decides; Gemini prepares/implements | Phase-plan / legacy-import confirmation | Confirm A1-UPDATED as canonical specification root without legacy imports. |
| **#14 shared-directory policy** | Human decides; ChatGPT clarifies; Gemini implements | #9 compatibility base, #12 spec location | Prepare per-executable vs installation-wide options and test implications. |
| **#15 reassessment** | Gemini; ChatGPT verifies | #9 base and #12 normative spec location | Establish source freshness and queued-action evidence. |
| **#16 incompatible lifecycle** | Gemini; ChatGPT verifies | Terminal/parked/auto-resume decision; coordinate with #15 | Short options brief, then lifecycle tests and implementation. |
| **#17 / #18 coverage** | Gemini; targeted ChatGPT evidence check | #9 merged; #18 spec docs depend on #12 | Run independent small sessions for non-vacuous coverage and persistence round-trips. |

---

## 3. Model Role Allocation & Handoff Protocol

### Active Allocation
- **Gemini**: Primary implementer. Executes code edits, test suites, local verification, focused revisions, and bounded repairs in dedicated git worktrees.
- **ChatGPT**: Verification, architecture, reproduction analysis, and arbitration layer. Conducts independent checks and arbitrates reviewer findings.
- **Claude**: Audit and material-risk layer. Reserved for occasional independent audits and high-risk architectural/safety decisions to conserve quota.
- **Human**: Retains final merge authority, policy governance, and architectural specification decisions.

### Fresh-Session Operating Instructions

#### For Gemini (Implementer)
1. Read [AGENTS.md](../AGENTS.md) and the live dashboard issue (or `docs/AI-CURRENT-STATE.md`).
2. Read the assigned GitHub Issue (`gh issue view <number>`). Note acceptance criteria and `### Allowed paths`.
3. Verify git status, fetch `origin/main`, and work in an isolated worktree.
4. Write failing regression fixtures first when addressing a defect.
5. Run task-relevant tests:
   - Application changes: `dotnet test tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj` (CI-verified).
   - Workflow automation changes: `python -m unittest discover -s tests/ai-workflow -v`.
6. Push your branch, open a PR with required headings (`Primary Issue`, `Summary`, `Scope`, `Verification`, `Documentation`), and verify that GitHub Actions CI checks complete successfully.
7. Record a factual session entry in `docs/ai-journal/<YYYY-MM-DD>-<issue>-gemini-session-<n>.md`. Do not edit out-of-scope files.

#### For ChatGPT (Verifier / Arbitrator)
1. Inspect the PR and its generated review packet artifact (`review_packet.md` + `full-diff.diff`).
2. Verify tested checkout provenance, TRX test counts, and boundary compliance against the Issue contract.
3. If an unresolved defect is identified, formulate an exact reproducer and request a single bounded repair cycle.
4. Distinguish blocking defects from non-blocking follow-up items.

#### For Claude (Auditor)
1. When explicitly engaged for a high-risk audit, review durable findings in [docs/AI-PILOT-LOG.md](AI-PILOT-LOG.md) and the generated review packet.
2. Focus on safety invariants, bypass routes, untrusted input handling, and test efficacy.
3. State whether test counts and execution logs were directly verified or accepted from platform metadata.

---

## 4. Observational Measurement Rules

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

1. **Issue #12 (Normative Specification Authority)**: Existing proposal establishes `docs/A1-UPDATED.md` as the canonical specification root, pending human confirmation of the phase plan and no-legacy-import policy.
2. **Issue #14 (Shared-Directory Multi-Executable Policy)**: Architectural decision regarding whether DXVK installation should be scoped per-executable or across entire shared installation directories.
3. **Issue #15 (API Reassessment before Queued Execution)**: Document mixed-module precedence and verify API status immediately prior to executing queued actions.
4. **Issue #16 (Lifecycle for Incompatible Pending Actions)**: Policy decision on terminal vs parked vs auto-resume behavior for actions blocked by modern API classification.
5. **Issue #17 & #18 (Follow-up Coverage & Persistence)**: Non-vacuous testing for adoption/reapply/restore and `GraphicsApi` enum persistence downgrade behavior.
6. **Repository Protection Rulesets**: GitHub Actions workflows (`ai-scope-check`, `ai-pr-hygiene`, `build-and-test`) currently run as status checks. Enabling mandatory branch protection rulesets remains a human administrative choice.
