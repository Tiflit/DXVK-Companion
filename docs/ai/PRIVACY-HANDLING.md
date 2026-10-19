# AI Workflow Privacy and Recovery Handling Guide

This document defines privacy boundaries, recovery data management, repository hygiene, and security incident response procedures for AI-assisted workflows in DXVK-Companion.

---

## 1. Privacy Boundaries & Recovery Storage

Local recovery backups are critical for preserving baseline states and recovering from unexpected concurrent modifications or failed operations. However, uncurated recovery data must never be exposed to public version control or external publication channels.

### External Backup Destination Mandate
- **Mandatory External Placement:** For any mutation operation (`--write`) on a Pull Request or Issue that performs a write requiring a pre-write recovery backup, the workflow helper (`scripts/ai-workflow/update_pr_body.py`) strictly mandates an explicit `--backup-dir` destination.
- **Fail-Closed Repository Boundary Enforcement:** The helper discovers all local Git worktrees of the caller repository (the main checkout and linked worktrees registered with `git worktree list --porcelain` and `git rev-parse --show-toplevel`). Furthermore, the helper probes whether the destination or its nearest existing directory ancestor is located inside *any* locally establishable Git repository or worktree (including independent repositories, `.git` directories, and non-existent destination suffixes within checkouts) using `git -C <nearest_ancestor> rev-parse --is-inside-work-tree --is-inside-git-dir`. If the specified `--backup-dir` is located within, or equal to, any checkout or worktree, the operation refuses immediately before any filesystem directory is created and before any GitHub API mutation (`PATCH`) is executed.
- **Strict Path Anchoring & Alias Resolution:** Path validation explicitly anchors relative paths against `cwd` (or `Path.cwd()`), strictly resolves existing ancestor symlinks and directory junctions with `resolve(strict=True)` (failing closed with `SecurityValidationError("Backup destination path resolution failed.")` on loops, broken symlinks, or access denials), appends non-existent suffixes, and returns/uses the single validated absolute `Path` for downstream `mkdir` and pre-write file creation. Path characters are preserved losslessly when parsing `git worktree list --porcelain`.
- **Boundary Failure:** The helper distinguishes established non-repository absence (exit code 128 with "not a git repository") from query failures, timeouts, corrupted metadata, or detached execution outside a Git context, failing closed with a category-only error on any uncertainty.
- **No Implicit Fallback:** The helper never silently falls back into the current working directory (`CWD`) or `.ai-review-backups/` within the repository.
- **Exempt Operations:** Side-effect-free preview flows (`--preview` or omitting `--write`) and verified idempotent no-op flows (where the remote entity already contains the identical record and zero writes are committed) remain side-effect-free and do not require `--backup-dir`.

### Exact Raw Snapshots & Collision Preservation
- **Exact Raw Content:** Pre-write recovery backups are written as target-qualified, exact byte-for-byte UTF-8 representations of the remote state immediately prior to mutation.
- **Non-Overwriting Exclusive Creation:** Backup files are created using atomic exclusive creation (`open(..., 'x', encoding='utf-8', newline='')`). If a backup file with the target-qualified timestamp name already exists, the helper refuses to overwrite it, preserves the existing file intact (collision preservation), and aborts the operation before any API mutation.
- **No Historical Mutation:** The helper never scans, moves, renames, or deletes historical backup files or expands filesystem exploration outside the explicitly provided destination.

---

## 2. Repository Hygiene & Ignore Realities

Targeted exclusions are declared in `.gitignore` to prevent incidental accumulation of build artifacts, caches, and scratch recovery files in local checkouts.

### Targeted Exclusions vs. Blanket Ignores
The repository `.gitignore` provides targeted exclusions:
- `.ai-review-backups/` — legacy or accidental local recovery backups
- `.ai-cache/` — local AI workflow temporary cache and scratch data
- `artifacts/` — root build provenance and publish output directories (`artifacts/provenance/`, `artifacts/publish/`)
- `bin/`, `obj/`, `TestResults/` — compilation and test output artifacts
- `__pycache__/`, `*.py[cod]` — Python bytecode caches
- `.env`, `.env.*` (with `!.env.example` unignored) — local environment secrets

> [!WARNING]
> **Ignore Rules Are Not Security Guarantees:**
> 1. **Ignores Do Not Untrack Existing Files:** Adding a pattern to `.gitignore` does not remove or untrack files that are already tracked in Git history or present in the Git index.
> 2. **No Publisher Policing:** `.gitignore` and `update_pr_body.py` govern only their own operations; they do not police other tools, scripts, Git GUI clients, or manual commits.
> 3. **No Blanket Ignores:** Blanket patterns such as `*.json`, `*.zip`, `*.dll`, or `*.png` are strictly forbidden because they hide legitimate versioned project source, fixtures, and documentation assets.

---

## 3. Privacy Detection & Redaction

The workflow helper incorporates fail-closed scanners and redaction filters:
- **Home Directory Scanners:** Detect Windows personal directory paths (`C:\Users\<user>\...` or `C:/Users/...`) and POSIX personal directory paths (`/home/<user>/...` or `/Users/<user>/...`).
- **Fail-Closed Pre-Write Checks:** Proposed PR bodies, candidate Issue append records, and final mutation payloads are strictly scanned for sensitive personal paths and credentials before remote dispatch; if sensitive data is detected in the candidate content, the helper immediately aborts. When previewing updates against an existing remote target that contains sensitive data (such as a dirty remote PR body), the helper does not abort clean-replacement candidates, but instead redacts sensitive paths and tokens line-by-line in the diff preview (`sanitize_diff_line`) so contaminated remote lines appear as `-[REDACTED_PATH]` without leaking private data.
- **Diagnostic Sanitization & Fixed Category Errors:** Error diagnostics use fixed category-only error strings without echoing raw Git stderr, exception text, or environment paths. Backup confirmation displays use a fixed basename format (`[REDACTED_PATH]/<filename>`) to prevent leaking personal paths or usernames with spaces (e.g. `C:\Users\Example Person\`).
- **Diff Preview Redaction:** Unified-diff previews are sanitized line-by-line (`sanitize_diff_line`) so personal paths or tokens present in dirty remote bodies are safely redacted without corrupting unified-diff structural indicators (`+`, `-`, ` `).

---

## 4. Incident Response Procedure

If private recovery material, personal paths, or credentials are accidentally committed, transmitted, or detected in public records:

1. **Stop Publication Immediately:**
   - Immediately pause and halt all pending pushes, PR updates, Issue comments, and automated workflows.
   - Do not attempt additional automated mutations or write attempts while the scope of disclosure is assessed.

2. **Report a Minimally Revealing Concern:**
   - Report the incident using generic, non-sensitive identifiers (e.g. "Credential exposure detected in commit `<sha>`" or "Personal path detected in PR #`<number>`").
   - **Never echo or quote** the exposed token, secret, password, or sensitive path in GitHub Issues, PR discussions, activity journals, or agent conversation logs.

3. **Coordinate Human-Approved Revocation & Rotation:**
   - If a secret or token (e.g. GitHub PAT) was exposed, immediately coordinate with the human owner to revoke the token in the provider console (e.g. GitHub Developer Settings) and generate a replacement.
   - Token revocation is the primary and most reliable containment action.

4. **No Automatic Deletion or History Rewriting:**
   - Autonomous agents must **never** execute force pushes (`git push --force`), Git history rewrites (`git filter-repo`, `git filter-branch`, `git reset --hard` on shared branches), or automatic deletion of repository history.
   - History rewriting requires explicit human authorization, careful coordination with collaborators, and awareness that public forks or mirrors may already have fetched the revision.

5. **Acknowledge Containment Limits:**
   - Once data has been pushed to a remote repository or published to an API, external mirrors, caches, Webhooks, forks, and local clones may retain copies.
   - Do not provide false guarantees that data has been completely purged; focus on rapid credential revocation, minimal revelation, and authorized human remediation.
