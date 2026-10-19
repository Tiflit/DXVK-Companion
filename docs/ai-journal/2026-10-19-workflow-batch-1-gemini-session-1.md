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
  - Repaired in second iteration across allowed paths.
  - Second run on PR #189 commit `521ba0174e2d3bbefc4a0346387eaebdfcb104bf`:
    - `AI Workflow Tests` ([Run 39330758410](https://github.com/Tiflit/DXVK-Companion/actions/runs/39330758410)): 274 passed in 30s. PASS.
    - `AI task scope` ([Run 39330758428](https://github.com/Tiflit/DXVK-Companion/actions/runs/39330758428)): PASS.
    - `PR hygiene` ([Run 39330758448](https://github.com/Tiflit/DXVK-Companion/actions/runs/39330758448)): PASS.
    - `Build and Test` ([Run 39330758423](https://github.com/Tiflit/DXVK-Companion/actions/runs/39330758423)): PASS.

---

### 4. Codex Review Findings R1–R4 Resolution

Following PR #189 initial green CI, Codex conducted peer review ([comment 6278239033](https://github.com/Tiflit/DXVK-Companion/pull/189#issuecomment-6278239033)) with findings R1–R4 addressed in-place on branch `feat/issue-183-workflow-backup-hygiene`:

1. **R1 (Independent Repository Boundary Enforcement)**:
   - Extended boundary validation beyond caller repository worktrees to reject destinations inside *any* locally establishable Git repository.
   - Implemented `is_inside_git_repository(target: Path)` which walks up to the nearest existing directory ancestor and probes with `git -C <curr> rev-parse --is-inside-work-tree --is-inside-git-dir`.
   - Distinguishes confirmed non-repository absence (exit code 128 with `"not a git repository"` in stderr) from boundary query failures or timeouts, failing closed on unexpected codes or errors.
   - Added synthetic multi-repo tests in `test_repository_hygiene.py` with Repo A and Repo B verifying that Repo A rejects `--backup-dir` inside Repo B root, nested existing directory, nested non-existent suffix, and `.git` directory before `mkdir` or PATCH.

2. **R2 (Strict Anchoring & Alias Resolution)**:
   - Implemented `anchor_and_resolve_backup_path(backup_dir: Path, cwd: Optional[Path]) -> Path`: explicitly anchors relative paths against supplied `cwd` (defaulting to `Path.cwd()`), strictly resolves existing ancestor symlinks/junctions with `resolve(strict=True)` failing closed with `SecurityValidationError("Backup destination path resolution failed.")` on loops or denials, appends non-existent suffixes, and returns the single validated absolute `Path`.
   - Used this validated absolute `Path` across downstream `mkdir` and pre-write `open` calls in `create_prewrite_backup`.
   - Preserved path characters losslessly in `find_git_worktree_roots` by using `rstrip("\r\n")` without stripping legal internal or leading/trailing spaces from porcelain lines.

3. **R3 (Privacy Leak Prevention in Boundary Diagnostics)**:
   - Formatted backup confirmation displays strictly as `[REDACTED_PATH]/{backup_path.name}` via `format_safe_backup_display`.
   - Used fixed category-only error strings for all security validation exceptions without echoing raw Git stderr, exception tracebacks, or personal paths with spaces (e.g. `C:\Users\Example Person\`).
   - Added regression coverage verifying that paths with spaces never leak personal directory fragments into stdout or stderr.

4. **R4 (Generated Artifacts Exclusion)**:
   - Added root `artifacts/` to `.gitignore` to ignore build provenance and publish directories (`artifacts/provenance/`, `artifacts/publish/`).
   - Verified via `git check-ignore` that provenance and publish artifacts are ignored while tracked source files and test fixtures remain versionable.

---

### 5. Focused Repair Review (Codex Comment 6278681129) Resolution

Following PR #189 first review commit `bbb85007c1fd6618f676aceea4f86d0fe3d3c58d`, Codex completed a focused repair review ([comment 6278681129](https://github.com/Tiflit/DXVK-Companion/pull/189#issuecomment-6278681129)) highlighting unhandled probe exceptions and failure diagnostic leaks. Addressed in-place:

1. **Enclosed All Backup-Boundary Filesystem Probes in Category-Only Handlers**:
   - `exists()`, `is_symlink()`, `is_dir()`, `is_file()`, and `resolve(strict=True)` in `anchor_and_resolve_backup_path`, `is_inside_git_repository`, and `validate_backup_dir` are enclosed in `try ... except Exception:` blocks.
   - Any OS `PermissionError` or probe failure fails closed with fixed category strings (`"Backup destination path resolution failed."` or `"Git worktree boundary could not be established."`), completely eliminating raw exception tracebacks that expose environment paths.

2. **Category-Only Backup Operation Error Handling**:
   - In `create_prewrite_backup`:
     - Wrapped `mkdir` failure to raise `SecurityValidationError("Failed to create pre-write backup directory.")`.
     - Wrapped file write/open failure to raise `SecurityValidationError("Failed to write pre-write recovery backup file.")`.
     - Collision retains exact truthful diagnosis: `FileExistsError(f"Pre-write backup file already exists: {backup_file.name}. Refusing to overwrite.")` (filename only, no directory path).
   - In `main()` (both PR and Issue write modes):
     - Catch `FileExistsError` and `SecurityValidationError` directly, printing the category-only messages without raw exception echo; catch generic `Exception` fallback; exit code 1 with zero PATCH requests dispatched.

3. **Privacy Leak Prevention for Paths with Spaces**:
   - Tested that personal paths with whitespace (e.g. `C:\Users\Example Person\...` or `/home/Example Person/...`) do not leak either full paths or trailing word tokens (`"Person"`) into stdout or stderr.

4. **Documentation Alignment in `docs/ai/PRIVACY-HANDLING.md`**:
   - Clarified that candidate PR bodies and Issue append records fail closed if sensitive data is detected before remote dispatch, whereas unified-diff preview against a dirty remote target redacts sensitive lines line-by-line (`sanitize_diff_line`) rather than aborting clean-replacement operations.

5. **Deterministic Regression Tests in `test_update_pr_body.py`**:
   - Added `test_filesystem_predicates_permission_errors_fail_closed_as_security_validation`.
   - Added `test_main_cli_fails_closed_on_predicate_permission_errors_without_traceback_or_leak`.
   - Added `test_main_cli_fails_closed_on_mkdir_failure_without_traceback_or_leak`.
   - Added `test_main_cli_fails_closed_on_write_failure_without_traceback_or_leak`.
   - Added `test_main_cli_collision_refusal_preserves_existing_backup_without_leak`.
   - Enhanced `test_write_mode_backup_confirmation_never_leaks_personal_paths_with_spaces` to assert absence of trailing personal path fragments.

---

### 6. Allowed Paths Compliance

All modified and created files strictly match the declared `### Allowed paths` in Issue #183:
1. `.gitignore`
2. `scripts/ai-workflow/update_pr_body.py`
3. `tests/ai-workflow/test_update_pr_body.py`
4. `tests/ai-workflow/test_repository_hygiene.py`
5. `docs/ai/PRIVACY-HANDLING.md`
6. `docs/ai-journal/2026-10-19-workflow-batch-1-gemini-session-1.md`

No product code, project files, or unapproved paths were modified.

---

### 7. Next Steps

1. Commit and push repair fixes to feature branch `feat/issue-183-workflow-backup-hygiene`.
2. Monitor Cloud CI workflows across GitHub Actions (`AI Workflow Tests`, `AI task scope`, `PR hygiene`, `Build and Test`).
3. Return new head SHA and verified passing test runs for Codex review; human performs final merge.
