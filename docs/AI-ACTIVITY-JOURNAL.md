# Agent Activity Journal & Session Index

This document establishes the **Mandatory Agent Activity Record Policy** for the DXVK-Companion project and indexes durable session records.

---

## 1. Activity Record Policy

Every agent engaging in a meaningful work session on this repository must leave a factual, concise activity record. This requirement applies across all modes:
- Implementation, test generation, and bug fixing
- Verification, independent code audit, and arbitration
- Architecture, contract definition, and triage
- Partial, blocked, exploratory, or no-change outcomes

### Core Recording Rules

1. **Concise by default**: Target 100–200 words. Expand beyond this only when recording essential evidence, reproducer definitions, or critical diagnostics.
2. **Key elements required**:
   - Purpose of the session
   - Decisions made and underlying rationale
   - Evidence identity, test results, and residual limitations
   - Next concrete action and owner
   - Partial or no-change outcomes if stopped early
3. **Checkpointing**: Record a checkpoint at material milestones (e.g. after verifying a reproducer or passing a test suite) and before stopping or switching models.
4. **Accurate attribution**: Attribute each entry strictly to the agent that performed the actions. Do not conflate implementer actions with coordinator or auditor actions.
5. **No guessed model versions**: Record only confirmed agent identities (e.g., `Gemini (Implementer)`, `ChatGPT (Coordinator)`, `Claude (Auditor)`, `Human`).

### Persistence Channels

- **Worktree / Repository Write**: Agents with repository filesystem write access create a dedicated session file in `docs/ai-journal/<YYYY-MM-DD>-<issue>-<agent>-<suffix>.md`. Filenames include role/agent and a unique suffix to prevent accidental collisions.
- **Connector Write (Issue / PR Body)**: Web or review agents with GitHub connector/API write access can persist an attributed `Activity record` section directly in the Issue or PR description (as demonstrated in PR #19 and PR #21).
- **Human Transcription Fallback**: Used only when an agent has neither repository write nor connector write permissions, with durable repository persistence labeled as pending.
- **Opportunistic Indexing**: Do **not** require sessions to update this shared `docs/AI-ACTIVITY-JOURNAL.md` index file when the task contract does not allow it. Issues #12–#18 allow files under `docs/ai-journal/` but fence `docs/AI-ACTIVITY-JOURNAL.md`. Update this index opportunistically during authorized documentation tasks.

---

## 2. Session Record Template

```markdown
# Session Record: [YYYY-MM-DD] — [Task Title]

- **Date / Timestamp**: YYYY-MM-DD HH:MM TZ
- **Agent Role & Model**: [Gemini (Implementer) | ChatGPT (Coordinator) | Claude (Auditor) | Human]
- **Task / Issue**: Issue #[N] — [Title]
- **Starting Head**: [Commit SHA or known base link]
- **Branch / Worktree**: [branch name / worktree path]

### Purpose & Context
- [1-2 sentences on what this session set out to achieve]

### Decisions & Rationale
- [Key design choices or trade-offs made during the session and why]

### Actions Executed
- [Summary of key steps, files touched, commands run]

### Evidence & Limitations
- [Test outputs, CI run IDs, verification results, and what could NOT be verified]

### Observable Measurements
- Human decision/action interventions: [Count]
- Human status queries: [Count]
- Repeated investigations from missing context: [Count]
- Session blocked / required extra session: [Yes/No]
- Quota / elapsed clock time: not measured
- Journal word count: [~Count]

### Outcome & Next Steps
- Result: [SUCCESS | CHANGES_REQUIRED | REVISED | BLOCKED | NO_CHANGE]
- Resulting Head: [Commit SHA or PR #]
- Next Action & Owner: [Concrete next step and responsible role]
```

---

## 3. Session Index

The index is updated opportunistically during authorized documentation tasks:

| Date | Task / Issue | Role & Model | Result | Journal Link |
|---|---|---|---|---|
| **2026-10-03** | Issue #20 Post-#19 cleanup | Gemini (Implementer) | CHANGES_REQUIRED (Revision) | [2026-10-03-issue-20-gemini-session-1.md](ai-journal/2026-10-03-issue-20-gemini-session-1.md) |
| **2026-10-03** | Issue #20 Post-#19 cleanup revision | Gemini (Implementer) | REVISED (Awaiting verification) | [2026-10-03-issue-20-gemini-session-2.md](ai-journal/2026-10-03-issue-20-gemini-session-2.md) |
