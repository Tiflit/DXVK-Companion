# Current AI Development State & Handoff Orientation

> **Two-Layer Handoff System**:
> 1. **Automated Dashboard (Machine-Published Snapshot)**: Automatically refreshed facts (current main SHA, open PRs, CI identities, tested checkouts) are published by GitHub Actions to the dedicated issue: `[AI Dashboard] Current Repository State & Handoff Orientation` (find via `gh issue list --search "[AI Dashboard]"`). It is an automatically refreshed snapshot rather than real-time state or independently authenticated authority; underlying GitHub records remain authoritative, and machine publication does not authenticate human-only approvals.
> 2. **Curated Repository Orientation (This Document)**: Curated work queue, role allocation, architectural decisions, and handoff protocols. The automated dashboard generator reads and republishes this guidance; agents update it directly through authorized documentation tasks.
>
> **Governing Navigation & Selective Reading**: Routine agent sessions must NOT read the entire historical pilot log or journal archive. To conserve context and avoid drift, follow the 4-step startup route in [AGENTS.md](../AGENTS.md), navigating under adopted [AI-WORKFLOW-KERNEL.md](AI-WORKFLOW-KERNEL.md) (v0.4) and [AI-ENFORCEMENT-REGISTER.md](AI-ENFORCEMENT-REGISTER.md) via on-demand register loading while keeping mandatory project operating rules visible.

- **Current Repository Priority**: Validating a reliable, low-overhead multi-agent workflow centered on GitHub ([workflow purpose](AI-DEVELOPMENT-WORKFLOW.md#purpose)); broader application feature work and release testing follow separately under human direction.
- **Repository Status Snapshot**: Published periodically by GitHub Actions in dashboard [**Issue #27**](https://github.com/Tiflit/DXVK-Companion/issues/27) (`[AI Dashboard] Current Repository State & Handoff Orientation`) for latest observed default-branch SHA, open PRs, and active CI runs.
- **Active Task & PR Tracking**: Task assignments and open PR inventories are tracked dynamically through GitHub queries (`gh issue list`, `gh pr list`) and reflected in the dashboard snapshot. This curated document provides stable architecture, role allocation, and governance protocols; it does not maintain a second live inventory by hand.
- **Optional Template Extraction**: DXVK's read-only preflight check (`scripts/ai-workflow/preflight_check.py`) and role handoff preview (`scripts/ai-workflow/preview_role_handoff.py`) utilities were extracted into a stack-neutral offline toolkit in candidate repository [`Tiflit/Agent-Workflow`](https://github.com/Tiflit/Agent-Workflow) ([`tooling/offline/README.md`](https://github.com/Tiflit/Agent-Workflow/blob/b283a23620a7ade163577da1d12d8ed461f18181/tooling/offline/README.md)). Historical extraction is pinned to DXVK base `1ce8b28f983ceb9b610c5e495fac02dac5c5a72f` and template extraction pin `b283a23620a7ade163577da1d12d8ed461f18181`, distinguishing it from current candidate main (`7f6d7a8800777ca96229a4a1220dad3c00283d13`). Both repositories maintain separate copies with no automatic synchronization or adopted upstream version policy. Agent-Workflow remains an unadopted candidate template and does not replace DXVK's adopted kernel v0.4 or enforcement register.

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
| **PR #19** (`workflow/review-packet-provenance`) | `0dcc2bd` | **MERGED** | Review packet provenance hardening, TRX parsing, scope/hygiene CI checks reporting PR violations (Issue #11). |
| **PR #10** (`docs/pilot-2-current-checkpoint`) | `e7b6e06` | **MERGED** | Preserved final Pilot #2 review, workflow handoff, and pilot outcomes. |
| **PR #8** (`automation/ai-workflow-foundation`) | `38b4d83` | **MERGED** | Initial GitHub-native workflow foundations. |

---

## 2. Active Work Governance & Decision Prerequisites

- **Active Priority**: Validating a reliable, low-overhead multi-agent workflow on GitHub ([workflow purpose](AI-DEVELOPMENT-WORKFLOW.md#purpose)); broader feature work follows under human direction. Task assignments and PR inventories are derived dynamically via GitHub CLI and dashboard snapshot ([Issue #27](https://github.com/Tiflit/DXVK-Companion/issues/27)); static docs maintain stable governance rather than a duplicate live inventory.

### Decision Governance Rules
- **Decision Governance Block**: Tasks requiring architectural or policy choices must specify a Decision Governance Block in the Issue description (`Decision required`, `Proposed option`, `Status`, `Source of explicit human approval`).
- **Autonomous Routine Repairs vs. Consequential Choices**: Routine in-scope implementation, compilation repairs, and grouped review repairs proceed autonomously without stopping. Consequential choices (architectural direction, product policy changes, contract changes, major scope expansion) require recorded human approval.
- **Approval Routes & Provenance**: Permitted approval routes are direct human decisions and clearly attributed coordinator transcriptions (cooperative, not independently authenticated). Recommendations, passing CI, or generic continuation do not grant new authority.
- **Human Authority & Pending Decisions**: The human developer retains exclusive design policy and final merge authority. Preparatory investigation and decision briefs may proceed while decisions are pending, but unapproved production policy changes must not be implemented or merged.

### Local Test Execution Authorization Gate
- **Human Authorization Required**: Executing tests on the user's host (unit/integration suites, desktop/application runs, device testing) requires explicit human authorization recorded in the task Issue ([AGENTS.md](../AGENTS.md)); disposable folders or local VMs do not waive the gate. CI runs and read-only source/evidence retrieval do not require prompts.
- **Approved Profile Reuse**: When the human approves a local execution profile in an Issue (e.g. Issue #100), that approval provides standing authorization for that specific scope. However, standing authorization does not imply host capability or session readiness: unreviewed Issue #100 implementation work remains non-runnable until reviewed, and the required .NET 8 SDK was missing on the host. Reusing an approved profile requires verifying prerequisite tools (.NET 8 SDK) and repository preparation (clean worktree) before attempting execution; never assume host capability or treat unreviewed #100 implementation work as runnable without them.
- **Reporting & Boundaries**: Built-in OS scripts and test processes do not authorize desktop bridging or forced process termination. Honestly record unexecuted tests as `NOT RUN (pending human authorization)` or `NOT RUN (blocked: capability/desktop limitation)` rather than claiming passes.

---

## 3. Model Role Allocation & Handoff Protocol

### Active Allocation
- **Gemini**: Primary implementer. Executes code edits, test suites, local verification, focused revisions, and bounded repairs in dedicated git worktrees.
- **ChatGPT**: Verification, architecture, reproduction analysis, and arbitration layer. Conducts independent checks and arbitrates reviewer findings.
- **Claude**: Audit and material-risk layer. Reserved for occasional independent audits and high-risk architectural/safety decisions to conserve quota.
- **Human**: Retains final merge authority, policy governance, and architectural specification decisions.

### Fresh-Session Operating Instructions

#### For Gemini (Implementer)
1. Read [AGENTS.md](../AGENTS.md) and the dashboard snapshot for compact orientation under adopted [AI-WORKFLOW-KERNEL.md](AI-WORKFLOW-KERNEL.md) (v0.4) and [AI-ENFORCEMENT-REGISTER.md](AI-ENFORCEMENT-REGISTER.md). The present repository priority is validating a reliable multi-agent development workflow ([workflow purpose](AI-DEVELOPMENT-WORKFLOW.md#purpose)). Independently verify task-critical remote identities mechanically (e.g. `gh api repos/Tiflit/DXVK-Companion/git/ref/heads/main --jq .object.sha`), distinguishing remote default branch, PR head/base, and local workspace HEAD. Underlying GitHub records are authoritative; this static document provides stable governance, not live task state.
2. Read the assigned GitHub Issue (`gh issue view <number>`). Note acceptance criteria and `### Allowed paths`.
3. Verify git status, fetch `origin/main`, and work in an isolated worktree. Reference durable evidence using portable GitHub URLs (referencing immutable commit SHAs, PRs, or Issue numbers), avoiding personal home paths, raw transcripts, or secrets.
4. Write failing regression fixtures first when addressing a defect.
5. Verify task-relevant tests:
   - **Local Test Execution Gate**: Before executing test suites, desktop/application runs, or device tests on the user's local system, verify that explicit human authorization is recorded in the assigned Issue for the proposed scope ([AGENTS.md](../AGENTS.md)). If unapproved or blocked, do NOT execute locally; honestly record checks as `NOT RUN (pending human authorization)` or `NOT RUN (blocked: capability/desktop limitation)` rather than claiming `PASS` or silently omitting them, while preserving verified results for steps already executed.
   - **CI & Remote Verification**: GitHub Actions CI executes automatically on pull request pushes (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`). Distinguish remote CI verification from local test execution.
   - When authorized for local execution:
     - Application changes: `dotnet test tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj`.
     - Workflow automation changes: `python -m unittest discover -s tests/ai-workflow -v`.
6. Push your branch, open a PR with required headings (`Primary Issue`, `Summary`, `Scope`, `Verification`, `Documentation`) and one initial durable submission handoff snapshot generated via `scripts/ai-workflow/generate_handoff.py`. Check submission state once via GitHub CLI/API at the boundary without busy sleep/status polling. If CI checks are pending at submission, report status and yield honestly without polling loops or regenerating snapshots solely to refresh state. The final coordinator review comment supplies matching-revision CI evidence, test totals, and synthetic merge provenance without requiring a PR-body snapshot refresh. Routine in-scope implementation, compilation repairs, and grouped review repairs proceed autonomously.
7. Record a factual session entry in `docs/ai-journal/<YYYY-MM-DD>-<issue>-gemini-session-<n>.md`. Do not edit out-of-scope files.

#### For ChatGPT (Verifier / Arbitrator)
1. Inspect the PR and its generated review packet artifact (`review_packet.md` + `full-diff.diff`).
2. Verify tested checkout provenance, TRX test counts, and boundary compliance against the Issue contract. Supply verified matching-revision CI evidence, test counts, and synthetic merge provenance in a final revision-bound coordinator review/acceptance comment.
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
- **Review cadence**: After three completed task handoffs, the coordinator reviews available journal entries to identify recurring friction points. (The first post-#99 autonomy trial comprising PRs #102, #105, and #108 is concluded in [`docs/ai-journal/2026-10-14-post-99-autonomy-trial-closeout.md`](ai-journal/2026-10-14-post-99-autonomy-trial-closeout.md)).

### Verification Limitations & Release Readiness
- **Cleared Backlog != Release Readiness**: Successful task backlog clearance and headless test passes establish bounded component safety and workflow continuity, but do not constitute broader release readiness.
- **Verification Environment Limitations**: Headless CI environments verify headless logic, simulated mocks, and automated workflows; runtime behaviors such as Windows GUI notification delivery (e.g. PR #37 balloon/toast notifications) and live graphics driver interactions remain unverified without dedicated manual or desktop integration testing.
- **Recorded Feature Limitations ([PR #37](https://github.com/Tiflit/DXVK-Companion/pull/37))**: Under Issue #16's lifecycle policy, conservative suppression retains the cancellation marker on process-based and direct Reapply paths unless cleared by an explicit user request; end-to-end GUI detection and crash/concurrency edge cases remain uncertified by headless test suites.

---

## 5. Architectural & Governance Decisions

### Unresolved Decisions
- **None currently pending**: All specifically tracked Phase A architectural and safety policy decisions (#12, #14, #15, #16, #18, #106) are resolved and implemented. Future policy implementation remains blocked until explicit human approval is recorded in the respective Issue.

### Preserved Approved Decisions
- **Issue #106 / #107 (Library Schema Compatibility & Startup Refusal)**: Refuse startup cleanly on newer schemas (`SchemaVersion > CurrentSchemaVersion`), guard storage mutations with `IncompatibleLibrarySchemaException`, and preserve newer data and backups byte-for-byte intact ([Issue #106](https://github.com/Tiflit/DXVK-Companion/issues/106)/[Issue #107](https://github.com/Tiflit/DXVK-Companion/issues/107), [PR #108](https://github.com/Tiflit/DXVK-Companion/pull/108)).
- **Issue #101 / #103 / #104 (Storage Staged-Save Persistence)**: Atomic temporary staging before replace in `ProfileStore` ([PR #102](https://github.com/Tiflit/DXVK-Companion/pull/102)) and operation-owned GUID staging in `GameLibraryStore` ([PR #105](https://github.com/Tiflit/DXVK-Companion/pull/105)).
- **Issue #14 (Shared-Directory Multi-Executable Policy)**: Installation-wide compatibility refusal across shared directories, preserving Restore and RestoreAll ([Issue #14](https://github.com/Tiflit/DXVK-Companion/issues/14), [PR #35](https://github.com/Tiflit/DXVK-Companion/pull/35)).
- **Issue #15 (API Reassessment before Queued Execution)**: Pre-execution API reassessment, conservative conflict handling, and executable-path identity alignment ([Issue #15](https://github.com/Tiflit/DXVK-Companion/issues/15), [PR #36](https://github.com/Tiflit/DXVK-Companion/pull/36)).
- **Issue #16 (Lifecycle for Incompatible Pending Actions)**: Terminal cancellation for compatibility-refused pending actions, durable cancellation context, and no automatic revival without fresh intent ([Issue #16](https://github.com/Tiflit/DXVK-Companion/issues/16), [PR #37](https://github.com/Tiflit/DXVK-Companion/pull/37)).
- **Issue #32 (Clean-Slate V1 Legacy-Import Removal)**: Clean-slate V1 legacy profile import removal following Issue #18 ([Issue #32](https://github.com/Tiflit/DXVK-Companion/issues/32), [PR #34](https://github.com/Tiflit/DXVK-Companion/pull/34)).
- **Normative Specification Authority (Issue #12)**: Canonical specification is [`docs/spec/DXVK-COMPANION-SPEC.md`](../docs/spec/DXVK-COMPANION-SPEC.md), with [`docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md`](../docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md) as normative safety supplement.
- **Deployment Guards (Issue #6)**: Refuse DXVK deployment for DX12 and Vulkan executables while preserving restore operations.
- **Local Test Execution Authorization Gate (Issue #56)**: Explicit human authorization required before executing local unit/integration tests or application runs on the host ([Issue #56](https://github.com/Tiflit/DXVK-Companion/issues/56)).
- **Repository Protection Rulesets**: Status checks run in CI; mandatory branch protection rulesets remain a human administrative choice.


---

## 6. Dashboard Discovery, Fallback & Operations

- **Discovery Mechanism**: The automated repository state snapshot is published by GitHub Actions to the dedicated machine-owned Issue titled `[AI Dashboard] Current Repository State & Handoff Orientation` (searchable via `gh issue list --search "[AI Dashboard]"` or by label `ai-dashboard`). It is an automatically refreshed snapshot, not real-time state or independently authenticated authority; underlying GitHub records remain authoritative, and machine publication does not authenticate human-only approvals.
- **Stale / Offline Fallback Protocol**: If the dashboard snapshot Issue is unavailable, closed, rate-limited, or reports an `INCOMPLETE` status, fresh agent sessions must inspect underlying GitHub records directly: independently acquire remote default-branch identity via connector/API (e.g. `gh api repos/Tiflit/DXVK-Companion/git/ref/heads/main --jq .object.sha`), distinguish it from local HEAD and cached `origin/main`, and inspect underlying task/PR records directly (`gh issue list`, `gh pr list`). Retain this static document as stable guidance, not a second live inventory.
- **Publisher Activation**: Active on `main` following merge of PR #26 (publishing snapshot updates to dedicated Issue #27).
- **Administrative Disablement & Stop Boundaries**: In the event of unexpected publishing behavior or workflow errors, disabling workflows is a human administrative decision (e.g. via GitHub repository settings or `gh workflow disable`). Under kernel guarantee K6, safe in-flight stopping is bounded at defined safe boundaries rather than instantaneous cross-agent cancellation.
