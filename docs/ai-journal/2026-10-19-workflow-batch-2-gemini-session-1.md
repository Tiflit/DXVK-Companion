# AI Engineering Journal: Shared Privacy Detection and Redaction Guard

**Date**: 2026-10-19  
**Session**: Gemini Session 1  
**Primary Issue**: [#184](https://github.com/Tiflit/DXVK-Companion/issues/184)  
**Base Revision**: `31a7a300f749419756db9867d3a842cd3ca5e314` (`origin/main`, includes merged #183 / PR #189)  
**Policy Identity**: `f3e9974371cb08d1dd99192bd314a135010594ed`  

---

### 1. Objective & Task Scope

Implement Issue #184 (Batch 2: Shared privacy detection and redaction guard for publication helpers) under the refreshed human relay:
1. **Shared Standard-Library Privacy Guard (`scripts/ai-workflow/privacy_guard.py`)**:
   - Standard-library-only module (`re`, `pathlib`, `typing`, `__future__`).
   - Zero network access, subprocess execution, environment identity discovery (`os.environ.get("USER")`), module import side effects, or private-data reads.
   - Separate detection (`scan_for_privacy_violations`, `is_privacy_sensitive_text`) and safe display redaction (`redact_privacy_text`, `sanitize_display_text`, `sanitize_diff_line`, `sanitize_diagnostic`).
2. **Comprehensive Path and Credential Families**:
   - Windows personal-home paths across slash variants (`\`, `/`, `\\`), casing, and punctuation prefixes (equals `path=`, colons `setting:`, brackets `[...]`, parentheses `(...)`, angles `<...>`, braces `{...}`, and quotes `""`, `''`, `` ` ``).
   - WSL home paths (`/mnt/<drive>/Users/...` and `/mnt/<drive>/Documents and Settings/...`).
   - POSIX home paths (`/home/<user>/...` and `/Users/<user>/...`).
   - Paths with spaces and full names (e.g. `C:\Users\Example Person\...`), ensuring neither full paths nor unique username/suffix tokens (`"Person"`) leak into redacted output or diagnostics.
   - Bounded UNC and network-path handling: concrete host/share identifiers (e.g. `\\nas01\share`, `//server02/share`) are redacted to `[REDACTED_PATH]`, while genuinely generic placeholder hosts (`server`, `host`, `example`, `placeholder`, `localhost`, `127.0.0.1`) are preserved.
   - Credentials and tokens (`ghp_`, `github_pat_`, `gho_`, `ghu_`, `ghs_`, `ghr_`, `bearer <token>`) and URL query parameters.
3. **Preservation of Safe and Development Paths**:
   - Explicitly preserve repository-relative paths, generic development paths (`D:\dev\DXVK-Companion`, `C:\dev\...`), program paths (`C:\Program Files\...`), game paths (`C:\Games\...`), and standard placeholders (`[REDACTED_PATH]`, `<redacted-filename>`).
4. **Redaction Precedes Truncation**:
   - In `sanitize_display_text`, privacy redaction runs strictly **before** display length truncation to prevent exposing partial private identifiers at truncation boundaries.
5. **Consumer Routing**:
   - Route both `scripts/ai-workflow/update_pr_body.py` and `scripts/ai-workflow/update_dashboard.py` through `privacy_guard.py` without weakening #183's backup, collision, expected-body, or readback protections.
6. **Documentation & Unit Testing**:
   - Update `docs/ai/PRIVACY-HANDLING.md` detailing the shared privacy guard architecture.
   - Author thorough unit tests in `tests/ai-workflow/test_privacy_guard.py` covering positive/negative detection, environment purity, paths with spaces, UNC handling, and display truncation ordering.
   - Extend `tests/ai-workflow/test_dashboard.py` and `tests/ai-workflow/test_update_pr_body.py` to verify consumer alignment.

---

### 2. Implementation Details

1. **`scripts/ai-workflow/privacy_guard.py`**:
   - Defined `PrivacyViolation` NamedTuple with `(line_number, category)` and `PrivacyViolationError`.
   - Defined `GENERIC_UNC_HOSTS = {"server", "host", "example", "placeholder", "localhost", "127.0.0.1"}`.
   - Implemented regex patterns:
     - `CREDENTIAL_TOKEN_PATTERN` covering `ghp_`, `github_pat_`, `gho_`, `ghu_`, `ghs_`, `ghr_`, and `bearer <token>`.
     - `WINDOWS_HOME_PREFIX_PATTERN` and `WSL_HOME_PREFIX_PATTERN` supporting single and double backslashes/slashes.
     - `POSIX_HOME_PREFIX_PATTERN` for `/home/` and `/Users/`.
     - `QUOTED_HOME_PATH_PATTERN` matching bracketed, quoted, and parenthesized paths.
     - `UNQUOTED_WINDOWS_HOME_PATTERN`, `UNQUOTED_WSL_HOME_PATTERN`, `UNQUOTED_POSIX_HOME_PATTERN` matching multi-word space-separated segments and filenames without trailing suffix token leakage.
   - Implemented `find_concrete_unc_violations` and `redact_unc_paths` to detect and redact concrete host identifiers while keeping generic placeholders intact.
   - Implemented `scan_for_privacy_violations(text)` returning `(line_number, category)` tuples without echoing sensitive text.
   - Implemented `redact_privacy_text(text)` and `sanitize_privacy_text(text)` replacing sensitive items with `[REDACTED_PATH]`, `[REDACTED_TOKEN]`, and `?[REDACTED_QUERY]`.
   - Implemented `sanitize_display_text(text, max_len=100)`: runs redaction before length truncation, normalizes whitespace and markdown injection.
   - Implemented `sanitize_diff_line(diff_line)`: preserves diff prefix indicators (`+`, `-`, ` `) while redacting payloads.
   - Implemented `sanitize_diagnostic(msg)` and `format_safe_filename_diagnostic(path)`.
   - Implemented `format_safe_backup_display(backup_path)` returning `[REDACTED_PATH]/{backup_path.name}`.

2. **`scripts/ai-workflow/update_dashboard.py`**:
   - Imported `_pg_sanitize_diagnostic` and `_pg_sanitize_display_text` from `privacy_guard`.
   - Updated `sanitize_display_text` and `sanitize_diagnostic` to delegate directly to `privacy_guard`.
   - Replaced old aggressive Windows path redaction (`[A-Za-z]:\\[^:\r\n\t|<>\"`]+`) with `privacy_guard`, allowing generic development and program paths (`D:\dev\...`, `C:\Program Files\...`) to be preserved safely.

3. **`scripts/ai-workflow/update_pr_body.py`**:
   - Imported `PrivacyViolation`, `PrivacyViolationError`, and all privacy functions directly from `privacy_guard`.
   - Removed duplicated regexes and functions from `update_pr_body.py` while re-exporting all existing names for backward compatibility.
   - Preserved all #183 external backup validations, raw snapshot byte writing, collision preservation, and concurrency readbacks intact.

4. **`docs/ai/PRIVACY-HANDLING.md`**:
   - Expanded Section 3 with detailed documentation of the shared privacy guard architecture, zero side-effect purity, supported path families, UNC handling, display truncation ordering, and consumer integration.

5. **`tests/ai-workflow/test_privacy_guard.py`**:
   - Authored new unit test suite verifying:
     - Standard-library-only imports and zero side-effects on reload.
     - Positive detection of Windows, WSL, POSIX paths across slash and case variants.
     - Punctuation prefixes (equals, colon, quotes, brackets).
     - Paths with spaces (e.g. `C:\Users\Example Person\...`) without suffix token leakage.
     - Concrete UNC detection vs. generic UNC placeholder preservation.
     - Credentials, PATs, OAuth, and Bearer tokens.
     - Safe development, program, game, and repository-relative paths.
     - Redaction preceding display truncation in `sanitize_display_text`.
     - Safe diagnostic and diff line formatting.

6. **`tests/ai-workflow/test_dashboard.py`**:
   - Added `test_dashboard_sanitizer_preserves_generic_dev_paths` and `test_dashboard_sanitizer_redacts_concrete_unc_paths`.

7. **`tests/ai-workflow/test_update_pr_body.py`**:
   - Added tests for WSL home paths, concrete UNC paths, generic UNC placeholders, and paths with spaces.

---

### 3. Verification & Observability

- **Local host execution**: `NOT RUN (pending human authorization)` under `AGENTS.md`. No host builds, tests, or application processes were executed on the Windows host.
- **Git and metadata inspection**:
  - `git diff` verified clean, exact, and strictly bounded to the 8 declared allowed paths.
  - Zero files modified outside the declared scope.
- **Cloud CI execution**:
  - Verification to be performed entirely via GitHub Actions on the opened Pull Request:
    - `AI Workflow Tests`
    - `AI task scope`
    - `PR hygiene`
    - `Build and Test`
