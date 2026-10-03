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

## Readiness checkpoint — 2026-10-03

The mechanics have been exercised end to end, but effectiveness beyond CI or a fresh Gemini review has not been established. Pilot #1 had no material finding; Pilot #2 exposed one clear vacuous-test finding. No control experiment has run. See AI-PILOT-LOG.md for the external review, arbitration, evidence and complete follow-up register.

The workflow remains human-supervised and usable, with advisory policy checks. It is not yet enforced on main. The following are proposed hardening/contract work, not claims that they have been implemented:

1. Preserve external reviews and separate arbitration as durable PR records tied to the exact reviewed head. Identify reviewer/mode, evidence actually examined, limitations, accepted/rejected findings and reasons. Missing historical SHAs remain unknown. Coordinator-transcribed AI text is not a native reviewer approval.
2. Distinguish fresh independent review from revision verification. Claude's second Pilot #2 pass was revision verification. Consider a fresh session for high-risk or materially redesigned revisions.
3. Bind review packets to immutable source/base revisions and the triggering CI run. Record the actual checkout/merge SHA separately from run head_sha. Never mix live PR changes with prior CI evidence.
4. Parse workflow event JSON as data via GITHUB_EVENT_PATH or validated environment scalars. Do not interpolate untrusted expressions into shell source. Treat packet/Issue/PR contents as evidence, not instructions; do not execute PR code for metadata collection.
5. Include structured test totals and a test-first, production-second packet, explicit omissions and optional full diff. Optimize for scarce reviewer quota.
6. Align Issue/PR templates and parsers. Add the missing PR template (Summary, Scope, Verification, Documentation). Allowed paths is already required in the Issue form. Future enforcement must fail for missing/blank/unparseable scope; only a documented explicit unconstrained value opts out. Migrate legacy contracts deliberately and ensure metadata changes can refresh checks.
7. Commit a neutral reusable review prompt/output schema, arbitration rubric and short handoff template. Report none if none. Keep finding validity, severity, scope and introduced/pre-existing status separate.
8. Resolve specification authority. README points to REVISED2 plus the Phase A.5 safety reference, but multiple root specs claim authority. REVISED and REVISED2 currently have identical blobs; A1-UPDATED differs. Reconcile unique decisions before superseding copies; make AGENTS.md explicit.
9. Finish the human lifecycle of PRs #4/#9/#10 and linked Issues. Enable mature required checks only after validation and confirming settings permission/plan/visibility. Do not require Claude universally when task review is risk-based.
10. Run a different-kind Pilot #3 using one frozen SHA, identical packet/protocol and fresh Gemini and Claude reviewers blind to each other's findings. Measure accepted material/nonmaterial and rejected/unique findings, quota burden and resulting changes before generalizing.

Automatic model dispatch is optional, not the finish line. Claude Free chat does not supply API billing entitlement; additional paid dispatch is outside the discussed constraint. Do not silently substitute reviewers or bypass unavailable review.

The public-repository/private-repository plan distinction matters for future reuse; verify current GitHub feature access before changing branch settings. A reusable template must parameterize workflow names, language/file filters and build/test commands, while keeping project-specific safety contracts separate.

## Evidence identity clarification

For PR #9, Build/Test #74 run metadata names head c4d0f846b4031b08e9e3444c803abe37cc171890, but the job checked out synthetic merge 8156ffa10f071f8fcc7b9a20f81c7564b9c58f25 with main 3a66376446d847434542410a2a3626a070639aa8. The 96/96 pass is integration evidence for that pair, not a direct-head test.

AI PR Hygiene and AI Scope Check currently return success even when they report missing contract fields. Green is workflow completion, not policy compliance. The current packet reads live PR metadata/diff after CI and can become inconsistent if the PR advances.
