# AI Engineering Journal: Shared Privacy Detection and Redaction Guard

**Date**: 2026-10-19  
**Session**: Gemini Session 1  
**Primary Issue**: [#184](https://github.com/Tiflit/DXVK-Companion/issues/184)  
**Pull Request**: [#190](https://github.com/Tiflit/DXVK-Companion/pull/190)  
**Base Revision**: `31a7a300f749419756db9867d3a842cd3ca5e314` (`origin/main`, includes merged #183 / PR #189)  
**Policy Identity**: `f3e9974371cb08d1dd99192bd314a135010594ed`  
**Head Commit**: `c10986432fc87f7d1bb16c97bc867a0a7f9efc6e`  

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
- **Cloud CI execution on PR #190**:
  - `Run Python workflow tests` (`AI Workflow Tests`): PASS ([Run 39342736334](https://github.com/Tiflit/DXVK-Companion/actions/runs/39342736334)) (322 tests passed in 13s)
  - `AI task scope`: PASS ([Run 39342736371](https://github.com/Tiflit/DXVK-Companion/actions/runs/39342736371))
  - `PR hygiene`: PASS ([Run 39342736225](https://github.com/Tiflit/DXVK-Companion/actions/runs/39342736225))
  - `build-and-test`: PASS ([Run 39342736409](https://github.com/Tiflit/DXVK-Companion/actions/runs/39342736409)) (971 tests passed in 2m3s)
  - `Acquire Facts and Render Snapshot`: PASS ([Run 39342734161](https://github.com/Tiflit/DXVK-Companion/actions/runs/39342734161))
  - `Publish Dashboard to Dedicated Issue`: PASS ([Run 39342734161](https://github.com/Tiflit/DXVK-Companion/actions/runs/39342734161))

---

### 4. Review Resolutions (Codex Review R1–R4)

Following review of PR #190 by Codex, findings R1–R4 were resolved on branch `feat/issue-184-shared-privacy-guard`:

1. **R1 (P1) — Complete Personal-Home Path Redaction (Unicode, Apostrophes, Multi-word)**:
   - Eliminated ASCII-only restrictions and apostrophe/space truncation in `scripts/ai-workflow/privacy_guard.py`.
   - Updated `UNQUOTED_HOME_PATH_PATTERN` with Unicode-aware word matching (`\w`), apostrophe handling (`O'Connor`), and multi-word names (`Example Middle Person`) across Windows, WSL, and POSIX prefixes.
   - Enforced paired delimiters (`"..."`, `'...'`, `` `...` ``, `(...)`, `[...]`, `<...>`, `{...}`) to preserve quoted names with spaces or apostrophes.
   - Ensured exact parity between `scan_for_privacy_violations` detection and `redact_privacy_text` redaction so no suffix tokens or trailing directory components leak into output or error diagnostics.

2. **R2 (P1) — Strict Bounded UNC Template Preservation**:
   - Removed broad host-only bypasses (`GENERIC_UNC_HOSTS`).
   - Implemented `is_generic_unc_template(full_unc)`: preserves only exact bare documentation placeholder templates (`\\server\share`, `//server/share`, `\\host\share`, `//host/share`, `\\example\share`, `//example/share`) without private shares or subpaths.
   - Concrete shares and private suffixes under any host (e.g. `\\server\share\repo\private_tool`, `\\localhost\my_share\data`, `\\127.0.0.1\c$\secret`, `\\nas01\Team-Share\private_tool`) are detected and redacted to `[REDACTED_PATH]`.

3. **R3 (P1) — RFC 6750 Bearer Tokens, Full Query Strings & Embedded Tokens**:
   - Expanded query string pattern `r"\?[^\s\"'`()<>[\]{}|#]+"` to consume complete query parameter values including dots, percent-encoding, and nested symbols (`?[REDACTED_QUERY]`).
   - Updated `BEARER_TOKEN_PATTERN` to RFC 6750 Section 2.1 b64token syntax (`[A-Za-z0-9\-._~+/]+=*`), supporting `~`, `+`, `/`, and trailing `=`.
   - Removed leading `\b` word boundary on GitHub token patterns to preserve prior dashboard behavior for embedded tokens (`prefix_ghp_...`).

4. **R4 (P2) — Dynamic Execution Boundary Test & Documentation Scoping**:
   - Added `test_guarded_import_verification_zero_side_effects` in `tests/ai-workflow/test_privacy_guard.py`: instruments network (`socket`, `urllib`), subprocess (`subprocess.Popen`), environment probing (`os.environ`), and filesystem writes (`io.open_code`, `builtins.open(..., 'w')`) during `importlib.reload`.
   - Updated `docs/ai/PRIVACY-HANDLING.md`: explicitly bounded operational scope to the two in-repo consumers (`update_pr_body.py` and `update_dashboard.py`), documented the pattern-based nature without claiming general personal-data discovery or zero-leak guarantees, and distinguished static source checks from dynamic execution boundary verification.

5. **Cloud CI Verification for R1–R4 Resolutions**:
   - `Run Python workflow tests` (`AI Workflow Tests`): PASS ([Run 39346455855](https://github.com/Tiflit/DXVK-Companion/actions/runs/39346455855)) (322 tests passed in 14s)
   - `AI task scope`: PASS ([Run 39346455767](https://github.com/Tiflit/DXVK-Companion/actions/runs/39346455767))
   - `PR hygiene`: PASS ([Run 39346455790](https://github.com/Tiflit/DXVK-Companion/actions/runs/39346455790))
   - `build-and-test`: PASS ([Run 39346455785](https://github.com/Tiflit/DXVK-Companion/actions/runs/39346455785)) (971 tests passed in 2m6s)
   - `Acquire Facts and Render Snapshot`: PASS ([Run 39346454077](https://github.com/Tiflit/DXVK-Companion/actions/runs/39346454077))
   - `Publish Dashboard to Dedicated Issue`: PASS ([Run 39346454077](https://github.com/Tiflit/DXVK-Companion/actions/runs/39346454077))
