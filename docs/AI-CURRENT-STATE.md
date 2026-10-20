# Current AI Development State & Handoff Orientation

Stable navigation, not a live task inventory. Start with [AGENTS.md](../AGENTS.md) and adopted [kernel v0.4](AI-WORKFLOW-KERNEL.md); load relevant [register](AI-ENFORCEMENT-REGISTER.md) entries on demand. Fresh agents must read unfamiliar rules: matching hashes cannot supply missing context. Do not routinely load the full pilot log or journal archive.

Two layers remain separate: [Issue #27](https://github.com/Tiflit/DXVK-Companion/issues/27) publishes machine-acquired status; this document supplies curated orientation. Underlying GitHub records control current tasks/revisions. A dashboard is a snapshot, not real-time state, authenticated human approval or a replacement for independent API checks.

## 1. Foundational Milestones & Merged PRs (Historical through 2026-10-04)

The preserved [historical milestone table](ai/WORKFLOW-CONSOLIDATION.md#historical-foundational-milestones) and [template extraction record](ai/WORKFLOW-CONSOLIDATION.md#historical-template-extraction) are reference material, not a current inventory. The [pilot log](AI-PILOT-LOG.md) and [session archive](ai-journal/) remain in place; use only relevant entries.

## 2. Active Work Governance & Decision Prerequisites

- **Active Priority**: The repository remains a testbed for the [GitHub-centered workflow](AI-DEVELOPMENT-WORKFLOW.md#purpose). Human direction and scoped Issues select current product/workflow work; this orientation neither assigns a task nor freezes product work behind optional workflow batches. Acquire actual Issue/PR status and remote main/head/base independently from the API, distinguishing local HEAD and cached refs.
- **Decision Governance Block**: Tasks with architectural/policy choices specify `Decision required`, `Proposed option`, `Status`, and `Source of explicit human approval`. Routine in-scope implementation, compilation and grouped repairs continue autonomously. Unsettled consequential choices require recorded human approval; preparatory investigation may continue, dependent unapproved implementation may not.
- **Authority**: The human retains final merge, policy and specification decisions. Direct authorized-human decisions and clearly attributed coordinator transcriptions are the approval routes; shared credentials do not authenticate human-only actions. Recommendations, labels, peer agreement, CI or general continuation do not enlarge delegation.
- **Local Test Execution Authorization Gate**: Follow [AGENTS.md](../AGENTS.md#local-execution-profiles). No standing host profile is activated here. An unchanged scoped approval may be reused only with its conditions satisfied: host/resources/commands, prerequisite tools (.NET 8 for the referenced #100 suite), clean/synced checkout and advance-notice/current human availability acknowledgment. See historical [#100 approval](https://github.com/Tiflit/DXVK-Companion/issues/100#issuecomment-6177558065) and [disposition](https://github.com/Tiflit/DXVK-Companion/issues/100#issuecomment-6177757634); an old approval is not present readiness. Cloud CI and routine source/Git inspection remain distinct from host execution. Built-in scripts/processes do not authorize desktop bridging or forced termination. Preserve actual step outcomes; unavailable execution is `NOT RUN (pending human authorization)` or `NOT RUN (blocked: capability/desktop limitation)`, never an inferred pass.

## 3. Model Role Allocation & Handoff Protocol

The four-step startup and role boundaries are in [AGENTS.md](../AGENTS.md#fresh-session-quick-start): Gemini implements, ChatGPT coordinates/reviews/arbitrates, Claude is selectively engaged for consequential audits, and the human decides and merges. Apply explicit human/task assignments rather than treating this page as an automatic dispatcher. The [project map](ai/PROJECT-MAP.md) links modules, relevant tests/spec sections and the existing implementation/review submission procedure. The [decision index](ai/DECISION-INDEX.md) links binding clarifications and their limits.

### One current-task checkpoint

Use the current task's existing Issue/PR conversation or task-allowed session journal, with one concise checkpoint linking detailed evidence instead of copying it across channels. Preserve older records; a later checkpoint links corrections rather than silently rewriting history. The [activity policy](AI-ACTIVITY-JOURNAL.md) still requires factual session records and milestone/yield checkpoints; no new shared-file edit or expanded journal allowlist is required.

Include: objective/Issue and allowed scope; remote head/base, separate local HEAD and policy identity; approval/hold links; completed work and revision-bound run/attempt/artifact/results; unresolved finding IDs/decisions and unavailable checks; dirty/unique work and recovery preservation; next owner and exact action. Unknown information stays unknown. A checkpoint, label or hash is not a distributed lease, approval or fresh-agent context. Publish portable sanitized links, never personal paths, private logs, transcripts or secrets. Existing body writes use the guarded helper under AGENTS; comments do not bypass privacy/history obligations.

## 4. Observational Measurement Rules

Session records retain counts of human action/decision interventions, separate status queries, repeated investigation from missing context, journal words and whether a blocker required another session. Provider tokens/quota and elapsed human time are “not measured” unless directly observed. After three completed handoffs the coordinator reviews available journals for recurring friction; the first post-#99 trial's [closeout](ai-journal/2026-10-14-post-99-autonomy-trial-closeout.md) remains historical evidence. Document bytes/words measure text only, not tokens, savings or compliance.

### Verification Limitations & Release Readiness

Cleared tasks and headless tests do not establish release readiness, Windows GUI notification delivery or graphics/driver behavior. Dedicated authorized manual/device evidence remains necessary for those claims. PR #37's conservative cancellation-marker behavior and unverified GUI/crash/concurrency cases remain indexed in [established decisions](ai/DECISION-INDEX.md); no certification is added here.

## 5. Architectural & Governance Decisions

The [decision index](ai/DECISION-INDEX.md) preserves source links, disposition/status, supersession and limits. These are existing decisions, not new approvals or a live assertion that no decisions remain pending. Future policy implementation remains blocked until explicit human approval is recorded in the respective Issue.

Core references retained for dashboard consumers: Issue #14 [shared-directory policy](https://github.com/Tiflit/DXVK-Companion/issues/14) / [PR #35](https://github.com/Tiflit/DXVK-Companion/pull/35); Issue #15 [queued reassessment](https://github.com/Tiflit/DXVK-Companion/issues/15) / [PR #36](https://github.com/Tiflit/DXVK-Companion/pull/36); Issue #16 [terminal cancellation](https://github.com/Tiflit/DXVK-Companion/issues/16) / [PR #37](https://github.com/Tiflit/DXVK-Companion/pull/37). Normative product sources remain [DXVK-COMPANION-SPEC.md](spec/DXVK-COMPANION-SPEC.md) and the [safety supplement](spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md). CI reports status; mandatory merge protection and administrative setup remain human decisions, not guarantees inferred from passing checks.

## 6. Dashboard Discovery, Fallback & Operations

Find the dedicated `[AI Dashboard] Current Repository State & Handoff Orientation` Issue by title (`gh issue list --search "[AI Dashboard]"`) or `ai-dashboard` label; [Issue #27](https://github.com/Tiflit/DXVK-Companion/issues/27) publication was activated by PR #26.

**Stale / Offline Fallback Protocol**: If the snapshot is unavailable, closed, rate-limited or `INCOMPLETE`, inspect underlying task/PR records and independently acquire remote default-branch identity through connector/API, such as `gh api repos/Tiflit/DXVK-Companion/git/ref/heads/main --jq .object.sha`. Distinguish this from local HEAD and cached `origin/main`. If those records are also unavailable, preserve known evidence and mark dependent facts unavailable; static prose does not become live authority.

Disabling publishing/workflows remains a human administrative decision (repository settings or `gh workflow disable`), not a troubleshooting action authorized here. K6 stopping remains bounded at defined safe boundaries; no instantaneous cross-agent cancellation is promised. CLI investigation #94 remains paused.
