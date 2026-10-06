# Current AI Development State & Handoff Orientation

> **Two-Layer Handoff System**:
> 1. **Live Automated Dashboard (Machine-Published)**: Real-time facts (current main SHA, open PRs, CI identities, tested checkouts) are automatically maintained in the dedicated GitHub Issue: `[AI Dashboard] Current Repository State & Handoff Orientation` (find via `gh issue list --search "[AI Dashboard]"`).
> 2. **Curated Repository Orientation (This Document)**: Curated work queue, role allocation, architectural decisions, and handoff protocols. The automated dashboard generator reads and republishes this guidance; agents update it directly through authorized documentation tasks.
>
> **Selective Reading Rule**: Routine agent sessions must NOT read the entire historical pilot log or journal archive. To conserve context and avoid drift, follow the 4-step startup route in [AGENTS.md](../AGENTS.md).

- **Current Repository Priority**: Validating a reliable, low-overhead multi-agent workflow centered on GitHub ([workflow purpose](AI-DEVELOPMENT-WORKFLOW.md#purpose)); broader application feature work and release testing follow separately under human direction.
- **Live Repository Status**: Maintained in real time by GitHub Actions in authenticated dashboard [**Issue #27**](https://github.com/Tiflit/DXVK-Companion/issues/27) (`[AI Dashboard] Current Repository State & Handoff Orientation`) for live default-branch SHA, open PRs, and active CI runs.
- **Active Task & PR Tracking**: Live task assignments and open PR inventories are tracked dynamically in the automated dashboard and via GitHub queries (`gh issue list`, `gh pr list`). This curated document provides stable architecture, role allocation, and governance protocols; it does not maintain a second live inventory by hand.

---

## 1. Foundational Milestones & Merged PRs (Historical through 2026-10-04)

Major foundational pilot features, bugfixes, and CI harnesses merged into `main` include:

| PR / Branch | Merged Commit | Status | Milestone Summary |
|---|---|---|---|
| **PR #30** (`test/issue-18-graphicsapi-persistence-downgrade`) | `f576ddd` | **MERGED** | `GraphicsApi` enum persistence downgrade verification and clean-slate V1 alignment (Issue #18). |
| **PR #29** (`test/issue-17-non-vacuous-guard-coverage`) | `56808e5` | **MERGED** | Non-vacuous test coverage for adoption, reapply, and restore guards (Issue #17). |
| **PR #28** (`spec/issue-12-canonical-specification`) | `68c604e` | **MERGED** | Canonical specification consolidation and Phase A.5 safety invariant restoration (Issue #12). |
| **PR #26** (`workflow/issue-25-compact-handoffs`) | `955ca78` | **MERGED** | Compact handoff automation and live dashboard publication (Issue #25). |
| **PR #9** (`issue-6-prevent-dx12-vulkan-deployment`) | `ce74e1e` | **MERGED** | Prevent DXVK deployment for DX12/Vulkan (Issue #6). |
| **PR #4** (`pilot/companion-version-ordering`) | `230c8ae` | **MERGED** | CompanionVersion numeric ordering (Issue #5). |
| **PR #24** (`audit/issue-22-privacy-coverage`) | `4f44059` | **MERGED** | Historical privacy audit, rule redaction, and durable evidence annex (Issue #22). |
| **PR #23** (`fix/issue-13-reapply-original-baseline`) | `51691ee` | **MERGED** | Reapply original-baseline capture and backup preservation defect repair (Issue #13). |
| **PR #21** (`docs/issue-20-workflow-cleanup`) | `093664d` | **MERGED** | Post-#19 cleanup, connector journaling, and dashboard orientation (Issue #20). |
| **PR #19** (`workflow/review-packet-provenance`) | `0dcc2bd` | **MERGED** | Review packet provenance hardening, TRX parsing, blocking scope/hygiene gates (Issue #11). |
| **PR #10** (`docs/pilot-2-current-checkpoint`) | `e7b6e06` | **MERGED** | Preserved final Pilot #2 review, workflow handoff, and pilot outcomes. |
| **PR #8** (`automation/ai-workflow-foundation`) | `38b4d83` | **MERGED** | Initial GitHub-native workflow foundations. |

---

## 2. Active Work Governance & Decision Prerequisites

- **Active Priority**: Validating a reliable, low-overhead multi-agent workflow centered on GitHub ([workflow purpose](AI-DEVELOPMENT-WORKFLOW.md#purpose)). Broader application revisions and release testing follow separately under human direction.

Active task assignments, in-flight work, and open PR inventories are derived dynamically from GitHub (see authenticated dashboard [Issue #27](https://github.com/Tiflit/DXVK-Companion/issues/27) and `gh issue list` / `gh pr list`). Static documentation maintains stable governance rules, architecture, and role allocation without duplicating a secondary live task inventory.

### Decision Governance Rules
- **Decision Governance Block**: Tasks requiring architectural or policy choices must contain a structured Decision Governance Block in the GitHub Issue description (`Decision required`, `Proposed option`, `Status`, `Source of explicit human approval`).
- **Human Approval Preflight**: Policy implementation is blocked until explicit human approval is authenticated in the task Issue. Agent recommendations (e.g. from ChatGPT or Gemini) do not constitute approval.
- **Investigation Allowed**: Preparatory investigation, options analysis, and decision briefs may proceed while decisions are pending, but policy changes to production code or canonical specifications must not be implemented or merged without recorded approval.

### Local Test Execution Authorization Gate
- **Human Authorization Required**: Executing tests on the user's local system (unit/integration suites, desktop/application runs, device testing) requires explicit human authorization recorded in the task Issue before execution begins ([AGENTS.md](../AGENTS.md)). A disposable folder, separate directory, or local VM does not waive the gate.
- **Reporting & Boundaries**: Built-in OS scripts and test-created processes do not authorize desktop bridging or forced termination without explicit scope and permission. Distinguish `NOT RUN (pending human authorization)` from `NOT RUN (blocked: capability/desktop limitation)`, while preserving verified results for steps already executed.
- **Safe Preparation & CI**: GitHub-hosted CI runs and read-only source/evidence retrieval do not require approval prompts.

---

## 3. Model Role Allocation & Handoff Protocol

### Active Allocation
- **Gemini**: Primary implementer. Executes code edits, test suites, local verification, focused revisions, and bounded repairs in dedicated git worktrees.
- **ChatGPT**: Verification, architecture, reproduction analysis, and arbitration layer. Conducts independent checks and arbitrates reviewer findings.
- **Claude**: Audit and material-risk layer. Reserved for occasional independent audits and high-risk architectural/safety decisions to conserve quota.
- **Human**: Retains final merge authority, policy governance, and architectural specification decisions.

### Fresh-Session Operating Instructions

#### For Gemini (Implementer)
1. Read [AGENTS.md](../AGENTS.md) and the live dashboard issue for compact orientation. The present repository priority is validating a reliable multi-agent development workflow ([workflow purpose](AI-DEVELOPMENT-WORKFLOW.md#purpose)). Independently verify task-critical remote identities mechanically (e.g. `gh api repos/Tiflit/DXVK-Companion/git/ref/heads/main --jq .object.sha`), distinguishing remote default branch, PR head/base, and local workspace HEAD. Underlying GitHub records are authoritative; this static document provides stable governance, not live task state.
2. Read the assigned GitHub Issue (`gh issue view <number>`). Note acceptance criteria and `### Allowed paths`.
3. Verify git status, fetch `origin/main`, and work in an isolated worktree. Reference durable evidence using portable GitHub URLs (referencing immutable commit SHAs, PRs, or Issue numbers), avoiding personal home paths, raw transcripts, or secrets.
4. Write failing regression fixtures first when addressing a defect.
5. Verify task-relevant tests:
   - **Local Test Execution Gate**: Before executing test suites, desktop/application runs, or device tests on the user's local system, verify that explicit human authorization is recorded in the assigned Issue for the proposed scope ([AGENTS.md](../AGENTS.md)). If unapproved or blocked, do NOT execute locally; honestly record checks as `NOT RUN (pending human authorization)` or `NOT RUN (blocked: capability/desktop limitation)` rather than claiming `PASS` or silently omitting them, while preserving verified results for steps already executed.
   - **CI & Remote Verification**: GitHub Actions CI executes automatically on pull request pushes (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`). Distinguish remote CI verification from local test execution.
   - When authorized for local execution:
     - Application changes: `dotnet test tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj`.
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

### Verification Limitations & Release Readiness
- **Cleared Backlog != Release Readiness**: Successful task backlog clearance and headless test passes establish bounded component safety and workflow continuity, but do not constitute broader release readiness.
- **Verification Environment Limitations**: Headless CI environments verify headless logic, simulated mocks, and automated workflows; runtime behaviors such as Windows GUI notification delivery (e.g. PR #37 balloon/toast notifications) and live graphics driver interactions remain unverified without dedicated manual or desktop integration testing.
- **Recorded Feature Limitations ([PR #37](https://github.com/Tiflit/DXVK-Companion/pull/37))**: Under Issue #16's lifecycle policy, conservative suppression retains the cancellation marker on process-based and direct Reapply paths unless cleared by an explicit user request; end-to-end GUI detection and crash/concurrency edge cases remain uncertified by headless test suites.

---

## 5. Architectural & Governance Decisions

### Unresolved Decisions
- **None currently pending**: All specifically tracked Phase A architectural and safety policy decisions (#12, #14, #15, #16, #18) are resolved and implemented. Policy implementation for future tasks remains blocked until explicit human approval is authenticated in the respective GitHub Issue.

### Preserved Approved Decisions
- **Issue #14 (Shared-Directory Multi-Executable Policy)**: Approved installation-wide compatibility refusal across shared directories, with Restore and RestoreAll operations preserved ([Issue #14](https://github.com/Tiflit/DXVK-Companion/issues/14), [PR #35](https://github.com/Tiflit/DXVK-Companion/pull/35)).
- **Issue #15 (API Reassessment before Queued Execution)**: Implemented pre-execution API reassessment, conservative conflict handling, and executable-path identity alignment ([Issue #15](https://github.com/Tiflit/DXVK-Companion/issues/15), [PR #36](https://github.com/Tiflit/DXVK-Companion/pull/36)).
- **Issue #16 (Lifecycle for Incompatible Pending Actions)**: Implemented terminal cancellation for compatibility-refused pending actions, durable cancellation context, and automatic non-revival until fresh deliberate user intent ([Issue #16](https://github.com/Tiflit/DXVK-Companion/issues/16), [PR #37](https://github.com/Tiflit/DXVK-Companion/pull/37)).
- **Issue #32 (Clean-Slate V1 Legacy-Import Removal)**: Implemented clean-slate V1 legacy profile import removal following Issue #18 ([Issue #32](https://github.com/Tiflit/DXVK-Companion/issues/32), [PR #34](https://github.com/Tiflit/DXVK-Companion/pull/34)).
- **Normative Specification Authority (Issue #12)**: Canonical specification is [`docs/spec/DXVK-COMPANION-SPEC.md`](../docs/spec/DXVK-COMPANION-SPEC.md), with [`docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md`](../docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md) as normative safety supplement.
- **Clean-Slate V1 Policy**: No legacy configuration import; modern clean-slate setup.
- **Deployment Guards**: Refuse DXVK deployment for DX12 and Vulkan executables, while preserving restore operations.
- **Local Test Execution Authorization Gate (Issue #56)**: Required explicit human authorization before executing local unit/integration test suites, desktop/application runs, or device tests on the user's host system ([Issue #56](https://github.com/Tiflit/DXVK-Companion/issues/56)).
- **Repository Protection Rulesets**: GitHub Actions workflows (`ai-scope-check`, `ai-pr-hygiene`, `build-and-test`) currently run as status checks. Enabling mandatory branch protection rulesets remains a human administrative choice.


---

## 6. Live Dashboard Discovery, Fallback & Operations

- **Discovery Mechanism**: The live automated repository state is published to the dedicated machine-owned Issue titled `[AI Dashboard] Current Repository State & Handoff Orientation` (searchable via `gh issue list --search "[AI Dashboard]"` or by label `ai-dashboard`).
- **Stale / Offline Fallback Protocol**: If the live dashboard Issue is unavailable, closed, rate-limited, or reports an `INCOMPLETE` status, fresh agent sessions must inspect underlying GitHub records directly (verify latest default-branch commit via `git log -n 1 origin/main`, and open PRs via `gh pr list --state open`), consulting `docs/AI-CURRENT-STATE.md` only for stable governance rules and role allocation.
- **Publisher Activation**: Active on `main` following merge of PR #26 (currently publishing to authenticated Issue #27).
- **Rollback / Disable Procedure**: In the event of unexpected publishing behavior, the workflow can be instantly disabled via `gh workflow disable ai-current-state.yml` without altering credentials or codebase files.
