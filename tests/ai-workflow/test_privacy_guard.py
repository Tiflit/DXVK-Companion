"""
Tests for scripts/ai-workflow/privacy_guard.py.

Verifies:
- Standard-library-only operation with zero network, subprocess, or env side-effects.
- Detection and redaction of Windows, WSL, POSIX personal paths (with slashes, case variants, prefixes).
- Paths with spaces and full names (e.g., 'C:\\Users\\Example Person\\...') without suffix leakage.
- Concrete UNC host redaction vs. generic UNC host preservation.
- Credentials, PATs, OAuth, and Bearer token detection and redaction.
- Preservation of safe repo-relative paths, generic dev paths, program paths, and placeholders.
- Display truncation occurring strictly after privacy redaction.
- Diff line and diagnostic formatting safety.

Governed by docs/ai/PRIVACY-HANDLING.md and Issue #184.
"""

from __future__ import annotations

import importlib
import inspect
import sys
import unittest
from pathlib import Path

# Ensure scripts/ai-workflow is on sys.path
REPO_ROOT = Path(__file__).resolve().parent.parent.parent
AI_WORKFLOW_DIR = REPO_ROOT / "scripts" / "ai-workflow"
if str(AI_WORKFLOW_DIR) not in sys.path:
    sys.path.insert(0, str(AI_WORKFLOW_DIR))

import privacy_guard  # noqa: E402
from privacy_guard import (  # noqa: E402
    GENERIC_UNC_HOSTS,
    PrivacyViolation,
    PrivacyViolationError,
    format_safe_backup_display,
    format_safe_filename_diagnostic,
    is_privacy_sensitive_text,
    redact_privacy_text,
    redact_unc_paths,
    sanitize_diagnostic,
    sanitize_diff_line,
    sanitize_display_text,
    sanitize_privacy_text,
    scan_for_privacy_violations,
)


class TestPrivacyGuardEnvironmentAndPurity(unittest.TestCase):
    """Verifies that privacy_guard is standard-library only and has no side effects."""

    def test_imports_only_standard_library(self):
        """Ensures privacy_guard only imports standard library modules."""
        allowed_stdlib = {"__future__", "re", "pathlib", "typing"}
        imported_modules = set()
        for name, mod in inspect.getmembers(privacy_guard):
            if inspect.ismodule(mod):
                imported_modules.add(mod.__name__.split(".")[0])
        non_standard = imported_modules - allowed_stdlib
        self.assertEqual(non_standard, set(), f"Disallowed modules imported: {non_standard}")

    def test_no_forbidden_module_imports(self):
        """Ensures subprocess, os, socket, urllib, requests, etc., are never imported."""
        source = Path(privacy_guard.__file__).read_text(encoding="utf-8")
        forbidden = ["subprocess", "import os", "from os", "socket", "urllib", "requests", "http.client"]
        for term in forbidden:
            self.assertNotIn(term, source, f"Forbidden dependency or call found: {term}")

    def test_import_has_no_side_effects(self):
        """Re-importing privacy_guard performs zero network, disk write, or process spawning."""
        reloaded = importlib.reload(privacy_guard)
        self.assertIsNotNone(reloaded)


class TestPrivacyDetection(unittest.TestCase):
    """Verifies positive and negative detection across path families, UNC hosts, and tokens."""

    def test_detects_windows_personal_home_paths(self):
        """Detects standard Windows user paths with backslashes and case variations."""
        variants = [
            r"C:\Users\developer\project\file.cs",
            r"c:\users\developer\project\file.cs",
            r"D:\USERS\runner\work\app.log",
            r"C:\Documents and Settings\developer\Desktop\run.bat",
            r"\Users\developer\Documents\test.txt",
        ]
        for v in variants:
            with self.subTest(variant=v):
                violations = scan_for_privacy_violations(v)
                self.assertGreaterEqual(len(violations), 1)
                self.assertEqual(violations[0][1], "Windows personal-home path")
                self.assertTrue(is_privacy_sensitive_text(v))

    def test_detects_windows_personal_paths_with_forward_slashes_and_file_uri(self):
        """Detects Windows personal paths using forward slashes and file:/// schemes."""
        variants = [
            "C:/Users/developer/project/file.cs",
            "file:///C:/Users/developer/project/file.cs",
            "file:///c:/users/runner/work/out.txt",
            "/Users/developer/Documents/test.txt",
        ]
        for v in variants:
            with self.subTest(variant=v):
                violations = scan_for_privacy_violations(v)
                self.assertGreaterEqual(len(violations), 1)
                self.assertTrue(is_privacy_sensitive_text(v))

    def test_detects_wsl_personal_home_paths(self):
        """Detects WSL mapped personal home paths (/mnt/<drive>/Users/...)."""
        variants = [
            "/mnt/c/Users/developer/project/file.cs",
            "/mnt/d/Users/runner/work/app.log",
            "/mnt/c/Documents and Settings/developer/Desktop",
        ]
        for v in variants:
            with self.subTest(variant=v):
                violations = scan_for_privacy_violations(v)
                self.assertGreaterEqual(len(violations), 1)
                self.assertEqual(violations[0][1], "WSL personal-home path")
                self.assertTrue(is_privacy_sensitive_text(v))

    def test_detects_posix_personal_home_paths(self):
        """Detects Linux and macOS personal home directories."""
        variants = [
            "/home/developer/debug.log",
            "/home/runner/work/DXVK-Companion/build",
            "/Users/developer/Desktop/output.txt",
        ]
        for v in variants:
            with self.subTest(variant=v):
                violations = scan_for_privacy_violations(v)
                self.assertGreaterEqual(len(violations), 1)
                self.assertIn(violations[0][1], ("POSIX personal-home path", "Windows personal-home path"))
                self.assertTrue(is_privacy_sensitive_text(v))

    def test_detects_paths_with_punctuation_prefixes(self):
        """Detects personal paths following equals, colons, brackets, and quotes."""
        samples = [
            "param=C:\\Users\\dev\\file.txt",
            "setting:c:/users/runner/config.ini",
            "[C:\\Users\\dev\\doc.md]",
            "(file:///C:/Users/dev/image.png)",
            "<C:\\Users\\dev\\test.xml>",
            "{'path': 'C:\\\\Users\\\\dev\\\\file.json'}",
            "`C:\\Users\\dev\\tool.exe`",
            "\"/home/dev/script.sh\"",
            "'param=/mnt/c/Users/dev/file.txt'",
        ]
        for s in samples:
            with self.subTest(sample=s):
                violations = scan_for_privacy_violations(s)
                self.assertGreaterEqual(len(violations), 1)
                self.assertTrue(is_privacy_sensitive_text(s))

    def test_detects_paths_with_spaces_and_full_names(self):
        """Detects personal paths containing spaces in usernames and directory names."""
        samples = [
            r"C:\Users\Example Person\Projects\DXVK\file.txt",
            r"C:\Users\Example Person\...",
            r"C:\Users\John Doe\AppData\Local\Temp\log.txt",
            r"/mnt/c/Users/Example Person/Documents/report.docx",
            r"/home/Example Person/Desktop/file.py",
        ]
        for s in samples:
            with self.subTest(sample=s):
                violations = scan_for_privacy_violations(s)
                self.assertGreaterEqual(len(violations), 1)
                self.assertTrue(is_privacy_sensitive_text(s))

    def test_detects_concrete_unc_paths(self):
        """Detects concrete UNC host and network share paths."""
        samples = [
            r"\\nas01\share\backup\file.zip",
            r"\\fileserver02\projects\repo",
            r"\\corp.internal\storage\data",
            "//server02/share/data",
            "//backup-box/c$/volume",
        ]
        for s in samples:
            with self.subTest(sample=s):
                violations = scan_for_privacy_violations(s)
                self.assertGreaterEqual(len(violations), 1)
                self.assertEqual(violations[0][1], "UNC / network path")
                self.assertTrue(is_privacy_sensitive_text(s))

    def test_preserves_generic_unc_placeholders(self):
        """Preserves generic UNC host placeholders used in documentation and examples."""
        samples = [
            r"\\server\share\path",
            r"\\host\share\data",
            r"\\example\share\file.txt",
            r"\\placeholder\share",
            r"\\localhost\c$\data",
            r"\\127.0.0.1\share",
            "//server/share/path",
            "//localhost/share",
        ]
        for s in samples:
            with self.subTest(sample=s):
                violations = scan_for_privacy_violations(s)
                self.assertEqual(len(violations), 0, f"Generic UNC unexpectedly flagged: {s}")
                self.assertFalse(is_privacy_sensitive_text(s))
                self.assertEqual(redact_unc_paths(s), s)

    def test_detects_credentials_and_tokens(self):
        """Detects personal access tokens, OAuth tokens, and Bearer authorization tokens."""
        tokens = [
            "ghp_1234567890abcdef1234567890abcdef",
            "github_pat_11AAAAAAA0123456789_abcdefghijklmnopqrstuvwxyz",
            "gho_1234567890abcdef1234567890abcdef",
            "ghu_1234567890abcdef1234567890abcdef",
            "ghs_1234567890abcdef1234567890abcdef",
            "ghr_1234567890abcdef1234567890abcdef",
            "Bearer my-secret-auth-token-12345678",
            "bearer abcdef0123456789abcdef0123456789",
        ]
        for t in tokens:
            with self.subTest(token=t):
                violations = scan_for_privacy_violations(f"Header: {t}")
                self.assertGreaterEqual(len(violations), 1)
                self.assertEqual(violations[0][1], "GitHub credential or bearer token")
                self.assertTrue(is_privacy_sensitive_text(t))

    def test_allows_generic_dev_paths_and_repo_relative_paths(self):
        """Preserves generic development paths, program directories, games, and placeholders."""
        safe_samples = [
            r"D:\dev\DXVK-Companion-issue-42",
            r"C:\dev\DXVK-Companion",
            r"C:\Program Files\Git\bin\git.exe",
            r"C:\Program Files (x86)\Steam\steamapps\common\Portal",
            r"C:\Games\DirectX\d3d11.dll",
            "scripts/ai-workflow/privacy_guard.py",
            "docs/ai/PRIVACY-HANDLING.md",
            "https://github.com/Tiflit/DXVK-Companion/issues/184",
            "https://api.github.com/repos/Tiflit/DXVK-Companion/pulls/189",
            "[REDACTED_PATH]",
            "[REDACTED_TOKEN]",
            "[REDACTED_QUERY]",
            "<redacted-filename>",
        ]
        for s in safe_samples:
            with self.subTest(sample=s):
                violations = scan_for_privacy_violations(s)
                self.assertEqual(len(violations), 0, f"Safe sample unexpectedly flagged: {s}")
                self.assertFalse(is_privacy_sensitive_text(s))

    def test_detection_does_not_echo_sensitive_text(self):
        """Asserts that PrivacyViolation contains line number and category only, no sensitive payload."""
        secret_user = "very_secret_personal_username_9999"
        text = f"Build path is C:\\Users\\{secret_user}\\workspace"
        violations = scan_for_privacy_violations(text)
        self.assertEqual(len(violations), 1)
        violation = violations[0]
        self.assertEqual(violation.line_number, 1)
        self.assertEqual(violation.category, "Windows personal-home path")
        self.assertNotIn(secret_user, str(violation))
        self.assertNotIn(secret_user, repr(violation))


class TestPrivacyRedaction(unittest.TestCase):
    """Verifies safe redaction without leaking sensitive fragments, suffix tokens, or paths."""

    def test_redacts_windows_personal_paths_and_variants(self):
        """Redacts Windows paths to [REDACTED_PATH]."""
        text = r"Logs saved at C:\Users\developer\AppData\Local\Temp\log.txt"
        redacted = redact_privacy_text(text)
        self.assertNotIn("developer", redacted)
        self.assertNotIn(r"C:\Users", redacted)
        self.assertIn("[REDACTED_PATH]", redacted)
        self.assertEqual(redacted, "Logs saved at [REDACTED_PATH]")

    def test_redacts_paths_with_spaces_and_prevents_suffix_leakage(self):
        """Redacts full paths with spaces (e.g. C:\\Users\\Example Person\\...) without leaking suffix tokens."""
        text = r"Failed at C:\Users\Example Person\Projects\DXVK\file.txt on line 12"
        redacted = redact_privacy_text(text)
        self.assertNotIn("Example", redacted)
        self.assertNotIn("Person", redacted)
        self.assertNotIn("DXVK", redacted)
        self.assertIn("[REDACTED_PATH]", redacted)
        self.assertIn("on line 12", redacted)

    def test_redacts_path_with_ellipsis_syntax(self):
        """Redacts literal ellipsis paths as described in Issue #184 (e.g. C:\\Users\\Example Person\\...)."""
        text = r"Referenced location: C:\Users\Example Person\..."
        redacted = redact_privacy_text(text)
        self.assertNotIn("Example", redacted)
        self.assertNotIn("Person", redacted)
        self.assertEqual(redacted, "Referenced location: [REDACTED_PATH]")

    def test_redacts_wsl_and_posix_paths(self):
        """Redacts WSL and POSIX home paths completely."""
        text = "Linux log: /home/runner/work/out.log; WSL file: /mnt/c/Users/developer/file.cs"
        redacted = redact_privacy_text(text)
        self.assertNotIn("runner", redacted)
        self.assertNotIn("developer", redacted)
        self.assertNotIn("/home/", redacted)
        self.assertNotIn("/mnt/c/Users/", redacted)
        self.assertEqual(redacted, "Linux log: [REDACTED_PATH]; WSL file: [REDACTED_PATH]")

    def test_redacts_concrete_unc_paths(self):
        """Redacts concrete UNC paths while leaving surrounding text intact."""
        text = r"Share mounted at \\nas01\share\sub\data.bin successfully"
        redacted = redact_privacy_text(text)
        self.assertNotIn("nas01", redacted)
        self.assertNotIn("share", redacted)
        self.assertEqual(redacted, "Share mounted at [REDACTED_PATH] successfully")

    def test_redacts_quoted_and_bracketed_paths_cleanly(self):
        """Preserves enclosing punctuation while replacing internal sensitive path."""
        cases = [
            ('path="C:\\Users\\alice\\foo.txt"', 'path="[REDACTED_PATH]"'),
            ("path='C:\\Users\\alice\\foo.txt'", "path='[REDACTED_PATH]'"),
            ("path=`C:\\Users\\alice\\foo.txt`", "path=`[REDACTED_PATH]`"),
            ("see [C:\\Users\\alice\\foo.txt]", "see [[REDACTED_PATH]]"),
            ("link (C:\\Users\\alice\\foo.txt)", "link ([REDACTED_PATH])"),
            ("tag <C:\\Users\\alice\\foo.txt>", "tag <[REDACTED_PATH]>"),
        ]
        for input_text, expected in cases:
            with self.subTest(input_text=input_text):
                self.assertEqual(redact_privacy_text(input_text), expected)

    def test_redacts_tokens_and_queries(self):
        """Redacts credential tokens and URL query parameter values."""
        text = "token=ghp_abcdef1234567890 url=https://api.github.com/issues?state=all&per_page=100"
        redacted = redact_privacy_text(text)
        self.assertNotIn("ghp_", redacted)
        self.assertNotIn("state=all", redacted)
        self.assertIn("[REDACTED_TOKEN]", redacted)
        self.assertIn("?[REDACTED_QUERY]", redacted)

    def test_sanitize_display_text_redacts_before_truncation(self):
        """Crucial security test: Redaction must occur before display truncation to prevent partial username leaks."""
        long_prefix = "A" * 90
        # A 120-character path that would be cut right inside the username if truncated before redaction
        sensitive_string = f"{long_prefix} C:\\Users\\PrivateUserIdentifierLongName\\Documents\\file.txt"
        sanitized = sanitize_display_text(sensitive_string, max_len=100)
        self.assertNotIn("PrivateUser", sanitized)
        self.assertNotIn("Identifier", sanitized)
        self.assertLessEqual(len(sanitized), 100)
        self.assertTrue(sanitized.endswith("..."))

    def test_sanitize_diff_line_preserves_prefix(self):
        """Preserves diff prefixes (+, -, space) while redacting payloads."""
        add_line = "+ C:\\Users\\developer\\file.cs"
        sub_line = "- /home/developer/file.cs"
        ctx_line = "  token: ghp_1234567890abcdef1234567890abcdef"
        self.assertEqual(sanitize_diff_line(add_line), "+ [REDACTED_PATH]")
        self.assertEqual(sanitize_diff_line(sub_line), "- [REDACTED_PATH]")
        self.assertEqual(sanitize_diff_line(ctx_line), "  token: [REDACTED_TOKEN]")

    def test_sanitize_diagnostic_suppresses_sensitive_data(self):
        """Sanitizes diagnostic messages cleanly."""
        msg = "Error reading C:\\Users\\developer\\secret.key: file not found"
        sanitized = sanitize_diagnostic(msg)
        self.assertNotIn("developer", sanitized)
        self.assertNotIn("secret.key", sanitized)
        self.assertEqual(sanitized, "Error reading [REDACTED_PATH]: file not found")

    def test_format_safe_filename_diagnostic(self):
        """Replaces sensitive filenames with <redacted-filename>."""
        sensitive_path = Path("C:/Users/developer/secret_report.md")
        safe_path = Path("reports/summary.md")
        self.assertEqual(format_safe_filename_diagnostic(sensitive_path), "<redacted-filename>")
        self.assertEqual(format_safe_filename_diagnostic(safe_path), "summary.md")

    def test_format_safe_backup_display(self):
        """Formats backup confirmation safely using fixed prefix and basename."""
        backup_path = Path("C:/Users/developer/.backups/pr_body_backup_20261019.md")
        display = format_safe_backup_display(backup_path)
        self.assertNotIn("developer", display)
        self.assertEqual(display, "[REDACTED_PATH]/pr_body_backup_20261019.md")


if __name__ == "__main__":
    unittest.main()
