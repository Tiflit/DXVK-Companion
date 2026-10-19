"""
Standard-library-only privacy detection and redaction guard for publication helpers.

Provides shared detection of sensitive personal-home paths, credentials, tokens,
and concrete UNC network paths, while preserving generic development paths,
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
    "GENERIC_UNC_HOSTS",
    "scan_for_privacy_violations",
    "is_privacy_sensitive_text",
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


# Literal host placeholders for UNC paths that are known generic documentation examples
GENERIC_UNC_HOSTS: Set[str] = {
    "server",
    "host",
    "example",
    "placeholder",
    "localhost",
    "127.0.0.1",
}

# Credential tokens (GitHub personal access tokens and OAuth/bearer tokens)
CREDENTIAL_TOKEN_PATTERN: Pattern[str] = re.compile(
    r"\b(?:ghp_|github_pat_|gho_|ghu_|ghs_|ghr_)[A-Za-z0-9_]{8,}\b|\bbearer\s+[A-Za-z0-9_.-]{8,}\b",
    re.IGNORECASE,
)

# URL query string parameter pattern
QUERY_STRING_PATTERN: Pattern[str] = re.compile(r"\?[\w=&-]+")

# Windows personal home path prefix (matches drive root or rooted / Users or Documents and Settings)
# Supports single and double backslashes/slashes
WINDOWS_HOME_PREFIX_PATTERN: Pattern[str] = re.compile(
    r"(?:file:///)?(?:[A-Za-z]:[/\\]+|(?<![A-Za-z0-9_.-])[/\\]+)(?:Users|Documents and Settings)[/\\]+",
    re.IGNORECASE,
)

# WSL Windows personal path prefix (/mnt/<drive>/Users or Documents and Settings)
WSL_HOME_PREFIX_PATTERN: Pattern[str] = re.compile(
    r"(?<![A-Za-z0-9_.-])/mnt/[A-Za-z]/(?:Users|Documents and Settings)[/\\]+",
    re.IGNORECASE,
)

# POSIX personal home path prefix (/home/<user> or /Users/<user>)
POSIX_HOME_PREFIX_PATTERN: Pattern[str] = re.compile(
    r"(?:file:///)?(?<![A-Za-z0-9_.-])/(?:home|Users)/[^\s\"'`|<>]+",
    re.IGNORECASE,
)

# Quoted Windows, WSL, or POSIX home paths inside quotes or enclosing delimiters:
# "", '', ``, <>, (), [], or {}
QUOTED_HOME_PATH_PATTERN: Pattern[str] = re.compile(
    r"""(?P<open>["'`(<\[{])"""
    r"""(?P<path>(?:file:///)?(?:[A-Za-z]:[/\\]+|(?<![A-Za-z0-9_.-])[/\\]+|/(?:mnt/[A-Za-z]/)?)(?:Users|Documents and Settings|home)[/\\]+[^"'`()<>[\]{}\r\n\t]+)"""
    r"""(?P<close>["'`>)\]}])""",
    re.IGNORECASE,
)

# Unquoted Windows personal home path with path segments and optional spaces
UNQUOTED_WINDOWS_HOME_PATTERN: Pattern[str] = re.compile(
    r"(?:file:///)?(?:[A-Za-z]:[/\\]+|(?<![A-Za-z0-9_.-])[/\\]+)(?:Users|Documents and Settings)[/\\]+"
    r"(?:[A-Za-z0-9_.-]+(?: [A-Za-z0-9_.-]+)*[/\\]+)*"
    r"(?:[A-Za-z0-9_.-]+\.[A-Za-z0-9_.-]+|\.\.\.|[A-Za-z0-9_.-]+(?: [A-Za-z0-9_.-]+)?)[/\\]?",
    re.IGNORECASE,
)

# Unquoted WSL Windows personal home path
UNQUOTED_WSL_HOME_PATTERN: Pattern[str] = re.compile(
    r"(?<![A-Za-z0-9_.-])/mnt/[A-Za-z]/(?:Users|Documents and Settings)[/\\]+"
    r"(?:[A-Za-z0-9_.-]+(?: [A-Za-z0-9_.-]+)*[/\\]+)*"
    r"(?:[A-Za-z0-9_.-]+\.[A-Za-z0-9_.-]+|\.\.\.|[A-Za-z0-9_.-]+(?: [A-Za-z0-9_.-]+)?)[/\\]?",
    re.IGNORECASE,
)

# Unquoted POSIX personal home path
UNQUOTED_POSIX_HOME_PATTERN: Pattern[str] = re.compile(
    r"(?:file:///)?(?<![A-Za-z0-9_.-])/(?:home|Users)/"
    r"(?:[A-Za-z0-9_.-]+(?: [A-Za-z0-9_.-]+)*[/\\]+)*"
    r"(?:[A-Za-z0-9_.-]+\.[A-Za-z0-9_.-]+|\.\.\.|[A-Za-z0-9_.-]+(?: [A-Za-z0-9_.-]+)?)[/\\]?",
    re.IGNORECASE,
)


def find_concrete_unc_violations(line: str) -> bool:
    """
    Checks if a line contains concrete UNC or network share paths,
    excluding generic placeholder hosts (server, host, example, placeholder, localhost, 127.0.0.1).
    """
    for m in re.finditer(r"\\\\([A-Za-z0-9_.-]+)\\[A-Za-z0-9_.$]+", line):
        if m.group(1).lower() not in GENERIC_UNC_HOSTS:
            return True
    for m in re.finditer(r"(?<![A-Za-z0-9+.-]:)//([A-Za-z0-9_.-]+)/[A-Za-z0-9_.$]+", line):
        if m.group(1).lower() not in GENERIC_UNC_HOSTS:
            return True
    return False


def redact_unc_paths(text: str) -> str:
    """
    Redacts concrete UNC and network share paths (e.g. \\\\nas01\\share, //server02/share),
    while preserving generic placeholder hosts (server, host, example, placeholder, localhost, 127.0.0.1).
    """
    def _replace_unc(m: re.Match) -> str:
        full = m.group(0)
        host = m.group(1).lower()
        if host in GENERIC_UNC_HOSTS:
            return full
        return "[REDACTED_PATH]"

    # Backslash UNC: \\host\share[\folder...][\file]
    text = re.sub(
        r"\\\\([A-Za-z0-9_.-]+)\\[A-Za-z0-9_.$]+(?:[/\\](?:[A-Za-z0-9_.-]+(?: [A-Za-z0-9_.-]+)*(?=[/\\])|[A-Za-z0-9_.-]+\.[A-Za-z0-9_.-]+|\.\.\.|[A-Za-z0-9_.-]+))*[/\\]?",
        _replace_unc,
        text,
    )
    # Forward slash UNC: //host/share[/folder...][/file]
    text = re.sub(
        r"(?<![A-Za-z0-9+.-]:)//([A-Za-z0-9_.-]+)/[A-Za-z0-9_.$]+(?:/(?:[A-Za-z0-9_.-]+(?: [A-Za-z0-9_.-]+)*(?=/)|[A-Za-z0-9_.-]+\.[A-Za-z0-9_.-]+|\.\.\.|[A-Za-z0-9_.-]+))*[/\\]?",
        _replace_unc,
        text,
    )
    return text

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

    # 1. Redact quoted/bracketed personal paths first
    def _replace_quoted(m: re.Match) -> str:
        return m.group("open") + "[REDACTED_PATH]" + m.group("close")

    clean = QUOTED_HOME_PATH_PATTERN.sub(_replace_quoted, clean)

    # 2. Redact unquoted personal paths
    clean = UNQUOTED_WINDOWS_HOME_PATTERN.sub("[REDACTED_PATH]", clean)
    clean = UNQUOTED_WSL_HOME_PATTERN.sub("[REDACTED_PATH]", clean)
    clean = UNQUOTED_POSIX_HOME_PATTERN.sub("[REDACTED_PATH]", clean)

    # 3. Redact concrete UNC / network paths
    clean = redact_unc_paths(clean)

    # 4. Redact tokens
    clean = CREDENTIAL_TOKEN_PATTERN.sub("[REDACTED_TOKEN]", clean)

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
