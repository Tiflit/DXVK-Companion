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
import io
import os
import socket
import subprocess
import sys
import unittest
from contextlib import contextmanager
from pathlib import Path
from typing import Any

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


IDENTITY_ENV_KEYS = {"USER", "USERNAME", "HOME", "USERPROFILE", "LOGNAME"}


def is_permitted_module_file(file_obj: Any) -> bool:
    """Permits only privacy_guard.py source and bytecode loading during reload."""
    name = getattr(file_obj, "name", str(file_obj))
    norm = str(name).replace("\\", "/").lower()
    return (
        norm.endswith("privacy_guard.py")
        or "privacy_guard.cpython" in norm
        or norm.endswith("privacy_guard.pyc")
    )


@contextmanager
def guarded_execution_boundary(
    unauthorized_reads: list,
    file_writes: list,
    identity_env_probed: list,
    network_called: list,
    subprocess_called: list,
):
    """
    Instruments network, subprocess, identity environment access, and file I/O entry points.
    Guards builtins.open, io.open, Path.open, Path.read_text, as well as direct dictionary
    mapping access (os.environ['USER'], os.getenv('USER'), 'USER' in os.environ).
    """
    orig_socket = socket.socket
    orig_popen = subprocess.Popen
    orig_builtins_open = builtins.open
    orig_io_open = io.open
    orig_path_open = Path.open
    orig_path_read_text = Path.read_text

    environ_cls = type(os.environ)
    orig_environ_getitem = environ_cls.__getitem__
    orig_environ_get = environ_cls.get
    orig_environ_contains = environ_cls.__contains__
    orig_getenv = os.getenv

    def fake_socket(*args, **kwargs):
        network_called.append(args)
        raise AssertionError("Network socket creation attempted during privacy_guard import")

    def fake_popen(*args, **kwargs):
        subprocess_called.append(args)
        raise AssertionError("Subprocess spawning attempted during privacy_guard import")

    def guarded_env_check(key: Any):
        if str(key).upper() in IDENTITY_ENV_KEYS:
            identity_env_probed.append(str(key))
            raise AssertionError(f"User identity environment variable '{key}' probed during privacy_guard import")

    def guarded_environ_getitem(self, key):
        guarded_env_check(key)
        return orig_environ_getitem(self, key)

    def guarded_environ_get(self, key, default=None):
        guarded_env_check(key)
        return orig_environ_get(self, key, default)

    def guarded_environ_contains(self, key):
        guarded_env_check(key)
        return orig_environ_contains(self, key)

    def guarded_getenv(key, default=None):
        guarded_env_check(key)
        return orig_getenv(key, default)

    def guarded_open(file, mode="r", *args, **kwargs):
        if any(m in mode for m in ("w", "a", "x", "+")):
            file_writes.append(str(file))
            raise AssertionError(f"File write attempted to '{file}' during privacy_guard import")
        if not is_permitted_module_file(file):
            unauthorized_reads.append(str(file))
            raise AssertionError(f"Unauthorized file read attempted on '{file}' during privacy_guard import")
        return orig_builtins_open(file, mode, *args, **kwargs)

    def guarded_path_open(self, mode="r", *args, **kwargs):
        if any(m in mode for m in ("w", "a", "x", "+")):
            file_writes.append(str(self))
            raise AssertionError(f"File write attempted to '{self}' during privacy_guard import")
        if not is_permitted_module_file(self):
            unauthorized_reads.append(str(self))
            raise AssertionError(f"Unauthorized Path.open attempted on '{self}' during privacy_guard import")
        return orig_path_open(self, mode, *args, **kwargs)

    def guarded_path_read_text(self, *args, **kwargs):
        if not is_permitted_module_file(self):
            unauthorized_reads.append(str(self))
            raise AssertionError(f"Unauthorized Path.read_text attempted on '{self}' during privacy_guard import")
        return orig_path_read_text(self, *args, **kwargs)

    try:
        socket.socket = fake_socket
        subprocess.Popen = fake_popen
        builtins.open = guarded_open
        io.open = guarded_open
        Path.open = guarded_path_open
        Path.read_text = guarded_path_read_text

        environ_cls.__getitem__ = guarded_environ_getitem
        environ_cls.get = guarded_environ_get
        environ_cls.__contains__ = guarded_environ_contains
        os.getenv = guarded_getenv

        yield
    finally:
        socket.socket = orig_socket
        subprocess.Popen = orig_popen
        builtins.open = orig_builtins_open
        io.open = orig_io_open
        Path.open = orig_path_open
        Path.read_text = orig_path_read_text

        environ_cls.__getitem__ = orig_environ_getitem
        environ_cls.get = orig_environ_get
        environ_cls.__contains__ = orig_environ_contains
        os.getenv = orig_getenv


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
        Instruments network, subprocess, environment discovery, file reads, and file writes
        during importlib.reload(privacy_guard) to verify zero execution side effects (R4).
        """
        network_called = []
        subprocess_called = []
        identity_env_probed = []
        file_writes = []
        unauthorized_reads = []

        with guarded_execution_boundary(
            unauthorized_reads,
            file_writes,
            identity_env_probed,
            network_called,
            subprocess_called,
        ):
            # Reload privacy_guard within the instrumented boundary
            reloaded = importlib.reload(privacy_guard)
            self.assertIsNotNone(reloaded)

        # Assert zero boundary violations
        self.assertEqual(network_called, [], "Network calls were attempted during import")
        self.assertEqual(subprocess_called, [], "Subprocesses were spawned during import")
        self.assertEqual(identity_env_probed, [], "Identity environment variables were probed during import")
        self.assertEqual(file_writes, [], "Filesystem writes were attempted during import")
        self.assertEqual(unauthorized_reads, [], "Unauthorized file reads were attempted during import")

    def test_guarded_boundary_intercepts_seeded_forbidden_reads_and_env_probes(self):
        """
        Verifies that the execution boundary actively traps and blocks:
        - builtins.open reads to non-module files
        - io.open reads to non-module files
        - Path.read_text() reads to non-module files
        - Path.open() reads to non-module files
        - Direct dictionary mapping access: os.environ["USER"]
        - Function call access: os.getenv("USER")
        - Membership testing: "USER" in os.environ
        - Mapping get: os.environ.get("USER")
        - Filesystem writes in any mode
        While permitting legitimate reads of privacy_guard.py itself (R4).
        """
        network_called = []
        subprocess_called = []
        identity_env_probed = []
        file_writes = []
        unauthorized_reads = []

        with guarded_execution_boundary(
            unauthorized_reads,
            file_writes,
            identity_env_probed,
            network_called,
            subprocess_called,
        ):
            # 1. Traps builtins.open read to unauthorized file
            with self.assertRaises(AssertionError):
                builtins.open("forbidden_secret.txt", "r")
            self.assertIn("forbidden_secret.txt", unauthorized_reads)

            # 2. Traps io.open read to unauthorized file
            with self.assertRaises(AssertionError):
                io.open("forbidden_io.txt", "r")
            self.assertIn("forbidden_io.txt", unauthorized_reads)

            # 3. Traps Path.read_text
            with self.assertRaises(AssertionError):
                Path("forbidden_path.txt").read_text()
            self.assertIn("forbidden_path.txt", unauthorized_reads)

            # 4. Traps Path.open read
            with self.assertRaises(AssertionError):
                Path("forbidden_path_open.txt").open("r")
            self.assertIn("forbidden_path_open.txt", unauthorized_reads)

            # 5. Traps direct dict mapping access os.environ["USER"]
            with self.assertRaises(AssertionError):
                _ = os.environ["USER"]
            self.assertIn("USER", identity_env_probed)

            # 6. Traps os.getenv("USER")
            with self.assertRaises(AssertionError):
                _ = os.getenv("USER")
            self.assertIn("USER", identity_env_probed)

            # 7. Traps membership test "USER" in os.environ
            with self.assertRaises(AssertionError):
                _ = "USER" in os.environ
            self.assertIn("USER", identity_env_probed)

            # 8. Traps os.environ.get("USER")
            with self.assertRaises(AssertionError):
                _ = os.environ.get("USER")
            self.assertIn("USER", identity_env_probed)

            # 9. Traps file write
            with self.assertRaises(AssertionError):
                builtins.open("forbidden_write.txt", "w")
            self.assertIn("forbidden_write.txt", file_writes)

            # 10. Permitted module file read succeeds without error
            pg_path = Path(privacy_guard.__file__)
            content = builtins.open(str(pg_path), "r", encoding="utf-8").read()
            self.assertIn("Standard-library-only", content)


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
        """Detects personal paths containing Unicode, multiword names, punctuation, and apostrophes (R1)."""
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
            r"C:\Users\Example\Private Surname",
            r"/home/Example/Private Surname",
            r"/mnt/c/Users/Example/Private Surname",
            r"C:\Users\Example\Private+Sentinel.txt",
            r"C:\Users\Example+Sentinel\file.txt",
            r"C:\Users\Example+Sentinel",
            r"C:\Users\Example\Private$Secret.log",
            r"C:\Users\Example\user~1\data.csv",
            r"C:\Users\Example\user@domain\info.json",
            r"C:\Users\Example\build#1\out.log",
            r"C:\Users\Example\dir%20name\test.txt",
            r"C:\Users\Example\A&B\app.exe",
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
        """Detects concrete UNC host, private share, suffix, and ambiguous continuation paths (R2)."""
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
            r"\\server\share\+PrivateSentinel",
            "//server/share/+PrivateSentinel",
            r"\\server\share PrivateSentinel",
            r"\\nas01\share\Private Surname",
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
        """Redacts Unicode, multiword names, punctuation, and apostrophe paths completely without suffix leakage (R1)."""
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
            (
                r"Error at C:\Users\Example\Private Surname on line 42",
                "Error at [REDACTED_PATH] on line 42",
                ["Example", "Private", "Surname"],
            ),
            (
                r"Log at /home/Example/Private Surname for task",
                "Log at [REDACTED_PATH] for task",
                ["Example", "Private", "Surname"],
            ),
            (
                r"WSL path /mnt/c/Users/Example/Private Surname done",
                "WSL path [REDACTED_PATH] done",
                ["Example", "Private", "Surname"],
            ),
            (
                r"Build at C:\Users\Example\Private+Sentinel.txt successfully",
                "Build at [REDACTED_PATH] successfully",
                ["Example", "Private", "Sentinel", "Private+Sentinel"],
            ),
            (
                r"Source at C:\Users\Example+Sentinel\file.txt finished",
                "Source at [REDACTED_PATH] finished",
                ["Example", "Sentinel", "file.txt"],
            ),
            (
                r"Directory C:\Users\Example+Sentinel exists",
                "Directory [REDACTED_PATH] exists",
                ["Example", "Sentinel"],
            ),
            (
                r"Secret at C:\Users\Example\Private$Secret.log ok",
                "Secret at [REDACTED_PATH] ok",
                ["Private$Secret", "Secret.log"],
            ),
            (
                r"Path C:\Users\Example\user~1\data.csv ok",
                "Path [REDACTED_PATH] ok",
                ["user~1", "data.csv"],
            ),
            (
                r"Path C:\Users\Example\user@domain\info.json ok",
                "Path [REDACTED_PATH] ok",
                ["user@domain", "info.json"],
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
                r"UNC with plus at \\server\share\+PrivateSentinel finished",
                "UNC with plus at [REDACTED_PATH] finished",
                ["Private", "Sentinel", "PrivateSentinel"],
            ),
            (
                "Forward UNC with plus at //server/share/+PrivateSentinel finished",
                "Forward UNC with plus at [REDACTED_PATH] finished",
                ["Private", "Sentinel", "PrivateSentinel"],
            ),
            (
                r"UNC space continuation at \\server\share PrivateSentinel finished",
                "UNC space continuation at [REDACTED_PATH] finished",
                ["Private", "Sentinel", "PrivateSentinel"],
            ),
            (
                r"UNC multiword at \\nas01\share\Private Surname finished",
                "UNC multiword at [REDACTED_PATH] finished",
                ["nas01", "Private", "Surname"],
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

        # Verify multi-word, punctuation, and UNC continuations suppress unique suffix tokens
        multiword_text = r"Notice: C:\Users\Example\Private Surname for task"
        san_mw = sanitize_display_text(multiword_text)
        self.assertNotIn("Private", san_mw)
        self.assertNotIn("Surname", san_mw)
        self.assertIn("[REDACTED_PATH]", san_mw)

        punct_text = r"Notice: C:\Users\Example\Private+Sentinel.txt done"
        san_pt = sanitize_display_text(punct_text)
        self.assertNotIn("Private", san_pt)
        self.assertNotIn("Sentinel", san_pt)
        self.assertIn("[REDACTED_PATH]", san_pt)

        unc_text = r"Share: \\server\share\+PrivateSentinel finished"
        san_unc = sanitize_display_text(unc_text)
        self.assertNotIn("Private", san_unc)
        self.assertNotIn("Sentinel", san_unc)
        self.assertIn("[REDACTED_PATH]", san_unc)

    def test_sanitize_diff_line_preserves_prefix(self):
        """Preserves diff prefixes (+, -, space) while redacting payloads completely without suffix leakage."""
        add_line = "+ C:\\Users\\Éxample\\file.cs"
        sub_line = "- /home/Éxample/file.cs"
        ctx_line = "  token: Bearer ABCDEFGH+PrivateSentinel/=="
        self.assertEqual(sanitize_diff_line(add_line), "+ [REDACTED_PATH]")
        self.assertEqual(sanitize_diff_line(sub_line), "- [REDACTED_PATH]")
        self.assertEqual(sanitize_diff_line(ctx_line), "  token: [REDACTED_TOKEN]")

        # Multi-word and punctuation paths in diff lines
        mw_diff = "+ C:\\Users\\Example\\Private Surname"
        san_mw = sanitize_diff_line(mw_diff)
        self.assertEqual(san_mw, "+ [REDACTED_PATH]")
        self.assertNotIn("Private", san_mw)
        self.assertNotIn("Surname", san_mw)

        unc_diff = "- \\\\server\\share\\+PrivateSentinel"
        san_unc = sanitize_diff_line(unc_diff)
        self.assertEqual(san_unc, "- [REDACTED_PATH]")
        self.assertNotIn("Private", san_unc)
        self.assertNotIn("Sentinel", san_unc)

    def test_sanitize_diagnostic_suppresses_sensitive_data(self):
        """Sanitizes diagnostic messages cleanly without leaking Unicode names, tokens, or suffixes."""
        msg = "Error reading C:\\Users\\Éxample\\secret.key: file not found"
        sanitized = sanitize_diagnostic(msg)
        self.assertNotIn("Éxample", sanitized)
        self.assertNotIn("secret.key", sanitized)
        self.assertEqual(sanitized, "Error reading [REDACTED_PATH]: file not found")

        diag_mw = r"Path C:\Users\Example\Private Surname not accessible"
        san_mw = sanitize_diagnostic(diag_mw)
        self.assertNotIn("Private", san_mw)
        self.assertNotIn("Surname", san_mw)
        self.assertEqual(san_mw, "Path [REDACTED_PATH] not accessible")

        diag_unc = r"Network share \\server\share PrivateSentinel unavailable"
        san_unc = sanitize_diagnostic(diag_unc)
        self.assertNotIn("Private", san_unc)
        self.assertNotIn("Sentinel", san_unc)
        self.assertEqual(san_unc, "Network share [REDACTED_PATH] unavailable")

    def test_format_safe_filename_diagnostic(self):
        """Replaces sensitive filenames with <redacted-filename>."""
        sensitive_path = Path("C:/Users/Éxample/secret_report.md")
        safe_path = Path("reports/summary.md")
        self.assertEqual(format_safe_filename_diagnostic(sensitive_path), "<redacted-filename>")
        self.assertEqual(format_safe_filename_diagnostic(safe_path), "summary.md")

        mw_path = Path("C:/Users/Example/Private Surname")
        unc_path = Path(r"\\server\share\+PrivateSentinel")
        self.assertEqual(format_safe_filename_diagnostic(mw_path), "<redacted-filename>")
        self.assertEqual(format_safe_filename_diagnostic(unc_path), "<redacted-filename>")

    def test_format_safe_backup_display(self):
        """Formats backup confirmation safely using fixed prefix and basename."""
        backup_path = Path("C:/Users/Éxample/.backups/pr_body_backup_20261019.md")
        display = format_safe_backup_display(backup_path)
        self.assertNotIn("Éxample", display)
        self.assertEqual(display, "[REDACTED_PATH]/pr_body_backup_20261019.md")

        mw_backup = Path("C:/Users/Example/Private Surname/.backups/issue_42_backup.md")
        disp_mw = format_safe_backup_display(mw_backup)
        self.assertNotIn("Private", disp_mw)
        self.assertNotIn("Surname", disp_mw)
        self.assertEqual(disp_mw, "[REDACTED_PATH]/issue_42_backup.md")


if __name__ == "__main__":
    unittest.main()
