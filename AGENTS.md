# Agent Operating Rules

This repository uses AI-assisted development with GitHub as the durable source of truth.

## Before changing code

1. Read the GitHub Issue assigned to the task.
2. Read the relevant project specification and nearby implementation.
3. Treat the Issue acceptance criteria as authoritative for the task.
4. Do not infer missing requirements from an old AI conversation when the repository contains a newer decision.
5. Keep the task isolated from unrelated work.

## Scope

Make the smallest coherent change that satisfies the Issue.

Do not perform unrelated refactoring, formatting churn, dependency additions, project-file changes, or architecture changes unless the task explicitly requires them.

Respect any "Allowed paths" and "Forbidden or out-of-scope changes" in the task Issue.

## Tests and evidence

Add regression coverage for changed behavior.

Prefer tests that exercise real application orchestration rather than only mocks of the path under test.

Run the repository's relevant build/test commands before reporting completion.

A test or CI result reported by another agent is not evidence by itself; identify the exact command, commit, and CI result when verification matters.

## Review

Independent review is intended to challenge the implementation rather than merely confirm that it compiles.

Reviewers should audit tests and required invariants before trusting green CI, then trace alternate production entry points for bypasses.

If a material review finding requires a revision, use a fresh implementation-agent context containing the task contract, current branch, and exact finding.

Avoid repeated AI ping-pong. Escalate unresolved disagreement.

## Documentation and continuity

AI chats are temporary working sessions.

Important decisions, architectural conclusions, and workflow lessons must be written back to the repository.

For significant tasks, update the relevant durable documentation and record the result in docs/AI-PILOT-LOG.md when the work is part of the multi-agent workflow experiment.

See:

- docs/AI-DEVELOPMENT-WORKFLOW.md
- docs/AI-PILOT-LOG.md

## Merge authority

During the workflow experiment, final merge decisions remain human-controlled.

Do not automatically merge, bypass an unresolved review, or silently substitute another reviewer because a preferred model is unavailable.
