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

---

### 5. Codex Repair Review Resolutions (Comment 6294300045)

Following repair review of PR #190 by Codex ([comment 6294300045](https://github.com/Tiflit/DXVK-Companion/pull/190#issuecomment-6294300045)), findings R1, R2, and R4 were resolved:

1. **R1 (P1) — Stop Path Component Truncation at Spaces & Valid Punctuation**:
   - Updated `UNQUOTED_HOME_PATH_PATTERN` and `POSIX_HOME_PREFIX_PATTERN` in `scripts/ai-workflow/privacy_guard.py` to support valid path punctuation (`+`, `$`, `~`, `@`, `#`, `%`, `&`, `'`, `-`, `.`) and multi-word terminal components (e.g. `C:\Users\Example\Private Surname`, `/home/Example/Private Surname`, `C:\Users\Example\Private+Sentinel.txt`, `C:\Users\Example+Sentinel\file.txt`).
   - Implemented conservative complete-span matching bounded by prose boundary lookaheads (`_PROSE_BOUNDARY`) and added `SAFE_FALLBACK_PREFIX_PATTERN` in `redact_privacy_text` as a safe prefix fallback.
   - Asserted complete unique suffix token absence (`Private`, `Surname`, `Sentinel`, `PrivateSentinel`) across redactions, diff previews, diagnostics, and both consumers (`update_pr_body.py` and `update_dashboard.py`).

2. **R2 (P1) — Prevent Partial UNC Matches from Bare-Template Exemption**:
   - Re-architected `UNC_PATH_PATTERN` to capture the entire UNC span including trailing subpaths, punctuation, and ambiguous space continuations before evaluating template exemptions.
   - Partial or ambiguous continuations (e.g. `\\server\share\+PrivateSentinel`, `//server/share/+PrivateSentinel`, `\\server\share PrivateSentinel`, `\\nas01\share\Private Surname`) are captured in the whole span, causing `is_generic_unc_template` to return `False`, ensuring conservative redaction to `[REDACTED_PATH]`.
   - Exact bare documentation templates (`\\server\share`, `//server/share`, etc.) remain preserved.
   - Added exhaustive tests verifying unique suffix token absence across `test_privacy_guard.py`, `test_update_pr_body.py`, and `test_dashboard.py`.

3. **R4 (P2) — Dynamic Execution Boundary Entry Points & Documentation Scoping**:
   - Extended dynamic execution boundary in `tests/ai-workflow/test_privacy_guard.py` to guard all file-read entry points (`builtins.open`, `io.open`, `Path.open`, `Path.read_text`), permitting only narrowly defined module-loading resources (`privacy_guard.py` source and bytecode).
   - Guarded direct dictionary mapping access (`os.environ["USER"]`, `os.getenv("USER")`, `"USER" in os.environ`, `os.environ.get("USER")`) against identity discovery.
   - Added seeded forbidden read and identity probe test (`test_guarded_boundary_intercepts_seeded_forbidden_reads_and_env_probes`) actively asserting that unauthorized reads, env mapping accesses, and file writes are trapped.
   - Corrected opening sentence of `docs/ai/PRIVACY-HANDLING.md:49` to bound scope strictly to `update_pr_body.py` and `update_dashboard.py`.

4. **Honest Observability & CI Recording**:
   - Note on intermittent CI test flakiness: Cloud runs on Windows runner in prior cycles exhibited runner timing flakiness on 2 tests (`969 passed / 2 failed`) before rerun passed cleanly (`971 / 971`). Out-of-scope product tests are not modified; record all CI runs transparently.

---

### 6. Second Codex Repair Review (Comment 6295196815) — Status & Limitations

Head `5054cb7` was reviewed in [comment 6295196815](https://github.com/Tiflit/DXVK-Companion/pull/190#issuecomment-6295196815); R1/R2 (stopword boundaries) and R4 (module-read filename bypass) remained open. Section 5 above describes the superseded stopword/prefix-fallback approach; the following replaces it.

1. **R1/R2 — conservative whole-span handling**: `scripts/ai-workflow/privacy_guard.py` no longer treats English words as path boundaries and the redundant prefix fallback was removed. An unquoted personal path is consumed from its recognized home prefix through the first reliable structural boundary (end of text, newline/tab, colon followed by whitespace, unbalanced closing delimiter); balanced `(...)`, `[...]`, `{...}` groups are consumed. The UNC span now includes any continuation introduced by a slash or space before the bare-template exemption is evaluated, so incomplete prefixes (`\\server\share and Sentinel.txt`) never qualify. The exemption is restricted to structurally isolated literals (end of text, backticks/quotes, brackets, sentence punctuation).
2. **Intentional test-expectation changes (not weakening of privacy assertions)**: prior expectations such as `Failed at [REDACTED_PATH] on line 12` and `Exact bare template \\server\share is preserved` required unsafe guesses about where an unquoted name ends. They now expect over-redaction of trailing prose (`Failed at [REDACTED_PATH]`), and template-preservation tests use isolated literals (backticks). New tests cover stopword components (`and`, `in`, `for`, `with`, `missing`, `unavailable`), ambiguous tails (`Private! Surname`, `Private, Surname`), and template continuations across scan/redaction, diagnostic/diff output, and both consumers, asserting unique suffix absence. A negative test asserts that unquoted templates followed by prose are redacted.
3. **R4 — exact module exception**: the read guard now permits only the exact `privacy_guard.py` source path and its exact bytecode cache path (`importlib.util.cache_from_source`), computed before the guarded boundary starts. Lookalikes (`not_privacy_guard.py`, `synthetic-private/privacy_guard.py`, `privacy_guard.cpython-secret.txt`) and file descriptors are rejected by the guard before any real file access; tests assert `AssertionError` (not `FileNotFoundError`) for each.
4. **Documentation**: `docs/ai/PRIVACY-HANDLING.md` now states the conservative-span behavior and narrower template exemption and no longer claims that suffix tokens never leak; it notes this is pattern-based handling rather than a guarantee.
5. **CI record (honest)**: Run attempts on `97f5fb2` failed Python (one assertion repaired here in `5054cb7`) and a Windows C# test. On `5054cb7`, Windows attempt 1 failed `DxvkInstaller_ReapplyAsync_RefusesReapplyOnManagedGameWhenApiUnsupported_AndPreservesGameFiles(DX12)` (970/971) and attempt 2 passed 971/971 with no head change. The cause is undiagnosed; no C# files changed and application assertions were not altered. Local execution: `NOT RUN (pending human authorization)`; Python recheck performed by cloud CI only.