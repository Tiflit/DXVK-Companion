# Handoff Snapshot Reference: Markdown Formatter & CLI

> **Source Reference**: Inspected from `scripts/ai-workflow/generate_handoff.py` at pinned baseline commit `19a59d6d90bb69a84afb4c12aaf312c192f866e4` (static inspection only; pure documentation without helper execution).

---

## Document Outline

- **Section 1**: CLI Synopsis, Parameters & Invocation (Documented below)
- **Section 2**: Markdown Output Schema, Word-Budget Truncation & Evidence Limitations (Planned for subsequent stage)

---

## Section 1: CLI Synopsis & Arguments

### 1.1 Synopsis

`generate_handoff.py` generates an evidence-bound, read-only handoff snapshot summarizing current pull request state, verification evidence, decision governance, and next ownership.

```text
python scripts/ai-workflow/generate_handoff.py --pr <PR_NUMBER> [OPTIONS]
```

### 1.2 CLI Parameters

| Flag | Type | Requirement | Default | Description |
|---|---|---|---|---|
| `--pr` | `int` | **Required** | None | Target Pull Request number to inspect. Must be a positive integer. |
| `--issue` | `int` | Optional | `None` | Primary task issue number. If omitted, extracted from PR body contract if present. |
| `--repo` | `str` | Optional | `$GITHUB_REPOSITORY` or `Tiflit/DXVK-Companion` | Repository identity in `owner/repo` format. Validated against repository pattern. |
| `--token` | `str` | Optional | Environment / `gh auth token` | GitHub API access token (`GITHUB_TOKEN` or `GH_TOKEN`). Falls back to `gh` CLI token. |
| `--worktree` | `Path` | Optional | `None` | Path to local workspace worktree. Used to inspect local Git branch and head identity. |
| `--output` | `Path` | Optional | `stdout` | File path destination for output snapshot. Parent directories are created automatically. |
| `--json` | flag | Optional | `False` | Outputs structured JSON representation instead of standard formatted Markdown. |
| `--max-words` | `int` | Optional | `300` | Word limit for Markdown snapshot output (excluding URLs). Default defined by `MAX_HANDOFF_WORDS`. |

### 1.3 Argument Validation & Environment Discovery

- **Repository Validation**: Validates `--repo` matching `^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$`. Invalid formats abort execution.
- **Token Discovery**: If `--token` is not explicitly passed, inspects `GITHUB_TOKEN` and `GH_TOKEN` environment variables, then attempts fallback to `gh auth token` with a 3-second timeout.
- **Local Worktree Inspection**: When `--worktree` is provided, executes non-destructive read-only queries (`git rev-parse`, `git status --porcelain`) within the specified path to capture local branch name, local HEAD commit SHA, and clean/dirty status.

---

## Section 2: Markdown Output Schema, Word-Budget & Evidence Limits (Planned)

*(This section is planned for the subsequent recovery stage. It will detail:)*
- **Schema Section 1**: Verified GitHub Revisions & Task State (Contract issue, PR head, base, default branch sync, local workspace).
- **Schema Section 2**: CI Verification & Evidence Provenance (Triggering run, tested checkout SHA, synthetic merge ref, TRX test totals, attributed PR body review records).
- **Schema Section 3**: Decision Prerequisites & Governance (Decision required, governance status, human approval source).
- **Schema Section 4**: Next Ownership & Action (Assigned owner, exact action, remaining uncertainties).
- **Word-Budget Truncation**: Bounded Markdown length algorithm (`count_words_excluding_urls`, budget threshold, truncation notice).
- **Evidence Boundaries**: Distinction between verifiable CI logs, PR body annotations, and unverified assumptions.
