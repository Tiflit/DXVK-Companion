# AI Workflow Privacy and Recovery Handling Guide

This document defines privacy boundaries, recovery data management, repository hygiene, and security incident response procedures for AI-assisted workflows in DXVK-Companion.

---

## 1. Privacy Boundaries & Recovery Storage

Local recovery backups are critical for preserving baseline states and recovering from unexpected concurrent modifications or failed operations. However, uncurated recovery data must never be exposed to public version control or external publication channels.

### External Backup Destination Mandate
- **Mandatory External Placement:** For any mutation operation (`--write`) on a Pull Request or Issue that performs a write requiring a pre-write recovery backup, the workflow helper (`scripts/ai-workflow/update_pr_body.py`) strictly mandates an explicit `--backup-dir` destination.
- **Fail-Closed Repository Boundary Enforcement:** The helper discovers all local Git worktrees of the caller repository (the main checkout and linked worktrees registered with `git worktree list --porcelain` and `git rev-parse --show-toplevel`). Furthermore, the helper probes whether the destination or its nearest existing directory ancestor is located inside *any* locally establishable Git repository or worktree (including independent repositories, `.git` directories, and non-existent destination suffixes within checkouts) using `git -C <nearest_ancestor> rev-parse --is-inside-work-tree --is-inside-git-dir`. If the specified `--backup-dir` is located within, or equal to, any checkout or worktree, the operation refuses immediately before any filesystem directory is created and before any GitHub API mutation (`PATCH`) is executed.
- **Strict Path Anchoring & Alias Resolution:** Path validation explicitly anchors relative paths against `cwd` (or `Path.cwd()`), strictly resolves existing ancestor symlinks and directory junctions with `resolve(strict=True)` (failing closed with `SecurityValidationError("Backup destination path resolution failed.")` on loops, broken symlinks, or access denials), appends non-existent suffixes, and returns/uses the single validated absolute `Path` for downstream `mkdir` and pre-write file creation. Path characters are preserved losslessly when parsing `git worktree list --porcelain`.
- **Boundary Failure & Absence Verification:** The helper distinguishes established non-repository absence from query failures, timeouts, broken pointers, corrupted metadata, or inaccessible repository evidence. Negative Git probe outputs (`not a git repository`) are only accepted as safe absence when a bounded check of the destination's relevant ancestors confirms that no `.git` repository-marker evidence (files, directories, or symlinks) exists in the ancestry chain. Any destination with broken, corrupted, or inaccessible repository markers fails closed with category-only diagnostic errors without creating directories, writing backups, or mutating remote entities.
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

## 3. Shared Privacy Guard & Redaction Architecture

Automated publication helpers in this repository (`scripts/ai-workflow/update_pr_body.py` and `scripts/ai-workflow/update_dashboard.py`) route privacy detection, diagnostics, and display redaction through a single standard-library-only module: `scripts/ai-workflow/privacy_guard.py`.

### Operational Scope & Limitations
- **Bounded In-Repo Scope:** The operational scope of `privacy_guard.py` is strictly bounded to the two in-repo publication helpers: `scripts/ai-workflow/update_pr_body.py` (PR body, review record, and diff sanitization) and `scripts/ai-workflow/update_dashboard.py` (dashboard issue body and table cell sanitization).
- **Pattern-Based Guard (No Zero-Leak Guarantee):** The guard uses deterministic regular expressions targeting recognized path prefixes, common token formats, and URL query strings. It is **not** an exhaustive personal-data discovery engine, natural-language entity extractor, or classifier, and does not provide an absolute zero-leak guarantee for arbitrary unstructured text or novel credential formats.

### Design & Environment Purity
- **Standard Library Only:** Zero external packages. Imports are strictly limited to `re`, `pathlib`, `typing`, and `__future__`.
- **Zero Side-Effects:** Zero network access, zero subprocess execution, zero environment identity probing (`os.environ["USER"]`, `os.getenv("USER")`, `USERNAME`, etc.), and zero private-data reads upon import or execution.
- **Dynamic & Static Boundary Verification:** Purity is enforced via static source inspection and verified dynamically in automated tests (`tests/ai-workflow/test_privacy_guard.py`) by instrumenting network, subprocess, environment probe, and filesystem read/write calls (`builtins.open`, `io.open`, `Path.open`, `Path.read_text`, `os.environ` mapping access, `os.getenv`) during dynamic module reloads, including seeded failure assertions. The only permitted read is the exact repository `privacy_guard.py` source path and its exact interpreter bytecode cache path, both established before the guarded boundary starts; lookalike names (for example `not_privacy_guard.py` or another directory's `privacy_guard.py`) are rejected before any real file access.
- **Separate Detection & Redaction:** Exposes distinct scanning (`scan_for_privacy_violations`, `is_privacy_sensitive_text`) and safe redaction (`redact_privacy_text`, `sanitize_display_text`, `sanitize_diff_line`, `sanitize_diagnostic`).

### Path Families & Credential Coverage
The privacy guard recognizes and filters:
- **Windows Personal-Home Paths:** Detects Windows user profiles (`C:\Users\<user>\...`, `C:\Documents and Settings\<user>\...`, and `/Users/<user>/...`) across slash variants (`\`, `/`, `\\`), casing, and punctuation prefixes (equals `path=`, colons `setting:`, brackets `[...]`, parentheses `(...)`, angles `<...>`, braces `{...}`, and quotes `""`, `''`, `` ` ``).
- **WSL Home Paths:** Detects WSL mapped Windows user directories (`/mnt/<drive>/Users/...` and `/mnt/<drive>/Documents and Settings/...`).
- **POSIX Home Paths:** Detects Unix and macOS personal user directories (`/home/<user>/...` and `/Users/<user>/...`).
- **Unicode, Spaces, Punctuation & Multi-Word Names (Conservative Spans):** Handles usernames and path components with Unicode characters (e.g. `C:\Users\Éxample\...`), apostrophes (`C:\Users\O'Connor\...`), punctuation, and multi-word names (e.g. `C:\Users\Example\Private Surname`, `C:\Users\Example\Private+Sentinel.txt`, `C:\Users\Example\Private and Sentinel.txt`). English words (`and`, `in`, `for`, `with`, `missing`, `unavailable`, ...) are valid parts of private names and are **not** treated as boundaries. An unquoted personal path is redacted from its recognized home prefix through the first reliable structural boundary: end of text, newline or tab, a colon followed by whitespace (so `path: message` diagnostics keep their message), or an unbalanced closing delimiter; balanced `(...)`, `[...]` and `{...}` groups are consumed. Where the end of an unquoted path is ambiguous the guard intentionally over-redacts trailing prose (for example `Failed at C:\Users\Example\Private Surname on line 12` becomes `Failed at [REDACTED_PATH]`). Quote the path to preserve surrounding prose. This is conservative pattern handling, not a proof that no fragment of an unrecognized or unusually delimited path can ever remain.
- **Bounded UNC / Network Share Handling:** Host-only bypasses are strictly prohibited. The whole UNC span (host, share, and any continuation introduced by a slash or space, e.g. `\\server\share\+PrivateSentinel`, `//server/share/+PrivateSentinel`, `\\server\share PrivateSentinel`, `\\server\share and Sentinel.txt`, `\\nas01\share\Private Surname`) is established before the template exemption is considered, including punctuation within share names. Only a structurally isolated bare documentation template is preserved (`\\server\share`, `//server/share`, `\\host\share`, `//host/share`, `\\example\share`, `//example/share`, `\\placeholder\share`, `//placeholder/share`): it must have nothing after it in the span, for example at end of text, in backticks or quotes, in brackets. An unquoted template followed by prose (`\\server\share is preserved`) is ambiguous and is redacted. Any concrete share or private suffix is detected and redacted to `[REDACTED_PATH]`.
- **Credentials & Tokens:** Scans and redacts GitHub personal access tokens (`ghp_`, `github_pat_`, including embedded tokens like `prefix_ghp_...`), OAuth tokens (`gho_`), user/server tokens (`ghu_`, `ghs_`, `ghr_`), and HTTP `Bearer` tokens adhering to RFC 6750 Section 2.1 b64token syntax (`[A-Za-z0-9\-._~+/]+=*`) to `[REDACTED_TOKEN]`. Complete URL query parameter strings including dots and percent-encoding are redacted to `?[REDACTED_QUERY]`.
- **Preserved Safe Paths:** Safe generic development paths (`D:\dev\DXVK-Companion`, `C:\dev\...`), program directories (`C:\Program Files\...`), game installations (`C:\Games\...`), repository-relative paths, and standard placeholders (`[REDACTED_PATH]`, `<redacted-filename>`) are explicitly preserved.

### Consumer Integration & Redaction Precedence

Share-name punctuation is data, not a template boundary. For example, `\\server\share,PrivateSentinel` and `\\nas01\O'Connor\PrivateSentinel` are redacted through their conservative span. Even an unquoted template followed by punctuation and prose (`\\server\share, then`) is redacted; use an isolated quoted/backtick literal when a generic example must survive. Home-prefix detection also recognizes colon-adjacent forms such as `setting:/home/Example/data` and `setting:/mnt/c/Users/Example/data`, without restricting the username to an ASCII or word-character alphabet.
Both consumers (`scripts/ai-workflow/update_pr_body.py` and `scripts/ai-workflow/update_dashboard.py`) route all privacy handling through `privacy_guard.py`:
- **Redaction Precedes Truncation:** In `sanitize_display_text`, privacy redaction executes strictly **before** display length truncation. This prevents exposing trailing username tokens or directory fragments cut mid-path at truncation boundaries.
- **Diff Preview Redaction:** Unified-diff previews are sanitized line-by-line (`sanitize_diff_line`) so personal paths or tokens present in dirty remote bodies are safely redacted without corrupting unified-diff structural indicators (`+`, `-`, ` `).
- **Diagnostics & Backup Confirmation:** Error diagnostics use fixed category-only error strings without echoing sensitive path text. Backup confirmation displays use a fixed basename format (`[REDACTED_PATH]/<filename>`).
- **Preservation of Core Protections:** Consumer adoption of `privacy_guard.py` does not weaken any existing backup, collision preservation, expected-body hash validation, or readback verification guarantees.

---

## 4. Explicit local publication preflight

`scripts/ai-workflow/publication_preflight.py` is an opt-in, read-only cooperative check. It does not publish, install hooks, modify Git configuration, create a terms file, discover account/host identities, or obtain credentials. It complements the existing offline scope/hygiene preflight; a result is not authorization, scope approval or remote-freshness evidence. The declared `--repo` must be an existing local checkout root in every mode. Text mode reads only its layout metadata plus the explicitly supplied files; Git modes read selected index/object data, not candidate working-tree contents.

Examples (run only within an approved local execution profile and after the owner's readiness acknowledgment):

```powershell
python scripts/ai-workflow/publication_preflight.py --repo D:/dev/Project --text-file D:/dev/local-private/comment.md
python scripts/ai-workflow/publication_preflight.py --repo D:/dev/Project --staged
python scripts/ai-workflow/publication_preflight.py --repo D:/dev/Project --commits BASE..TIP --private-terms D:/dev/local-private/terms.txt
python scripts/ai-workflow/publication_preflight.py --repo D:/dev/Project --commits BASE..TIP --public-identity 'Generic Agent <agent@example.invalid>'
```

Only one input mode is permitted. Repeat `--text-file` for multiple explicit UTF-8 files. `--staged` requires an existing HEAD and excludes unstaged/untracked/ignored content; it checks both old and new blobs, including deleted text. It cannot inspect the identity/message of a commit not yet created: repeat the check in commit mode before publishing commits. `--commits` accepts an explicit local two-dot range using simple refs/full object IDs (no shell expressions, revision operators or automatic remote-ref selection). It checks every introduced commit's raw author/committer fields and message, and changes relative to every parent; an endpoint-only diff is insufficient. Annotated tag metadata, future commits and omitted attachments are outside this mode.

Git operations are fixed plumbing reads (`rev-parse`, `diff-index`, `diff-tree`, `rev-list`, `cat-file`) with no shell. Hooks, fsmonitor, external diffs, configured diff order files, textconv, replacement objects, optional locks and lazy object fetch are disabled for those calls without persistent configuration changes. Raw-object access follows Git's [diff-index raw format](https://git-scm.com/docs/git-diff-index), [diff-tree parent comparisons](https://git-scm.com/docs/git-diff-tree) and [cat-file object reads](https://git-scm.com/docs/git-cat-file); it never requests `--filters` or `--follow-symlinks`. Git must support the `--no-lazy-fetch` safety flag; unsupported versions refuse. Before Git setup, bounded reads of the declared repository metadata reject local config include sections and continued config lines; include targets are not opened. Global/system Git configuration and inherited identity/credential/Git override variables are not forwarded. This assumes a trusted native Git executable and explicitly selected local filesystem; the checker cannot attest the physical location of operating-system mounts. Explicit UNC/URL paths and Windows shell-wrapper Git executables are refused. There are no network/authentication APIs, fetch/push calls or connector integrations.

An optional private-terms file must be a regular UTF-8 file outside the selected checkout, its Git metadata/common directory, and the tool checkout. Symlinked files and lexically in-checkout aliases are rejected. Supply 1–256 nonempty literal lines, each 3–128 characters, within 32 KiB. Matching uses Unicode NFC normalization and casefolded **substring** matching, without regular expressions or guessed word boundaries. This deliberately favors false positives. No list is discovered, generated, copied into fixtures/CI/source, hashed into the report or echoed. Cloud tests use synthetic terms only. Keep real lists private; never paste them into a prompt, journal or PR.

Default public commit identities require a GitHub noreply address (`login@users.noreply.github.com` or `number+login@users.noreply.github.com`) and a matching login in the name field, including supported bot suffixes. A noreply email alone does not approve a different personal name. Repeated `--public-identity 'Generic Name <agent@example.invalid>'` explicitly declares up to 16 approved generic identities using reserved `.invalid` addresses; it is a human-provided policy input, not automatic proof that the chosen name is generic. Private-term matching still applies. Identities/history are never rewritten. Ordinary third-party attribution in source text is not classified as a commit identity.

Inputs are bounded: 128 files, 64 introduced commits (at most 8 parents each), 1 MiB per file/object, 8 MiB total inspected content, 2 MiB per Git response, 30 seconds overall Git/check deadline (10 seconds per read command, plus bounded owned-process cleanup). Output is one bounded JSON report with at most 32 category/source-kind/ordinal/line findings (file ordinals stay stable across commit history), remaining finding count, exact inspected counts, resolved base/tip IDs where applicable, limits and a candidate SHA-256 covering selected names/content/object identities. It contains no local paths, matched terms, raw argument values, identities or messages. Private terms are not part of that public digest. Repeated basenames count as separate supplied text inputs. A changed payload requires a new check; this is not a reusable approval token.

Exit **0** means the supported selected candidate was inspected without a pattern finding; **1** means inspection completed but findings refuse publication; **2** means incomplete/invalid/unsupported and is also a refusal. Unknown suffixes, binary/control-containing/non-UTF-8 data, LFS pointers, symlinks/gitlinks, unmerged index entries, unborn/missing HEAD, shallow/overlaid history, missing objects, oversize data, changing staged snapshots and failed reads never receive a clean result. Supported text suffixes/names are explicitly listed in the checker. Pattern detection is not general PII recognition; deliberately sensitive synthetic fixtures can be flagged too. Unsupported attachments and contextual findings require human review using an approved tool; this checker supplies no bypass, attachment scanner or publication broker.

## 5. Incident Response Procedure

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
