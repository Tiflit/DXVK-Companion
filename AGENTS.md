# Agent Operating Rules

This repository uses AI-assisted development with GitHub as the durable source of truth. DXVK-Companion serves as the application and pilot repository; the present active priority is establishing and validating a reliable, low-overhead multi-agent software development workflow ([workflow purpose](docs/AI-DEVELOPMENT-WORKFLOW.md#purpose)). Broader application revisions and release testing follow separately under human direction.

## Fresh-Session Quick Start (4-Step Startup Route)

Before proposing or modifying code in a fresh session:

1. **Operating rules**: Review this document (`AGENTS.md`) for role allocation and repository invariants.
2. **Live dashboard & remote verification**: Inspect the automated repository dashboard in the dedicated machine-owned Issue (`gh issue list --search "[AI Dashboard]"`). The dashboard is a compact orientation entry point, not an infallible source. Underlying GitHub records (commits, PRs, Issues) remain authoritative. Independently verify task-critical remote identities mechanically (e.g. `gh api repos/Tiflit/DXVK-Companion/git/ref/heads/main --jq .object.sha` to query the remote default-branch SHA; distinguish remote default branch, PR head/base, and local workspace HEAD). A dashboard/local HEAD match alone is not remote verification. Static [docs/AI-CURRENT-STATE.md](docs/AI-CURRENT-STATE.md) provides curated orientation (roles, stable decisions, governance) and must not serve as an alternative for live identities.
3. **Assigned task contract**: Inspect the assigned GitHub Issue (`gh issue view <number>`). Acceptance criteria and `### Allowed paths` are strictly authoritative.
4. **Latest relevant activity / evidence**: Read the latest session record in `docs/ai-journal/` or the PR review packet associated with the task. *(Historical pilot logs in `docs/AI-PILOT-LOG.md` are for selective reference only; do not read full historical archives for routine tasks).*

Log your activity per the [activity-record policy](docs/AI-ACTIVITY-JOURNAL.md). Persist a concise session file under `docs/ai-journal/` when allowed. When publishing an attributed activity record directly to an assigned Issue or PR body, agents MUST use `scripts/ai-workflow/update_pr_body.py` (`--issue <number> --append-file <path>` or `--pr <number> --body-file <path>`). Manual or whole-body replacement of Issue bodies is forbidden; Issue updates must be strictly append-only, validated for single-record markers (`AI-REVIEW-RECORD`, `AI-POST-MERGE-RECORD`, or `AI-ASSIGNMENT-RECORD`), pass fail-closed privacy checks, and verify expected base hash. If operating in a connector-only environment lacking direct terminal/CLI execution access, agents MUST NOT perform manual or whole-body overwrites; follow the reduced-capability stop/handoff route by recording the proposed append block in the PR packet or session journal and handing off to a CLI-capable agent or coordinator. Note tool limits: client-side checks and local recovery backups protect against accidental loss and detect stale baselines, but cannot provide atomic distributed locking or prevent uncoordinated writes by outside clients bypassing the helper. Human transcription is fallback only. Checkpoint at material milestones and before stopping; shared index edits are optional and must be in scope.

## Model Role Allocation

- **Gemini**: Primary implementation layer (repository code, test suites, local verification, focused revisions, bounded repairs).
- **ChatGPT**: Verification, architecture, reproduction analysis, and arbitration layer.
- **Claude**: Reserved for occasional independent audits and high-risk material decisions (quota-conserving).
- **Human**: Retains final merge authority, policy governance, and architectural decisions.

```text
Gemini implementation -> CI -> ChatGPT verification & arbitration -> Human merge authority
                                        ^
                                        | (occasional audits / material risk)
                                  Claude audit
```

## Scope and Invariants

- Make the smallest coherent change that satisfies the task contract.
- Stay strictly within the assigned task's `### Allowed paths`. Changes outside allowed paths cause automated CI failure.
- Never modify application code (`src/**`) or test suites (`tests/DXVKCompanion.PhaseA.Tests/**`) unless explicitly included in the task's allowed paths (e.g. an authorized application feature, bugfix, or integration-test task).
- **Specification Authority & Precedence**: The canonical project specification is [`docs/spec/DXVK-COMPANION-SPEC.md`](docs/spec/DXVK-COMPANION-SPEC.md), with [`docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md`](docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md) as the normative safety and identity supplement.
- **Precedence Order**: `Assigned Task Contract (Issue) > Canonical Specification > Safety Supplement`.
- **Safety Invariant Protection**: Task Issues cannot silently override safety invariants without an explicit human decision.

## Decision Governance and Policy Preflight

When a task involves an unresolved policy or architectural choice, the assigned Issue must contain a compact Decision Governance Block:

```markdown
### Decision Governance Block
- **Decision required**: <Brief statement of architectural/policy choice required>
- **Proposed option**: <Specific proposal / recommendation>
- **Status**: `PENDING` | `DECIDED`
- **Source of explicit human approval**: <Link to authentic human GitHub decision or clearly attributed coordinator transcription; None if pending>
```

- **Preflight Rule**: Agents may conduct investigation, prepare decision briefs, and clarify options while approval is pending, but MUST block policy implementation until explicit human approval is recorded in the task Issue.
- **Approval Disambiguation**:
  - Agent recommendations (e.g. ChatGPT recommendations) are NOT human approval.
  - Editable status text (e.g. `APPROVED_BY_HUMAN` or `DECIDED`) written by an agent is NOT proof of human approval.
  - CI success, passing tests, and unrelated PR merges are NOT approval.
  - Source of explicit human approval must be either: (1) a direct link to a human-authored GitHub comment or issue decision; or (2) a clearly attributed coordinator transcription citing the human developer's explicit instruction verbatim, explicitly noting that the transcription is not mechanically authenticated.
  - Do not decide architectural or safety policies in preparatory or unrelated workflow tasks.

## Tests and Evidence

- A green CI run is evidence, not absolute proof that an invariant is satisfied.
- Add regression coverage exercising real application orchestration rather than only mocked paths.
- Review packets generated by `.github/workflows/ai-review-packet.yml` serve as the standardized entry point for verification, binding immutable commit SHAs, checkout provenance, and structured Visual Studio TRX test totals.
- **Portable Evidence Links**: Use portable GitHub URLs (referencing inspected immutable commit SHAs, PRs, or Issue numbers where applicable) for durable source and evidence references so they resolve across both local environments and cloud agents. Generic repository-relative paths may appear separately. Never publish personal home paths (e.g., local user directories), raw transcripts, or secrets (`update_pr_body.py` enforces this with fail-closed privacy scanning before preview or PATCH).

## Revision and Handoff Discipline

- One focused review-driven revision cycle is the normal limit.
- **Session Continuity vs Reset**: Continue a reliable implementation session for focused revisions and bounded repairs; restart in a fresh session only when context window saturation, tool failure, or capability degradation requires it. Independent reviewer and auditor passes retain strict fresh-context discipline.
- **Session Checkpoint Instructions**: Before stopping or handing off, record a concise session checkpoint containing:
  1. Completed work
  2. Changed and uncommitted files
  3. Verified evidence and test results
  4. Open findings and pending decisions
  5. Exact next action and assigned owner
  Record conclusions and rationale, not long transcripts.
- Persistent material disagreements or unresolved risks escalate to the human rather than triggering unbounded revision loops.
- Do not automatically merge PRs, enable branch protection, or dispatch paid APIs.

