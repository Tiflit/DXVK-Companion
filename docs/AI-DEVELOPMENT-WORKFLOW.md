# AI-Assisted Development Workflow

## Purpose

DXVK-Companion is being used to develop and validate a practical multi-agent software-development workflow built around GitHub.

The workflow is intentionally conservative. AI systems may implement, review, and reason about changes, but the repository remains the durable source of truth and a human retains final merge authority during the experimental phase.

## Durable-memory rule

AI chat sessions are temporary working contexts.

A conversation may end because of:

- context-window limits;
- subscription limits;
- a model session reset;
- a tool failure;
- switching from one model to another;
- a long pause between development sessions.

Therefore:

> Important project knowledge must not exist only in an AI conversation.

Durable information belongs in GitHub:

- task requirements and acceptance criteria -> GitHub Issue;
- implementation and verification -> Pull Request and commits;
- architectural decisions -> project documentation/specification;
- workflow decisions and pilot lessons -> this document and `AI-PILOT-LOG.md`.

AI conversations are working sessions and communication channels, not the authoritative project memory.

## Current model roles

### Gemini / Antigravity

Primary implementation and heavy-lifting agent.

Gemini has the most generous practical capacity in the current setup, including a separate Antigravity usage path. Use that capacity deliberately rather than trying to keep implementation changes artificially small.

Use for:

- repository exploration;
- cross-component tracing;
- implementation;
- broad but task-relevant refactoring;
- regression tests and test generation;
- local iteration;
- build/test diagnosis;
- focused revisions after review findings.

A large task should be decomposed by scope and acceptance criteria, not by an arbitrary token/diff budget. Gemini can do substantial repository work when the task genuinely requires it, while CI, scope checks, and independent review provide the safety boundaries.

The workflow should optimize for completed useful work rather than equalizing model usage across agents.

A revision should normally use a fresh Gemini/Antigravity session rather than continuing the original implementation conversation.

### Claude

Independent adversarial reviewer.

Claude has substantially tighter usage limits in the current subscription, so its quota is reserved for changes where independent review provides meaningful value.

Review emphasis:

1. task contract;
2. tests and whether they actually prove the required invariants;
3. bypass/alternate execution paths;
4. production implementation;
5. regression risk;
6. scope.

A green CI result is evidence, not proof that the implementation satisfies the intended invariant.

### ChatGPT

Architecture and reasoning layer.

Use for:

- defining or refining task contracts;
- tracing architectural boundaries;
- analyzing ambiguous requirements;
- interpreting independent review findings;
- arbitration when agents disagree;
- designing future workflow automation.

ChatGPT should not be inserted into every routine implementation loop.

### GitHub Actions

Deterministic validation layer.

Prefer deterministic checks for questions that can be answered by:

- compilation;
- automated tests;
- repository structure;
- allowed-path rules;
- required PR metadata;
- other mechanically verifiable policies.

Do not spend model quota asking an LLM to verify something the repository can verify itself.

### Human

Final authority.

During the experimental phase:

- merges remain human-controlled;
- unresolved review disagreements are escalated;
- automation must not silently bypass unavailable reviewers.

## Standard task lifecycle

```text
GitHub Issue
    |
    v
Gemini / Antigravity implementation
    |
    v
Pull Request
    |
    +--> deterministic CI
    +--> scope / hygiene checks
    |
    v
Independent review when required
    |
    +--> PASS -----------> Human merge
    |
    +--> material finding
              |
              v
       Fresh Gemini context
              |
              v
          CI / tests
              |
              v
       Independent re-review
              |
              v
          Human merge
```

There should be no open-ended AI ping-pong.

The normal policy is at most one focused revision cycle after a material independent-review finding. Persistent disagreement or uncertainty becomes an explicit escalation.

## Risk-based use of independent review

### Low-risk task

```text
Gemini -> CI -> human
```

### Normal task

```text
Gemini -> CI -> Claude -> human
```

### High-risk or architectural task

```text
ChatGPT architecture
        |
        v
Gemini -> CI -> Claude
        |
        +--> disagreement/ambiguity -> ChatGPT + human
```

Claude does not need to review every trivial change.

The task Issue should make the expected review level explicit.

## Task-contract rules

A non-trivial AI task should have a GitHub Issue before implementation begins.

The Issue should contain, at minimum:

- objective;
- acceptance criteria;
- scope;
- allowed paths where practical;
- forbidden or explicitly out-of-scope changes where useful;
- required tests;
- expected review level;
- documentation impact.

The Issue is authoritative for requirements.

A PR may summarize the contract, but should link back to the Issue rather than creating a second independent specification.

## Branch isolation

Implementation work must be isolated to the task being performed.

A task branch should normally start from the intended base branch/commit and avoid unrelated local changes.

If an existing development branch contains unrelated work, create an isolated task branch before implementation or review.

This is particularly important for independent review: a reviewer must be able to attribute changed behavior to the task being evaluated.

## Review protocol

Independent review should be test-first rather than diff-first.

The reviewer should ask:

> Assuming the implementation is subtly defective, can the new tests still pass?

For each important invariant:

- identify the expected invariant;
- inspect the test that supposedly protects it;
- determine whether the test exercises real application orchestration;
- look for mock-only or otherwise bypassable tests;
- trace alternate production entry points;
- then inspect implementation details.

Review should concentrate on correctness, safety, architectural invariants, and scope rather than stylistic preferences.

## Evidence discipline

Claims about validation must be tied to actual evidence.

A PR should identify:

- exact head commit;
- tests/commands run;
- relevant CI run;
- test result summary;
- remaining uncertainty.

Do not describe a test as passing merely because an AI agent reported that it passed. When important, independently inspect the GitHub Actions result.

## Documentation lifecycle

A task is not fully learned from when the implementation is finished.

For significant work:

```text
Task -> implementation -> CI/review -> decision -> durable documentation
```

Documentation should capture conclusions, not entire chat transcripts.

Useful durable records include:

- architectural decisions;
- workflow changes;
- pilot observations;
- review lessons;
- known limitations;
- decisions that future agents must not accidentally reopen.

## Automation strategy

The repository should evolve toward GitHub-native orchestration rather than an external multi-agent server.

Preferred layers:

1. structured Issue contracts;
2. deterministic PR hygiene/scope checks;
3. deterministic CI;
4. compact review-packet generation;
5. explicit review states;
6. branch/ruleset enforcement;
7. model/API automation only after cost, quota, and security are verified.

Avoid duplicating authoritative state into files such as `.github/current_task.json` when the same information already belongs in the GitHub Issue.

## Current experimental safeguards

The following remain intentionally manual during the pilot phase:

- choosing when Claude review is required;
- triaging Claude findings;
- deciding whether a finding is material;
- initiating the one allowed revision;
- arbitration of disagreement;
- final merge.

Automation should first make these decisions easier to audit, not silently make them for the human.

## Security principles

Repository automation must treat pull-request content and artifacts as potentially untrusted input.

Prefer read-only workflows that:

- do not execute pull-request code merely to collect review metadata;
- do not expose repository secrets to untrusted changes;
- avoid elevated workflows unless their security model is explicitly understood.

The review-packet generator should assemble evidence rather than execute arbitrary PR content.

## Current next steps

The current pilots are:

- Pilot #1: `CompanionVersion.IsOutdatedComparedTo` (#5 / PR #4).
- Pilot #2: Prevent DXVK deployment for DX12/Vulkan ModernAPI paths (#6).

Pilot #2 should be completed before adding more sophisticated automation.

The next likely automation steps are:

- deterministic allowed-path scope checking;
- compact review-packet generation;
- explicit workflow status conventions;
- eventual branch/ruleset enforcement.

Each should be validated against real pilot experience before becoming mandatory.
