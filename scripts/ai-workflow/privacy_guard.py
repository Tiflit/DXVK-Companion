"""
Standard-library-only privacy detection and redaction guard for publication helpers.

Provides shared pattern-based detection and safe display redaction of sensitive
personal-home paths, credentials, tokens, and concrete UNC network paths for
update_pr_body.py and update_dashboard.py, while preserving generic development paths,
program names, and placeholders.

Governed by docs/ai/PRIVACY-HANDLING.md and Issue #184.
"""

from __future__ import annotations

import re
from pathlib import Path
from typing import Any, List, NamedTuple, Optional, Pattern, Set, Tuple, Union

__all__ = [
    "PrivacyViolation",
    "PrivacyViolationError",
    "GENERIC_UNC_TEMPLATES",
    "scan_for_privacy_violations",
    "is_privacy_sensitive_text",
    "is_generic_unc_template",
    "redact_privacy_text",
    "sanitize_privacy_text",
    "sanitize_display_text",
    "sanitize_diff_line",
    "sanitize_diagnostic",
    "format_safe_filename_diagnostic",
    "format_safe_backup_display",
]


class PrivacyViolation(NamedTuple):
    """Represents a detected privacy violation with line number and category only."""
    line_number: int
    category: str


class PrivacyViolationError(Exception):
    """Raised when personal paths, concrete UNC paths, or credentials are detected."""
    pass


# Exact bare documentation placeholder templates for UNC paths.
# Only these literal templates (without private shares or arbitrary subpaths) are preserved.
GENERIC_UNC_TEMPLATES: Set[str] = {
    r"\\server\share",
    r"\\host\share",
    r"\\example\share",
    r"\\placeholder\share",
    r"//server/share",
    r"//host/share",
    r"//example/share",
    r"//placeholder/share",
}

# Credential tokens: GitHub tokens (including embedded after prefix/underscore) and Bearer tokens (RFC 6750)
CREDENTIAL_TOKEN_PATTERN: Pattern[str] = re.compile(
    r"(?:ghp_|github_pat_|gho_|ghu_|ghs_|ghr_)[A-Za-z0-9_]{8,}|(?:^|[\s\"'`(<\[{:,=])bearer\s+[A-Za-z0-9\-._~+/]{8,}=*",
    re.IGNORECASE,
)

# Specific pattern for GitHub tokens to redact without requiring leading word boundary
GITHUB_TOKEN_PATTERN: Pattern[str] = re.compile(
    r"(?:ghp_|github_pat_|gho_|ghu_|ghs_|ghr_)[A-Za-z0-9_]{8,}",
    re.IGNORECASE,
)

# Specific pattern for Bearer tokens adhering to RFC 6750 section 2.1 b64token syntax
BEARER_TOKEN_PATTERN: Pattern[str] = re.compile(
    r"(^|[\s\"'`(<\[{:,=])bearer\s+[A-Za-z0-9\-._~+/]{8,}=*",
    re.IGNORECASE,
)

# URL query string parameter pattern: matches ? through full query string up to delimiter/whitespace/anchor
QUERY_STRING_PATTERN: Pattern[str] = re.compile(
    r"\?[^\s\"'`()<>[\]{}|#]+",
    re.IGNORECASE,
)

# Windows personal home path prefix (matches drive root or rooted / Users or Documents and Settings)
# Supports single and double backslashes/slashes across case variants
WINDOWS_HOME_PREFIX_PATTERN: Pattern[str] = re.compile(
    r"(?:file:///)?(?:[A-Za-z]:[/\\]+|(?<![^\s\"'`()<>[\]{}|;,=])[/\\]+)(?:Users|Documents and Settings)[/\\]+",
    re.IGNORECASE,
)

# WSL Windows personal path prefix (/mnt/<drive>/Users or Documents and Settings)
WSL_HOME_PREFIX_PATTERN: Pattern[str] = re.compile(
    r"(?<![^\s\"'`()<>[\]{}|;,=])/mnt/[A-Za-z]/(?:Users|Documents and Settings)[/\\]+",
    re.IGNORECASE,
)

# POSIX personal home path prefix (/home/<user> or /Users/<user>)
POSIX_HOME_PREFIX_PATTERN: Pattern[str] = re.compile(
    r"(?:file:///)?(?<![^\s\"'`()<>[\]{}|;,=])/(?:home|Users)/[\w'.-]+",
    re.IGNORECASE,
)

# Individual pair-matched quoted/bracketed personal paths:
# Matches quotes ("...", '...', `...`), parens ((...)), brackets ([...]), angles (<...>), and braces ({...})
# Each opening delimiter matches only its corresponding closing delimiter, preserving apostrophes and
# delimiters inside other quote styles.
QUOTED_HOME_PATH_PATTERNS: List[Tuple[Pattern[str], str, str]] = [
    (
        re.compile(
            r'"((?:file:///)?(?:[A-Za-z]:[/\\]+|(?<![^\s"\'`()<>[\]{}|;,=])[/\\]+|/(?:mnt/[A-Za-z]/)?)(?:Users|Documents and Settings|home)[/\\]+[^"\r\n\t]+)"',
            re.IGNORECASE,
        ),
        '"',
        '"',
    ),
    (
        re.compile(
            r"'((?:file:///)?(?:[A-Za-z]:[/\\]+|(?<![^\s\"'`()<>[\]{}|;,=])[/\\]+|/(?:mnt/[A-Za-z]/)?)(?:Users|Documents and Settings|home)[/\\]+[^'\r\n\t]+)'",
            re.IGNORECASE,
        ),
        "'",
        "'",
    ),
    (
        re.compile(
            r"`((?:file:///)?(?:[A-Za-z]:[/\\]+|(?<![^\s\"'`()<>[\]{}|;,=])[/\\]+|/(?:mnt/[A-Za-z]/)?)(?:Users|Documents and Settings|home)[/\\]+[^`\r\n\t]+)`",
            re.IGNORECASE,
        ),
        "`",
        "`",
    ),
    (
        re.compile(
            r"\(((?:file:///)?(?:[A-Za-z]:[/\\]+|(?<![^\s\"'`()<>[\]{}|;,=])[/\\]+|/(?:mnt/[A-Za-z]/)?)(?:Users|Documents and Settings|home)[/\\]+[^)\r\n\t]+)\)",
            re.IGNORECASE,
        ),
        "(",
        ")",
    ),
    (
        re.compile(
            r"\[((?:file:///)?(?:[A-Za-z]:[/\\]+|(?<![^\s\"'`()<>[\]{}|;,=])[/\\]+|/(?:mnt/[A-Za-z]/)?)(?:Users|Documents and Settings|home)[/\\]+[^\]\r\n\t]+)\]",
            re.IGNORECASE,
        ),
        "[",
        "]",
    ),
    (
        re.compile(
            r"<((?:file:///)?(?:[A-Za-z]:[/\\]+|(?<![^\s\"'`()<>[\]{}|;,=])[/\\]+|/(?:mnt/[A-Za-z]/)?)(?:Users|Documents and Settings|home)[/\\]+[^>\r\n\t]+)>",
            re.IGNORECASE,
        ),
        "<",
        ">",
    ),
    (
        re.compile(
            r"\{((?:file:///)?(?:[A-Za-z]:[/\\]+|(?<![^\s\"'`()<>[\]{}|;,=])[/\\]+|/(?:mnt/[A-Za-z]/)?)(?:Users|Documents and Settings|home)[/\\]+[^}\r\n\t]+)\}",
            re.IGNORECASE,
        ),
        "{",
        "}",
    ),
]

# Unquoted personal path pattern:
# Matches from the personal home prefix through all path segments (Unicode, spaces, apostrophes, etc.)
# Branch 1 matches paths with internal slashes; intermediate segments end with slashes, while the terminal
# segment (file or single subfolder) does not consume trailing whitespace or prose words.
# Branch 2 matches a bare home directory name directly under Users/home at line end or delimiter.
UNQUOTED_HOME_PATH_PATTERN: Pattern[str] = re.compile(
    r"(?P<prefix>(?:file:///)?(?:[A-Za-z]:[/\\]+|(?<![^\s\"'`()<>[\]{}|;,=])[/\\]+)(?:Users|Documents and Settings)[/\\]+|(?<![^\s\"'`()<>[\]{}|;,=])/mnt/[A-Za-z]/(?:Users|Documents and Settings)[/\\]+|(?:file:///)?(?<![^\s\"'`()<>[\]{}|;,=])/(?:home|Users)/)"
    r"(?P<tail>"
    r"[\w'.-]+(?: [\w'.-]+)*[/\\]+(?:[\w'.-]+(?: [\w'.-]+)*(?=[/\\])[/\\]+)*(?:[\w'.-]+\.[\w'.-]+|\.\.\.|[\w'.-]+)?"
    r"|"
    r"[\w'.-]+(?: [\w'.-]+)*(?=[\r\n\t]|$|[;,:]\s)"
    r")"
    r"[/\\]?",
    re.IGNORECASE,
)

# UNC pattern matching network share paths with backslashes or forward slashes
UNC_PATH_PATTERN: Pattern[str] = re.compile(
    r"(?:\\\\|(?<![A-Za-z0-9+.-]:)//)"
    r"(?P<host>[^\s\\/\"'*?<>|]+)"
    r"[/\\]+"
    r"(?P<share>[^\s\\/\"'*?<>|]+)"
    r"(?P<extra>(?:[/\\]+[\w'.-]+(?: [\w'.-]+)*(?=[/\\]))*[/\\]+(?:[\w'.-]+\.[\w'.-]+|\.\.\.|[\w'.-]+))?"
    r"[/\\]?",
    re.IGNORECASE,
)


def is_generic_unc_template(full_unc: str) -> bool:
    """
    Returns True ONLY if the UNC path is an exact bare documentation placeholder template.
    Any path with a concrete host, private share, or arbitrary subpath returns False.
    """
    normalized = full_unc.strip().replace("/", "\\").lower().rstrip("\\")
    return normalized in {
        r"\\server\share",
        r"\\host\share",
        r"\\example\share",
        r"\\placeholder\share",
    }


def find_concrete_unc_violations(line: str) -> bool:
    """
    Checks if a line contains concrete UNC or network share paths,
    excluding only exact bare generic placeholder templates (\\\\server\\share, \\\\host\\share, etc.).
    """
    for m in UNC_PATH_PATTERN.finditer(line):
        full = m.group(0)
        if not is_generic_unc_template(full):
            return True
    return False


def redact_unc_paths(text: str) -> str:
    """
    Redacts concrete UNC and network share paths (e.g. \\\\nas01\\share, //server02/share),
    while preserving exact bare generic documentation placeholder templates.
    """
    def _replace_unc(m: re.Match) -> str:
        full = m.group(0)
        if is_generic_unc_template(full):
            return full
        return "[REDACTED_PATH]"

    return UNC_PATH_PATTERN.sub(_replace_unc, text)


def scan_for_privacy_violations(text: str) -> List[Tuple[int, str]]:
    """
    Scans text line-by-line for personal home paths, concrete UNC paths, and credentials/tokens.
    Returns a list of PrivacyViolation (line_number, violation_category) namedtuples.
    Does NOT return or echo matched sensitive text or tokens.
    """
    violations: List[Tuple[int, str]] = []
    if not text:
        return violations

    for line_num, line in enumerate(text.splitlines(), start=1):
        if WINDOWS_HOME_PREFIX_PATTERN.search(line):
            violations.append(PrivacyViolation(line_num, "Windows personal-home path"))
        elif WSL_HOME_PREFIX_PATTERN.search(line):
            violations.append(PrivacyViolation(line_num, "WSL personal-home path"))
        elif POSIX_HOME_PREFIX_PATTERN.search(line):
            violations.append(PrivacyViolation(line_num, "POSIX personal-home path"))
        elif find_concrete_unc_violations(line):
            violations.append(PrivacyViolation(line_num, "UNC / network path"))
        elif CREDENTIAL_TOKEN_PATTERN.search(line):
            violations.append(PrivacyViolation(line_num, "GitHub credential or bearer token"))

    return violations


def is_privacy_sensitive_text(text: Optional[str]) -> bool:
    """Returns True if text contains personal paths, concrete UNC paths, or credentials/tokens."""
    if not text:
        return False
    t_str = str(text)
    return (
        bool(WINDOWS_HOME_PREFIX_PATTERN.search(t_str))
        or bool(WSL_HOME_PREFIX_PATTERN.search(t_str))
        or bool(POSIX_HOME_PREFIX_PATTERN.search(t_str))
        or find_concrete_unc_violations(t_str)
        or bool(CREDENTIAL_TOKEN_PATTERN.search(t_str))
    )


def redact_privacy_text(text: Optional[str]) -> str:
    """
    Sanitizes diagnostic messages, diff lines, and paths to prevent leaking
    Windows personal-home paths (both / and \\), WSL home paths, POSIX home paths,
    concrete UNC network paths, credentials/tokens, and URL query strings.
    Replaces matched substrings with redaction placeholders.
    """
    if not text:
        return ""
    clean = str(text)

    # 1. Redact quoted/bracketed personal paths first (per-pair matching)
    for pat, open_delim, close_delim in QUOTED_HOME_PATH_PATTERNS:
        def _replace_quoted(m: re.Match, o=open_delim, c=close_delim) -> str:
            return o + "[REDACTED_PATH]" + c
        clean = pat.sub(_replace_quoted, clean)

    # 2. Redact unquoted personal paths
    clean = UNQUOTED_HOME_PATH_PATTERN.sub("[REDACTED_PATH]", clean)

    # 3. Redact concrete UNC / network paths
    clean = redact_unc_paths(clean)

    # 4. Redact tokens (GitHub tokens and RFC 6750 Bearer tokens)
    clean = GITHUB_TOKEN_PATTERN.sub("[REDACTED_TOKEN]", clean)
    clean = BEARER_TOKEN_PATTERN.sub(r"\g<1>[REDACTED_TOKEN]", clean)

    # 5. Redact URL query strings
    clean = QUERY_STRING_PATTERN.sub("?[REDACTED_QUERY]", clean)

    return clean


def sanitize_privacy_text(text: Optional[str]) -> str:
    """Alias for redact_privacy_text to maintain drop-in backward compatibility."""
    return redact_privacy_text(text)


def sanitize_display_text(text: Optional[str], max_len: int = 100) -> str:
    """
    Sanitizes untrusted display text for safe inclusion in Markdown and tables.
    Redacts personal paths, concrete UNC paths, and auth tokens BEFORE display truncation.
    Strips markdown link injection, HTML tags, backticks, and pipes, and normalizes whitespace.
    Truncates cleanly to max_len.
    """
    if text is None:
        return ""

    # 1. Privacy redaction occurs BEFORE any truncation
    sanitized = redact_privacy_text(str(text))

    # 2. Markdown and table formatting normalization
    sanitized = re.sub(r"[\r\n\t]+", " ", sanitized)
    sanitized = re.sub(r"<[^>]+>", "", sanitized)
    sanitized = re.sub(r"\[([^\]]+)\]\([^\)]+\)", r"\1", sanitized)
    sanitized = sanitized.replace("`", "")
    sanitized = sanitized.replace("|", "/")
    sanitized = re.sub(r"\s+", " ", sanitized).strip()

    # 3. Safe bounded display truncation
    if len(sanitized) > max_len:
        sanitized = sanitized[: max_len - 3].rstrip() + "..."

    return sanitized


def sanitize_diff_line(diff_line: str) -> str:
    """
    Sanitizes a unified-diff line while preserving diff structural prefixes.
    For diff change lines ('-', '+') and context lines (' '), strips the leading
    indicator, sanitizes the line payload, and restores the indicator.
    All other lines (including hunk headers and standalone text) are sanitized directly.
    """
    if diff_line.startswith(("-", "+", " ")):
        prefix = diff_line[0]
        payload = diff_line[1:]
        return prefix + redact_privacy_text(payload)
    return redact_privacy_text(diff_line)


def sanitize_diagnostic(msg: Any) -> str:
    """Sanitizes diagnostic and error messages to prevent leaking paths, tokens, or raw queries."""
    if msg is None:
        return ""
    return redact_privacy_text(str(msg)).strip()


def format_safe_filename_diagnostic(path: Union[Path, str]) -> str:
    """
    Returns a safe representation of a filename for diagnostic messages.
    If the path or filename contains sensitive user paths, UNC hosts, or tokens,
    it returns '<redacted-filename>' without echoing sensitive content.
    """
    p_str = str(path)
    name = Path(path).name if hasattr(path, "name") or isinstance(path, (str, Path)) else str(path)
    if is_privacy_sensitive_text(p_str) or is_privacy_sensitive_text(name):
        return "<redacted-filename>"
    return name


def format_safe_backup_display(backup_path: Path) -> str:
    """Formats backup confirmation safely using fixed safe basename/redacted identifier without path exposure."""
    return f"[REDACTED_PATH]/{backup_path.name}"
