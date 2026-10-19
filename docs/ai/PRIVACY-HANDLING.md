# AI Workflow Privacy and Recovery Handling Guide

This document defines privacy boundaries, recovery data management, repository hygiene, and security incident response procedures for AI-assisted workflows in DXVK-Companion.

---

## 1. Privacy Boundaries & Recovery Storage

Local recovery backups are critical for preserving baseline states and recovering from unexpected concurrent modifications or failed operations. However, uncurated recovery data must never be exposed to public version control or external publication channels.

### External Backup Destination Mandate
- **Mandatory External Placement:** For any mutation operation (`--write`) on a Pull Request or Issue that performs a write requiring a pre-write recovery backup, the workflow helper (`scripts/ai-workflow/update_pr_body.py`) strictly mandates an explicit `--backup-dir` destination.
- **Fail-Closed Worktree Boundary Enforcement:** The helper discovers all local Git worktrees (the main checkout and any linked worktrees registered with `git worktree list --porcelain` and `git rev-parse --show-toplevel`). If the specified `--backup-dir` is located within, or equal to, any local checkout or linked worktree, the operation refuses immediately with an error before any filesystem directory is created and before any GitHub API mutation (`PATCH`) is executed.
- **Path Alias Resolution:** Path validation resolves symlinks, directory junctions, relative references (`.` and `..`), and operating system path aliases (such as 8.3 short names and case variations on Windows) prior to boundary evaluation.
- **Boundary Failure:** If local Git repository boundaries cannot be determined (e.g. Git executable failure, corrupted metadata, or detached execution outside a Git context), the helper fails closed and refuses writes.
- **No Implicit Fallback:** The helper never silently falls back into the current working directory (`CWD`) or `.ai-review-backups/` within the repository.
- **Exempt Operations:** Side-effect-free preview flows (`--preview` or omitting `--write`) and verified idempotent no-op flows (where the remote entity already contains the identical record and zero writes are committed) remain side-effect-free and do not require `--backup-dir`.

### Exact Raw Snapshots & Collision Preservation
- **Exact Raw Content:** Pre-write recovery backups are written as target-qualified, exact byte-for-byte UTF-8 representations of the remote state immediately prior to mutation.
- **Non-Overwriting Exclusive Creation:** Backup files are created using atomic exclusive creation (`open(..., 'x')`). If a backup file with the target-qualified timestamp name already exists, the helper refuses to overwrite it, preserves the existing file intact (collision preservation), and aborts the operation before any API mutation.
- **No Historical Mutation:** The helper never scans, moves, renames, or deletes historical backup files or expands filesystem exploration outside the explicitly provided destination.

---

## 2. Repository Hygiene & Ignore Realities

Targeted exclusions are declared in `.gitignore` to prevent incidental accumulation of build artifacts, caches, and scratch recovery files in local checkouts.

### Targeted Exclusions vs. Blanket Ignores
The repository `.gitignore` provides targeted exclusions:
- `.ai-review-backups/` — legacy or accidental local recovery backups
- `.ai-cache/` — local AI workflow temporary cache and scratch data
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
- **Token and Secret Scanners:** Detect GitHub personal access tokens (`ghp_`, `github_pat_`, etc.) and bearer authorization tokens.
- **Fail-Closed Pre-Write Checks:** If sensitive paths or credentials are detected in proposed PR bodies, candidate Issue append records, or remote targets, the helper immediately aborts before output or remote dispatch.
- **Diagnostic Sanitization:** All diff previews, error diagnostics, and backup display confirmations sanitize sensitive personal paths to `[REDACTED_PATH]` and tokens to `[REDACTED_TOKEN]`. Backup contents and personal usernames are never echoed to public logs or output streams.

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
