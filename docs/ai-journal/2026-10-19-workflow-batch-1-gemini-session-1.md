# AI Engineering Journal: Workflow Backup Isolation and Repository Hygiene

**Date**: 2026-10-19  
**Session**: Gemini Session 1  
**Primary Issue**: [#183](https://github.com/Tiflit/DXVK-Companion/issues/183)  
**Base Revision**: `2349ef2460c7b27c6d7a7702c9f8dd187ecbd271` (`origin/main`)  
**Policy Identity**: `f3e9974371cb08d1dd99192bd314a135010594ed`  

---

### 1. Objective & Task Scope

Implement Issue #183 (Batch 1: Keep raw backups and generated output outside publications) under the refreshed human relay:
1. **Targeted Repository Exclusions in `.gitignore`**: Add targeted exclusions for `.ai-review-backups/`, `.ai-cache/`, compilation output (`bin/`, `obj/`, `TestResults/`), Python bytecode (`__pycache__/`, `*.pyc`), and local environment configuration (`.env`, `.env.*` with `!.env.example` unignored). Avoid blanket ignores (`*.json`, `*.zip`, `*.dll`, `*.png`) so that versioned source files and test fixtures remain tracked and visible.
2. **Mandatory External `--backup-dir` for Mutation Writes**: In `scripts/ai-workflow/update_pr_body.py`, require an explicit `--backup-dir` for real PR/Issue writes that require a pre-write recovery backup.
3. **Fail-Closed Worktree Boundary Enforcement**: Refuse missing or unsafe backup destinations prior to filesystem creation or GitHub API `PATCH`. Discover all local Git worktrees (main checkout and linked worktrees via `git worktree list --porcelain` and `git rev-parse --show-toplevel`). Resolve path aliases, symlinks, junctions, relative paths (`..`), and Windows case variations. Fail closed if the Git boundary cannot be established. Never silently fall back into `CWD` or `.ai-review-backups/`.
4. **Side-Effect-Free Preview and No-Op Exemption**: Ensure `--preview` and verified idempotent no-op flows (where remote state already contains identical content and zero writes are committed) remain side-effect-free and do not require `--backup-dir`.
5. **Preservation of Core Protections**: Preserve target-qualified exact raw byte pre-write recovery backups, non-overwriting exclusive file creation (`open(..., 'x')`), collision refusal, expected-base-hash checks, pre-write concurrency checks, and readback verification. Sanitize diagnostic messages, diffs, and confirmation displays so no personal paths or tokens are leaked. Never scan, move, or delete historical backups.
6. **Privacy and Incident Response Documentation**: Author `docs/ai/PRIVACY-HANDLING.md` documenting recovery privacy outside checkouts, ignore realities, and incident procedures (stop publication, report minimally revealing concern, coordinate credential revocation; no automated deletion or history rewrites).
7. **Regression and Hygiene Testing**: Provide thorough tests in `tests/ai-workflow/test_update_pr_body.py` and `tests/ai-workflow/test_repository_hygiene.py` using synthetic Git repositories.

---

### 2. Implementation Details

1. **`.gitignore`**:
   - Declared targeted rules for build output (`bin/`, `obj/`, `TestResults/`, `**/bin/`, etc.), Python caches (`__pycache__/`, `*.py[cod]`, `.pytest_cache/`), workflow cache and recovery backups (`.ai-review-backups/`, `.ai-cache/`), local secrets (`.env`, `.env.*`, `!.env.example`), and patch artifacts (`*.orig`, `*.rej`).
   - Verified via `git check-ignore` that source files (`src/Main.cs`, `update_pr_body.py`) and fixtures (`.json`, `.zip`, `.dll`, `.png`) are not ignored.

2. **`scripts/ai-workflow/update_pr_body.py`**:
   - `find_git_worktree_roots(cwd: Optional[Path]) -> List[Path]`: Queries `git worktree list --porcelain` and `git rev-parse --show-toplevel`. Parses all registered worktree paths and fails closed (`SecurityValidationError`) if Git execution fails or returns non-zero.
   - `is_path_inside_or_equal(target: Path, root: Path) -> bool`: Resolves canonical paths (`os.path.realpath` and `Path.resolve`) and compares path segments (`.parts`) with case normalization on Windows to prevent prefix collision or alias bypass.
   - `validate_backup_dir(backup_dir: Optional[Union[str, Path]], cwd: Optional[Path]) -> Path`: Enforces non-empty string/Path, verifies path is not an existing file, and verifies destination is strictly outside all discovered Git worktree roots.
   - `create_prewrite_backup(...)`: Hardened to call `validate_backup_dir` (removing default fallback into `CWD`), create directory only after validation, and write with `open(backup_file, "x", encoding="utf-8")` to refuse overwrite and preserve collisions with `FileExistsError`.
   - `main()`:
     - Updated `--backup-dir` argument help text.
     - In Issue write mode: after confirming not an idempotent no-op, requires `--backup-dir`, validates worktree boundary before directory creation or PATCH, and passes validated directory to `create_prewrite_backup`.
     - In PR write mode: requires `--backup-dir`, validates worktree boundary before directory creation or PATCH, and passes validated directory to `create_prewrite_backup`.
     - Updated preview guidance strings to include `--backup-dir <external-backup-dir>`.

3. **`docs/ai/PRIVACY-HANDLING.md`**:
   - Authored comprehensive guide detailing privacy boundaries, external backup requirements, `.gitignore` limitations, leak prevention, and the 5-step incident response procedure.

4. **`tests/ai-workflow/test_update_pr_body.py`**:
   - Updated `test_privacy_scan_on_backup_path_redacts_in_display` to pass `--backup-dir`.
   - Added Section 13 with 19 new unit and integration test methods covering:
     - `validate_backup_dir` rejection of `None`, empty string, whitespace.
     - Rejection of paths inside main worktree, linked worktrees, worktree root itself.
     - Alias and case resolution.
     - Rejection of existing files.
     - Fail-closed behavior on Git command failure.
     - Exact byte writing and target-qualified naming (`pr_31_...` and `issue_42_...`).
     - Non-overwriting creation and collision refusal (`FileExistsError`).
     - CLI write refusal when `--backup-dir` is missing (Issue and PR mode) before PATCH.
     - CLI write refusal when `--backup-dir` is inside worktree (Issue and PR mode) before PATCH.
     - Side-effect-free preview and idempotent no-op execution without `--backup-dir`.

5. **`tests/ai-workflow/test_repository_hygiene.py`**:
   - Authored synthetic repository test suite verifying:
     - Targeted exclusions for `.ai-review-backups/`, `.ai-cache/`, `bin/`, `obj/`, `TestResults/`, Python bytecode, `.env`.
     - Whitelist behavior of `!.env.example`.
     - Verification that source code and fixtures (`.json`, `.zip`, `.dll`, `.png`) are not blanket-ignored.
     - Linked worktree boundary discovery and rejection using synthetic `git worktree add`.
     - Alias resolution and path normalization.

---

### 3. Verification & Observability

- **Local host execution**: `NOT RUN (pending human authorization)` under `AGENTS.md`. No host builds, tests, or application processes were executed.
- **Git and metadata inspection**:
  - `git check-ignore -v` tested against targeted ignore paths and fixture paths.
  - `git status` verified strictly clean on declared allowed paths.
- **Cloud CI execution**:
  - First run on PR #189:
    - `AI task scope` ([Run 39330108957](https://github.com/Tiflit/DXVK-Companion/actions/runs/39330108957)): PASS.
    - `PR hygiene` ([Run 39330108974](https://github.com/Tiflit/DXVK-Companion/actions/runs/39330108974)): PASS.
    - `AI Workflow Tests` ([Run 39330108944](https://github.com/Tiflit/DXVK-Companion/actions/runs/39330108944)): 270 passed, 3 failures, 1 error. Failures traced to:
      1. Missing `import datetime` in test file.
      2. Linux path anchoring in `is_path_inside_or_equal` when synthetic `cwd` was passed for relative targets.
      3. Python universal-newlines translation on raw text assertions (`newline=""` added to `open(..., 'x')` and exact byte read in tests).
      4. OS-specific personal path syntax in `test_safe_backup_display_redacts_personal_home_path` (`/home/...` on POSIX vs `C:/Users/...` on Windows).
  - Repaired in second iteration across allowed paths.

---

### 4. Allowed Paths Compliance

All modified and created files strictly match the declared `### Allowed paths` in Issue #183:
1. `.gitignore`
2. `scripts/ai-workflow/update_pr_body.py`
3. `tests/ai-workflow/test_update_pr_body.py`
4. `tests/ai-workflow/test_repository_hygiene.py`
5. `docs/ai/PRIVACY-HANDLING.md`
6. `docs/ai-journal/2026-10-19-workflow-batch-1-gemini-session-1.md`

No product code, project files, or unapproved paths were modified.

---

### 5. Next Steps

1. Commit and push feature branch `feat/issue-183-workflow-backup-hygiene`.
2. Open Pull Request referencing `Fixes #183`.
3. Verify Cloud CI workflows pass.
4. Yield for Codex review; human performs final merge.
