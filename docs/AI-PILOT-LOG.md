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

### Current status at latest checkpoint

PR #9 is open against `main`.

Latest verified PR head:
`d9051cab30d5e5bde3d066fa8e04527e5e386f04`

The previous PR head `fb04a0cb3139bc915876a6fd110d8b15b9bed429` failed GitHub Actions while building the Phase A test project; the tests did not run.

At the newest checked head `d9051cab...`, the malformed/duplicated test-file content had been cleaned up. `GraphicsApi.cs` no longer contained the unnecessary JSON string-enum converter or D3D aliases and preserved the existing numeric ordinals while adding DX12/Vulkan values.

**No workflow run was yet associated with the newest head when this checkpoint was recorded.**

Gemini / Antigravity is actively revising the task. Do not treat the newest head as CI-verified until the exact SHA has a successful Build and Test result.

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

### First independent-review outcome

Claude reviewed the first implementation branch `fc5ca58e9ebef34f28ad71a8a6afece60d2333a4` and returned **CHANGES REQUIRED**.

The most important concrete finding was that the installer-level Reapply test could return false before reaching the new unsupported-API guard because the fixture was not a genuinely managed installation.

Additional review priorities included:

- persisted pending Install/Update/Reapply execution;
- preservation of Disable/Restore behavior after API reclassification;
- positive controls for supported APIs;
- stronger mixed-module classifier coverage;
- unnecessary enum aliases / serialization changes;
- exact PR and CI evidence;
- mixed-executable/shared-directory behavior as a design uncertainty;
- runtime/dynamic API loading as residual detection uncertainty.

The planned pre-implementation Claude review did not occur because Claude's usage limit was reached. It must not be described as having happened.

### Gemini revision requirements

The revision request required Gemini to:

1. reconstruct the Phase A compatibility test file cleanly;
2. preserve meaningful positive and negative tests rather than weakening them;
3. remove unnecessary enum serialization/alias changes;
4. evaluate stale pending-action behavior against the specification;
5. keep the compatibility boundary centralized;
6. verify DX9/DX10/DX11 positive behavior;
7. run focused and normal Phase A tests;
8. update the PR with exact final-head evidence;
9. use a fresh implementation context.

### Current reviewer rule

Do **not** consume Claude quota for another review until PR #9 has a corrected final head and successful Build and Test CI.

The intended sequence remains:

```text
Gemini revision
    -> exact-head CI verification
    -> Claude final independent review
    -> one revision cycle only if material
    -> CI
    -> Claude re-review
    -> human merge decision
```

## Workflow Foundation — Issue #7 / PR #8

### Purpose

Create the first repository-native automation layer for the multi-agent workflow without changing application behavior.

### Result

PR #8 was independently verified as **merged** into `main`.

Merge commit:
`3a66376446d847434542410a2a3626a070639aa8`

The foundation branch adds:

- `AGENTS.md` for concise agent operating rules;
- `docs/AI-DEVELOPMENT-WORKFLOW.md` for durable workflow policy;
- this pilot log/history;
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
- A failed test build is valuable workflow evidence: the deterministic CI layer caught a malformed AI-generated test revision before an independent reviewer was asked to spend scarce quota on it.

### Current limitation

The review-packet workflow uses `workflow_run` and therefore is intended to become operational after the workflow is present on the default branch. PR #8 was the bootstrap vehicle; actual packet activation should be verified rather than assumed.

## Durable workflow decisions

### GitHub is the memory layer

AI chat sessions are temporary. Important requirements, decisions, evidence, and pilot lessons must be recorded in GitHub.

### Evidence beats agent reports

A model saying “tests pass” is a claim. The authoritative verification is the exact command/result and, where relevant, the exact GitHub Actions run for the exact commit.

### Independent review is adversarial

Claude's role is not to confirm another agent's reasoning. It should reconstruct the contract, inspect tests first, and search for bypasses.

### Abundant implementation capacity should be used

Gemini/Antigravity can do substantial task-relevant work. There is no arbitrary diff-size budget. Use scope, tests, CI, independent review, and human merge authority to control risk.

### Fresh contexts matter

When a material review finding requires revision, use a fresh Gemini context containing the Issue, current PR/branch, and exact finding rather than continuing from the original implementation conversation.

### Avoid AI ping-pong

A normal policy is one focused revision after a material finding. Persistent disagreement should be escalated rather than iterated indefinitely.

## Verified GitHub state at handoff

- Repository: `Tiflit/DXVK-Companion`
- Default branch: `main`
- Verified main merge commit: `3a66376446d847434542410a2a3626a070639aa8`
- PR #8: merged
- PR #4: still open
- Issue #6: open
- PR #9: open
- PR #9 latest verified head: `d9051cab30d5e5bde3d066fa8e04527e5e386f04`
- PR #9 Build and Test run #70 failed at application compilation with two CS0103 `Logger` errors in `DxvkManager.cs`; tests did not run
- Gemini/Antigravity: actively revising PR #9
- Human merge authority: unchanged

## Durable checkpoint recorded on Issue #6

Three comments were added to Issue #6 during the final handoff review.

The first recorded the prior PR #9 head `fb04a0cb...` and its failed Build and Test result.

The second recorded the newer head `d9051cab...`, the cleanup of the malformed test file, the removal of the unnecessary enum JSON converter/aliases, and the absence of CI at that moment.

The third recorded the subsequent Build and Test run #70 failure: the application build reports two CS0103 `Logger` errors in `DxvkManager.cs` before tests can run.

These comments exist so a new AI session can recover the current Pilot #2 state without relying on this conversation.

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
