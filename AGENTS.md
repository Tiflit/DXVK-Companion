# Agent Operating Rules

The adopted [kernel v0.4](docs/AI-WORKFLOW-KERNEL.md) governs; the [enforcement register](docs/AI-ENFORCEMENT-REGISTER.md) declares cooperative controls and gaps. The human alone merges.

## Fresh-Session Quick Start

1. Read the kernel and this document. Derive policy identity with `git log -1 --format=%H <acquired-revision> -- AGENTS.md docs/AI-WORKFLOW-KERNEL.md docs/AI-ENFORCEMENT-REGISTER.md docs/AI-DEVELOPMENT-WORKFLOW.md`; compare with the trusted checkpoint and refresh changed guidance. A fresh agent must read rules it has never learned: a hash is not context. Load relevant register entries on demand; reuse reliable unchanged context.
2. Inspect machine dashboard [Issue #27](https://github.com/Tiflit/DXVK-Companion/issues/27), then independently query task-critical remote main/head/base identities through GitHub API or connector. Dashboard, local HEAD and static orientation are not substitutes.
3. Read the assigned Issue, latest approval/hold and checkpoint. Acceptance criteria, delegated actions and `### Allowed paths` define the task. An `ai-observation` Issue is unassigned evidence, not permission.
4. Read the latest relevant session or PR evidence. Use the [module map](docs/ai/PROJECT-MAP.md) and [decision index](docs/ai/DECISION-INDEX.md) for task-relevant navigation; do not reload the full journal/pilot archive.

## Roles and Authority

- Gemini / Antigravity implements, tests within approved profiles, repairs and contributes ideas.
- ChatGPT coordinates, reviews behavior/integration/test effectiveness and arbitrates.
- Gemini web may contribute selectively at feature-design forks; no extra model pass is mandatory for routine work.
- Claude is reserved for consequential independent audits and difficult disagreements.
- The human decides unsettled product/policy choices and performs final merges.

Peer agreement, passing CI, editable approval flags and general continuation do not expand delegation. Shared credentials provide cooperative attribution, not independent human authentication. Approval sources are direct authorized-human GitHub decisions or clearly attributed exact coordinator transcriptions.

## Delegated Engineering and Stop Rules

Complete the approved task within its actions, resources and environment: implementation, meaningful regression coverage, authorized verification, routine CI repairs, constructive peer revisions and PR preparation. Do not seek permission for naming, helper extraction, test layout, wording or internal patterns within that scope. Record only assumptions needed to understand behavior or review.

**Withhold an undelegated action** involving:
- destructive operations or data-loss risk: force pushes, history rewrites, removal of unique work/backups, deletion outside the declared task/test resources;
- credentials/secrets, permission/sandbox changes, installation, paid APIs, personal-data publication or a new processing destination;
- changes outside allowed paths, protected policy, dependencies/project files, CI permissions, or deployment/anti-cheat/restoration guarantees without explicit task approval;
- unknown approval origin, conflicting authority, missing required execution protection, or uncertain sensitive transmission.

**Queue a human decision** when the task has not settled a configuration/file-format change, new dependency, public interface, or choice between different product behaviors. State the exact question, recommendation and effect; pause only dependent work and continue independent approved work. An already approved bugfix/feature behavior does not need approval again. Implementation choices consistent with that behavior remain delegated.

Human holds are global unless explicitly scoped; only an authorized human lifts them. Missing evidence blocks the dependent acceptance, not independent preparation. Preserve work at a safe boundary; do not improvise bypasses.

Task authors should use relevant module/directory prefixes or `/**` and matching test paths where appropriate; exact files remain useful for narrow/sensitive tasks. No blanket test access or automatic out-of-module expansion. Changes to existing assertions must be explained and reviewed for weakened coverage. Protected scope is declared in the task, not a new denylist feature in the evaluator.

Kernel/stricter project rules > assigned contract > [canonical specification](docs/spec/DXVK-COMPANION-SPEC.md) > [safety supplement](docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md). Contracts cannot silently override safety guarantees. Scope CI detects PR violations; required merge enforcement is not established.

## Local Execution Profiles

Before host builds/tests, harnesses, application/device runs or background processes, obtain authorization for the exact commands/suites, host, affected resources, disruption/resource limits, stopping and artifact retention/cleanup. A worktree or VM does not waive this requirement.

Approval may cover a reusable standing profile or a task-specific run. Tasks link the approved profile and its readiness condition; reuse its unchanged scope without another permission request. Respect the human's advance availability/notice requirement. Changed commands, hooks, effects, host or readiness invalidate reuse until resolved. Profiles do not authorize arbitrary shell commands, installs, desktop bridging, fault injection, force termination or destructive cleanup.

No standing host profile is activated by this amendment. Existing scoped approvals remain valid only within their boundaries; CLI investigation #94 remains paused. Approved cloud CI and routine source/Git/metadata inspection continue without local-execution approval.

If authorized behavior cannot be observed, report `NOT RUN (blocked: capability/desktop limitation)`; unapproved execution is `NOT RUN (pending human authorization)`. Preserve completed step outcomes; process existence alone is not GUI validation.

## Review and Delivery

Perform one grouped review of contract, behavior, safety, integration and test effectiveness. Include useful in-scope improvements.
- **Blocking:** unmet acceptance criterion, build/test failure, demonstrated regression, safety/privacy/scope violation, or missing/false evidence required for acceptance.
- **Nonblocking:** style, editorial polish, optional refactoring and improvements outside the approved objective. Record or fix within scope without another human stop.
- **Decision:** an unsettled behavior/authority choice listed above. Give the human a concrete recommendation.

Group blocking fixes into one repair cycle; verify repairs and affected context rather than restarting the audit. Any post-review code change, including suggested improvements, needs a focused delta check and applicable current CI before acceptance carries forward. Editorial-only changes need an applicability check. New defects, relevant base/contract movement or contradictory evidence justify expanding review; persistent blocking disagreement goes to the human.

Do not weaken tests for green CI. Bind required evidence to reviewed head/base and actual checkout; distinguish executed, inspected, reported and unavailable. Label self-review honestly; model agreement cannot establish test counts or independence.

## Records and Handoffs

Use [activity policy](docs/AI-ACTIVITY-JOURNAL.md). At milestones/yield, record completed work, changed/uncommitted files, revision/evidence, open findings/decisions and next owner/action. Link evidence rather than duplicating transcripts. For the next product trial, record actual interruptions with trigger, affected action and whether prior delegation allowed continuation; classify justified/unnecessary/uncertain. Do not invent timing, token savings or unseen stops.

Use `scripts/ai-workflow/update_pr_body.py` for existing PR-body updates and append-only Issue-body records: preserve history, expected baseline, privacy checks, recovery backups and readback. No manual whole-body Issue replacement or blind retry after uncertain writes. Connector-only agents use Conversation comments or hand off a prepared append to a capable environment. Client checks are not distributed locks.

Generate a submission snapshot once; a final revision-bound coordinator comment may supply completed evidence without regenerating historical pending-CI prose. Query status at submission/completion/blocker boundaries, not in polling loops. Consumer agents are still human-activated; no automatic dispatch, merge, settings change or paid orchestration is delegated.

Keep publications sanitized: repository-relative paths/approved aliases, no personal paths, secrets, raw transcripts or backup contents. Preserve recovery evidence outside disposable scratch areas. Encountered out-of-scope findings follow the [bounded observation policy](docs/AI-DEVELOPMENT-WORKFLOW.md#bounded-observation-reporting-and-triage): one deduplication search, up to three routine observations, no manufactured audits. Restoration/data-loss/privacy threats bypass the cap and stop affected unsafe work.
