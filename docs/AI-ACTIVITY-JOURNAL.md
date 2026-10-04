# Agent Activity Journal & Session Index

This document establishes the **Mandatory Agent Activity Record Policy** for the DXVK-Companion project and indexes all durable session records.

---

## 1. Activity Record Policy

Every agent engaging in a meaningful work session on this repository must leave a factual, concise activity record. This requirement applies across all modes:
- Implementation and bug fixing
- Test generation and regression verification
- Independent code audit or revision verification
- Architecture and contract definition
- Blocked, aborted, or exploratory sessions

### Recording Principles
1. **Factual and verifiable**: Record actual commands run, files changed, commit SHAs, and CI run links. Do not speculate or claim actions that were not executed.
2. **Concise rationales**: Document *why* decisions were made, not raw conversational transcripts or full tool call listings.
3. **Privacy first**: Never record secrets, auth tokens, personal user home directory paths, or private credentials.
4. **No automated enforcement**: Journal entries are maintained via discipline and workflow convention, not enforced by an automated git hook or CI gate.

### Persistence Paths
- **Standard Write Session**: The agent creates a file in `docs/ai-journal/<YYYY-MM-DD>-<task-slug>.md` and adds a row to the Session Index below.
- **Read-Only / Web Session**: If an agent lacks filesystem write access (e.g., web chat model or review tool), it formats its final report using the Journal Template below so the coordinator or human can commit it to the repository.
- **Blocked / Abandoned Session**: If a session is blocked or fails, record the blocker, starting head, and exact point of failure so subsequent sessions do not repeat the dead end.

---

## 2. Session Record Template

```markdown
# Session Record: [YYYY-MM-DD] — [Task Title]

- **Date / Timestamp**: YYYY-MM-DD HH:MM TZ
- **Agent Role & Model**: [e.g., Gemini 2.5 Pro / Antigravity | ChatGPT o1 / Web | Claude 3.5 Sonnet / Web]
- **Task / Issue**: Issue #[N] — [Title]
- **Starting Head**: [40-character commit SHA]
- **Branch / Worktree**: [branch name]

### Actions Executed
- [Summary of key steps, files touched, commands run]

### Observable Measurements
- Human decision/action interventions: [Count]
- Human status queries: [Count]
- Repeated investigations from missing context: [Count]
- Session blocked / required extra session: [Yes/No]
- Quota / elapsed clock time: not measured

### Outcome & Handoff
- Result: [SUCCESS | CHANGES_REQUIRED | BLOCKED]
- Resulting Head: [Commit SHA or PR #]
- Handoff Target: [Next Role / Model]
```

---

## 3. Session Index

| Date | Task / Issue | Role & Model | Result | Journal Link |
|---|---|---|---|---|
| **2026-10-03** | Issue #20 Post-#19 cleanup | Gemini (Implementer) & ChatGPT (Coord/Verifier) | SUCCESS | [2026-10-03-issue-20-cleanup.md](ai-journal/2026-10-03-issue-20-cleanup.md) |
