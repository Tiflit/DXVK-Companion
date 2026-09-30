# AI Development Pilot Log

This file records durable lessons from the multi-agent development experiments.

The goal is not to preserve complete conversations. Record decisions, observations, defects, and workflow lessons that future development sessions need to know.

## Pilot #1 — CompanionVersion version ordering

### Task

Correct `CompanionVersion.IsOutdatedComparedTo` so update detection uses numeric MAJOR.MINOR.PATCH ordering rather than string inequality.

Tracked by Issue #5 and implemented in PR #4.

### Agents

- Implementation: Gemini / Antigravity
- Independent review: Claude
- Architecture / workflow coordination: ChatGPT
- Deterministic validation: GitHub Actions

### Implementation outcome

The implementation was isolated onto a clean task branch rather than leaving the work on a branch containing unrelated changes.

The production change:

- accepts stable three-part numeric versions;
- accepts one leading `v` or `V`;
- compares major, minor, then patch numerically;
- returns false for malformed or unsupported inputs.

Regression coverage included equal, older, newer, numeric ordering, prefix handling, and malformed/unsupported inputs.

### Review outcome

The independent review found no Critical, High, or Medium defects.

It identified two low-severity/future-facing concerns:

1. prerelease versions are treated as unsupported rather than ordered;
2. build metadata would become unsupported if the project version pin were ever removed.

Neither contradicted the task contract, so the implementation was not expanded to address them.

### Workflow lessons

#### 1. The task contract must live in GitHub

The initial contract existed primarily in the AI conversation.

The independent reviewer therefore had to reconstruct requirements from conversation context, code, and tests.

**Decision:** GitHub Issue is the authoritative task contract.

#### 2. Task branches must be isolated

The original working branch contained unrelated changes.

**Decision:** Each pilot/task should use a branch whose starting point is the intended base and whose diff contains only task-related work.

#### 3. CI claims need evidence

An implementation agent's report is not sufficient evidence that CI passed.

**Decision:** Important workflow reports should identify the exact commit and actual GitHub Actions result.

#### 4. Independent review should remain independent

The reviewer should not receive another agent's reasoning as an authority.

**Decision:** Give Claude the Issue, actual branch/PR evidence, and focused review instructions, but do not give it Gemini's conclusions beforehand.

#### 5. Documentation is part of completion

The useful result of a pilot includes the workflow lesson, not just the code change.

**Decision:** Significant pilots update durable repository documentation.

## Pilot #2 — DX12/Vulkan ModernAPI deployment invariant

### Task

Prevent DXVK deployment for DX12/Vulkan while retaining detection/reporting.

Tracked by Issue #6.

### Current status

Gemini / Antigravity is implementing the task.

Independent post-implementation review is intentionally deferred until a completed PR exists and Claude is available.

### Known architectural risk

The existing main-branch behavior was observed to allow a path conceptually equivalent to:

```text
DX12 / Vulkan
    -> ModernAPI classification
    -> compatibility decision
    -> DXVK manager
    -> installer
    -> d3d11.dll + dxgi.dll deployment
```

The task is therefore an architectural invariant problem spanning multiple layers rather than merely a UI condition.

### Review priorities

When the PR is ready, verify:

- DX12 detection still works;
- Vulkan detection still works;
- automated installation is blocked;
- direct/manual deployment is blocked;
- queued/pending deployment cannot bypass the rule;
- update paths cannot bypass the rule;
- reapply paths cannot bypass the rule;
- D3D9/D3D10/D3D11 behavior remains intact;
- tests cannot pass while a deployment bypass still exists.

The independent reviewer should inspect test quality before trusting green CI.

### Workflow experiment

The implementation agent was deliberately not given a preselected list of defective files.

The purpose is to observe whether it can trace the cross-component invariant independently.

A planned pre-implementation Claude analysis was not completed because Claude reached its usage limit. That experiment should not be treated as having occurred.

## Workflow Foundation — Issue #7 / PR #8

### Purpose

Create the first repository-native automation layer for the multi-agent workflow without changing application behavior.

### Changes

The foundation branch adds:

- `AGENTS.md` for concise agent operating rules;
- `docs/AI-DEVELOPMENT-WORKFLOW.md` for durable workflow policy;
- this pilot log entry/history;
- `.github/ISSUE_TEMPLATE/ai-task.yml` for structured task contracts;
- `.github/workflows/ai-pr-hygiene.yml` for advisory PR metadata checks;
- `.github/workflows/ai-scope-check.yml` for advisory allowed-path checking;
- `.github/workflows/ai-review-packet.yml` for a compact post-CI review evidence artifact.

### Observations

- GitHub can serve as the state/audit layer without an external orchestrator.
- Read-only PR metadata checks are straightforward to run safely.
- Allowed-path enforcement should remain advisory until several real tasks prove the contract format and matching rules.
- Documentation impact is important enough to appear explicitly in both task and PR contracts.
- Review-packet generation should assemble evidence, not execute reviewed PR code.
- Gemini should not be artificially constrained by a fixed diff budget; task scope and deterministic gates are safer controls.
- The first workflow automation is deliberately non-blocking so we can observe its behavior before making it a merge gate.

### Current limitation

The review-packet workflow uses `workflow_run` and therefore is intended to become operational after the workflow is present on the default branch. PR #8 is the bootstrap vehicle; the packet should not yet be treated as production automation until that activation path is verified.

## Future pilot records

For each future non-trivial pilot, record:

### Task

What was being changed and why?

### Agents

Which systems implemented, reviewed, and arbitrated?

### Evidence

What exact CI/test result was observed?

### Review

What did independent review find?

### Outcome

Merged, revised, rejected, or escalated?

### Workflow lesson

What changed about the development process as a result?

### Documentation impact

Which durable project documents or specifications changed?
