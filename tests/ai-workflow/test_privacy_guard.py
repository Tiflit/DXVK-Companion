"""
Tests for scripts/ai-workflow/privacy_guard.py.

Verifies:
- Dynamic guarded import execution with zero network, subprocess, environment discovery, or file writes.
- Source-level standard-library dependency inspection.
- Detection and redaction of Windows, WSL, and POSIX personal paths (Unicode, multiword, apostrophes, slashes, prefixes).
- Complete unique suffix token absence across redactions, diagnostics, and previews.
- Exact bare generic documentation UNC template preservation vs. concrete host/share/suffix redaction.
- Complete credential and query string redaction (RFC 6750 Bearer tokens, embedded GitHub tokens, complex queries).
- Preservation of safe repository-relative paths, generic dev paths, program paths, and placeholders.
- Display truncation occurring strictly after privacy redaction.
- Safe diagnostic, diff line, and backup display formatting.

Governed by docs/ai/PRIVACY-HANDLING.md and Issue #184.
"""

from __future__ import annotations

import builtins
import importlib
import inspect
import os
import socket
import subprocess
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
    GENERIC_UNC_TEMPLATES,
    PrivacyViolation,
    PrivacyViolationError,
    format_safe_backup_display,
    format_safe_filename_diagnostic,
    is_generic_unc_template,
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

    def test_source_inspection_no_forbidden_module_names(self):
        """Source-level check ensuring subprocess, socket, urllib, etc. are never imported in privacy_guard source."""
        source = Path(privacy_guard.__file__).read_text(encoding="utf-8")
        forbidden = ["subprocess", "import os", "from os", "socket", "urllib", "requests", "http.client"]
        for term in forbidden:
            self.assertNotIn(term, source, f"Forbidden dependency or call found in source: {term}")

    def test_guarded_import_verification_zero_side_effects(self):
        """
        Meaningful dynamic execution-boundary test:
        Instruments network, subprocess, environment discovery, and file writes
        during importlib.reload(privacy_guard) to verify zero execution side effects.
        """
        network_called = []
        subprocess_called = []
        identity_env_probed = []
        file_writes = []

        orig_socket = socket.socket
        orig_popen = subprocess.Popen
        orig_environ_get = os.environ.get
        orig_open = builtins.open

        def fake_socket(*args, **kwargs):
            network_called.append(args)
            raise AssertionError("Network socket creation attempted during privacy_guard import")

        def fake_popen(*args, **kwargs):
            subprocess_called.append(args)
            raise AssertionError("Subprocess spawning attempted during privacy_guard import")

        def guarded_env_get(key, default=None):
            if str(key).upper() in ("USER", "USERNAME", "HOME", "USERPROFILE", "LOGNAME"):
                identity_env_probed.append(key)
                raise AssertionError(f"User identity environment variable '{key}' probed during privacy_guard import")
            return orig_environ_get(key, default)

        def guarded_open(file, mode="r", *args, **kwargs):
            if any(m in mode for m in ("w", "a", "x", "+")):
                file_writes.append(str(file))
                raise AssertionError(f"File write attempted to '{file}' during privacy_guard import")
            return orig_open(file, mode, *args, **kwargs)

        try:
            socket.socket = fake_socket
            subprocess.Popen = fake_popen
            os.environ.get = guarded_env_get
            builtins.open = guarded_open

            # Reload privacy_guard within the instrumented boundary
            reloaded = importlib.reload(privacy_guard)
            self.assertIsNotNone(reloaded)

            # Assert zero boundary violations
            self.assertEqual(network_called, [], "Network calls were attempted during import")
            self.assertEqual(subprocess_called, [], "Subprocesses were spawned during import")
            self.assertEqual(identity_env_probed, [], "Identity environment variables were probed during import")
            self.assertEqual(file_writes, [], "Filesystem writes were attempted during import")
        finally:
            socket.socket = orig_socket
            subprocess.Popen = orig_popen
            os.environ.get = orig_environ_get
            builtins.open = orig_open


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

    def test_detects_paths_with_unicode_multiword_and_apostrophes(self):
        """Detects personal paths containing Unicode, multiword names, and apostrophes (R1)."""
        samples = [
            r"C:\Users\Éxample\PrivateSuffix",
            r"/home/Éxample/PrivateSuffix",
            r"/mnt/c/Users/Éxample/PrivateSuffix",
            r"C:\Users\Example Middle Person\Projects\DXVK\file.txt",
            r"C:\Users\Example Middle Person",
            r"/home/Example Middle Person/script.sh",
            r"C:\Users\O'Connor\Desktop\app.log",
            r"/home/O'Connor/debug.log",
            r"C:\Documents and Settings\René\app.log",
        ]
        for s in samples:
            with self.subTest(sample=s):
                violations = scan_for_privacy_violations(s)
                self.assertGreaterEqual(len(violations), 1)
                self.assertTrue(is_privacy_sensitive_text(s))

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

    def test_detects_concrete_unc_paths_including_under_example_hosts(self):
        """Detects concrete UNC host, private share, and suffix paths (R2)."""
        samples = [
            r"\\nas01\share\backup\file.zip",
            r"\\nas01\Team-Share\private_tool",
            r"\\nas01\Éxample-Share\private_tool",
            r"\\fileserver02\projects\repo",
            r"\\corp.internal\storage\data",
            "//server02/share/data",
            "//backup-box/c$/volume",
            r"\\server\share\repo\private_tool",
            r"\\server\Team-Share",
            r"\\localhost\my_share\data",
            r"\\localhost\share\repo\private_tool",
            r"\\127.0.0.1\c$\secret",
            "//localhost/share/repo/private_tool",
        ]
        for s in samples:
            with self.subTest(sample=s):
                violations = scan_for_privacy_violations(s)
                self.assertGreaterEqual(len(violations), 1, f"Concrete UNC was not detected: {s}")
                self.assertEqual(violations[0][1], "UNC / network path")
                self.assertTrue(is_privacy_sensitive_text(s))

    def test_preserves_bare_generic_unc_placeholders_only(self):
        """Preserves only exact bare documentation placeholder templates without private shares or suffixes (R2)."""
        exact_templates = [
            r"\\server\share",
            r"\\host\share",
            r"\\example\share",
            r"\\placeholder\share",
            "//server/share",
            "//host/share",
            "//example/share",
            "//placeholder/share",
        ]
        for s in exact_templates:
            with self.subTest(sample=s):
                self.assertTrue(is_generic_unc_template(s), f"Template must be recognized: {s}")
                violations = scan_for_privacy_violations(s)
                self.assertEqual(len(violations), 0, f"Generic UNC template unexpectedly flagged: {s}")
                self.assertFalse(is_privacy_sensitive_text(s))
                self.assertEqual(redact_unc_paths(s), s)

    def test_detects_credentials_and_tokens_including_rfc6750_and_embedded(self):
        """Detects credentials, RFC 6750 Bearer tokens with padding, and embedded tokens (R3)."""
        tokens = [
            "ghp_1234567890abcdef1234567890abcdef",
            "prefix_ghp_1234567890abcdef1234567890abcdef",
            "arg_github_pat_11AAAAAAA0123456789_abcdefghijklmnopqrstuvwxyz",
            "gho_1234567890abcdef1234567890abcdef",
            "ghu_1234567890abcdef1234567890abcdef",
            "ghs_1234567890abcdef1234567890abcdef",
            "ghr_1234567890abcdef1234567890abcdef",
            "Bearer ABCDEFGH+PrivateSentinel/==",
            "bearer token-with~tilde+plus/slash==",
            "token: Bearer ABCDEFGH+PrivateSentinel/==",
        ]
        for t in tokens:
            with self.subTest(token=t):
                violations = scan_for_privacy_violations(f"Header: {t}")
                self.assertGreaterEqual(len(violations), 1, f"Token not detected: {t}")
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

    def test_redacts_unicode_multiword_and_apostrophe_paths_without_suffix_leakage(self):
        """Redacts Unicode, multiword names, and apostrophe paths completely without suffix leakage (R1)."""
        cases = [
            (
                r"Failed at C:\Users\Éxample\PrivateSuffix on line 12",
                "Failed at [REDACTED_PATH] on line 12",
                ["Éxample", "PrivateSuffix"],
            ),
            (
                r"Log at /home/Éxample/PrivateSuffix for test",
                "Log at [REDACTED_PATH] for test",
                ["Éxample", "PrivateSuffix"],
            ),
            (
                r"WSL at /mnt/c/Users/Éxample/PrivateSuffix done",
                "WSL at [REDACTED_PATH] done",
                ["Éxample", "PrivateSuffix"],
            ),
            (
                r"Location: C:\Users\Example Middle Person",
                "Location: [REDACTED_PATH]",
                ["Example", "Middle", "Person"],
            ),
            (
                r"Path C:\Users\Example Middle Person\Projects\DXVK\file.txt on line 5",
                "Path [REDACTED_PATH] on line 5",
                ["Example", "Middle", "Person", "file.txt"],
            ),
            (
                r"Error in C:\Users\O'Connor\Desktop\app.log: access denied",
                "Error in [REDACTED_PATH]: access denied",
                ["O'Connor", "Desktop", "app.log"],
            ),
            (
                r"Home is /home/O'Connor/debug.log",
                "Home is [REDACTED_PATH]",
                ["O'Connor", "debug.log"],
            ),
            (
                'Quoted "C:\\Users\\O\'Connor\\Desktop\\app.log" here',
                'Quoted "[REDACTED_PATH]" here',
                ["O'Connor", "Desktop", "app.log"],
            ),
            (
                r"Bracketed [C:\Users\Éxample\doc.md] here",
                "Bracketed [[REDACTED_PATH]] here",
                ["Éxample", "doc.md"],
            ),
        ]
        for input_text, expected_redacted, forbidden_tokens in cases:
            with self.subTest(input_text=input_text):
                redacted = redact_privacy_text(input_text)
                self.assertEqual(redacted, expected_redacted)
                for tok in forbidden_tokens:
                    self.assertNotIn(tok, redacted, f"Token '{tok}' leaked into redacted output!")

    def test_redacts_concrete_unc_paths_including_under_example_hosts(self):
        """Redacts concrete UNC paths while preserving exact bare documentation templates (R2)."""
        cases = [
            (
                r"Share mounted at \\nas01\share\sub\data.bin successfully",
                "Share mounted at [REDACTED_PATH] successfully",
                ["nas01", "data.bin"],
            ),
            (
                r"Tool at \\nas01\Team-Share\private_tool finished",
                "Tool at [REDACTED_PATH] finished",
                ["nas01", "Team-Share", "private_tool"],
            ),
            (
                r"Unicode share at \\nas01\Éxample-Share\private_tool ok",
                "Unicode share at [REDACTED_PATH] ok",
                ["nas01", "Éxample-Share", "private_tool"],
            ),
            (
                r"Private suffix under server at \\server\share\repo\private_tool end",
                "Private suffix under server at [REDACTED_PATH] end",
                ["repo", "private_tool"],
            ),
            (
                r"Private share under server at \\server\Team-Share end",
                "Private share under server at [REDACTED_PATH] end",
                ["Team-Share"],
            ),
            (
                r"Localhost share at \\localhost\my_share\data end",
                "Localhost share at [REDACTED_PATH] end",
                ["my_share", "data"],
            ),
            (
                r"Localhost suffix at //localhost/share/repo/private_tool end",
                "Localhost suffix at [REDACTED_PATH] end",
                ["repo", "private_tool"],
            ),
            (
                r"IP share at \\127.0.0.1\c$\secret end",
                "IP share at [REDACTED_PATH] end",
                ["c$", "secret"],
            ),
            (
                r"Exact bare template \\server\share is preserved",
                r"Exact bare template \\server\share is preserved",
                [],
            ),
            (
                "Exact bare forward template //host/share is preserved",
                "Exact bare forward template //host/share is preserved",
                [],
            ),
        ]
        for input_text, expected_redacted, forbidden_tokens in cases:
            with self.subTest(input_text=input_text):
                redacted = redact_privacy_text(input_text)
                self.assertEqual(redacted, expected_redacted)
                for tok in forbidden_tokens:
                    self.assertNotIn(tok, redacted, f"Token '{tok}' leaked into redacted output!")

    def test_redacts_credentials_tokens_and_queries_completely(self):
        """Redacts full RFC 6750 Bearer tokens, embedded GitHub tokens, and complex queries (R3)."""
        cases = [
            (
                "Token ?token=prefix.PrivateSentinel%2FPrivateTail in URL",
                "Token ?[REDACTED_QUERY] in URL",
                ["prefix", "PrivateSentinel", "PrivateTail", "%2F"],
            ),
            (
                "Link [Issues](https://api.github.com/issues?state=all&token=prefix.PrivateSentinel%2FPrivateTail#frag)",
                "Link [Issues](https://api.github.com/issues?[REDACTED_QUERY]#frag)",
                ["PrivateSentinel", "PrivateTail"],
            ),
            (
                "Auth: Bearer ABCDEFGH+PrivateSentinel/== and more",
                "Auth: [REDACTED_TOKEN] and more",
                ["ABCDEFGH", "PrivateSentinel", "/=="],
            ),
            (
                "token: Bearer token-with~tilde+plus/slash==, next",
                "token: [REDACTED_TOKEN], next",
                ["token-with", "tilde", "plus", "slash"],
            ),
            (
                "Config prefix_ghp_1234567890abcdef1234567890abcdef set",
                "Config prefix_[REDACTED_TOKEN] set",
                ["1234567890abcdef1234567890abcdef"],
            ),
            (
                "arg=github_pat_11AAAAAAA0123456789_abcdefghijklmnopqrstuvwxyz",
                "arg=[REDACTED_TOKEN]",
                ["11AAAAAAA0123456789_abcdefghijklmnopqrstuvwxyz"],
            ),
        ]
        for input_text, expected_redacted, forbidden_tokens in cases:
            with self.subTest(input_text=input_text):
                redacted = redact_privacy_text(input_text)
                self.assertEqual(redacted, expected_redacted)
                for tok in forbidden_tokens:
                    self.assertNotIn(tok, redacted, f"Token '{tok}' leaked into redacted output!")

    def test_sanitize_display_text_redacts_before_truncation(self):
        """Crucial security test: Redaction must occur before display truncation to prevent partial username leaks."""
        long_prefix = "A" * 90
        # A long path that would be cut right inside the username if truncated before redaction
        sensitive_string = f"{long_prefix} C:\\Users\\PrivateUserIdentifierLongName\\Documents\\file.txt"
        sanitized = sanitize_display_text(sensitive_string, max_len=100)
        self.assertNotIn("PrivateUser", sanitized)
        self.assertNotIn("Identifier", sanitized)
        self.assertLessEqual(len(sanitized), 100)
        self.assertTrue(sanitized.endswith("..."))

    def test_sanitize_diff_line_preserves_prefix(self):
        """Preserves diff prefixes (+, -, space) while redacting payloads."""
        add_line = "+ C:\\Users\\Éxample\\file.cs"
        sub_line = "- /home/Éxample/file.cs"
        ctx_line = "  token: Bearer ABCDEFGH+PrivateSentinel/=="
        self.assertEqual(sanitize_diff_line(add_line), "+ [REDACTED_PATH]")
        self.assertEqual(sanitize_diff_line(sub_line), "- [REDACTED_PATH]")
        self.assertEqual(sanitize_diff_line(ctx_line), "  token: [REDACTED_TOKEN]")

    def test_sanitize_diagnostic_suppresses_sensitive_data(self):
        """Sanitizes diagnostic messages cleanly without leaking Unicode names or tokens."""
        msg = "Error reading C:\\Users\\Éxample\\secret.key: file not found"
        sanitized = sanitize_diagnostic(msg)
        self.assertNotIn("Éxample", sanitized)
        self.assertNotIn("secret.key", sanitized)
        self.assertEqual(sanitized, "Error reading [REDACTED_PATH]: file not found")

    def test_format_safe_filename_diagnostic(self):
        """Replaces sensitive filenames with <redacted-filename>."""
        sensitive_path = Path("C:/Users/Éxample/secret_report.md")
        safe_path = Path("reports/summary.md")
        self.assertEqual(format_safe_filename_diagnostic(sensitive_path), "<redacted-filename>")
        self.assertEqual(format_safe_filename_diagnostic(safe_path), "summary.md")

    def test_format_safe_backup_display(self):
        """Formats backup confirmation safely using fixed prefix and basename."""
        backup_path = Path("C:/Users/Éxample/.backups/pr_body_backup_20261019.md")
        display = format_safe_backup_display(backup_path)
        self.assertNotIn("Éxample", display)
        self.assertEqual(display, "[REDACTED_PATH]/pr_body_backup_20261019.md")


if __name__ == "__main__":
    unittest.main()
