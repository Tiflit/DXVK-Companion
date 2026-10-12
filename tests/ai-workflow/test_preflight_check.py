import builtins
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import preflight_check


def compute_dir_hashes(root_dir: Path) -> dict[str, str]:
    """Computes SHA-256 hashes for all regular files in a directory tree."""
    hashes = {}
    for p in sorted(root_dir.rglob("*")):
        if p.is_file():
            rel = str(p.relative_to(root_dir)).replace("\\", "/")
            hashes[rel] = hashlib.sha256(p.read_bytes()).hexdigest()
    return hashes


class TestPreflightCheck(unittest.TestCase):
    """Regression test suite for read-only operator-invoked preflight CLI."""

    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.dir_path = Path(self.temp_dir.name)

    def tearDown(self):
        self.temp_dir.cleanup()

    def _write_file(self, name: str, content: str, encoding: str = "utf-8") -> Path:
        file_path = self.dir_path / name
        file_path.write_text(content, encoding=encoding)
        return file_path

    def _write_bytes(self, name: str, data: bytes) -> Path:
        file_path = self.dir_path / name
        file_path.write_bytes(data)
        return file_path

    def _create_standard_inputs(
        self,
        issue_content: str,
        pr_content: str,
        files_data: list,
    ) -> list[str]:
        issue_file = self._write_file("issue.md", issue_content)
        pr_file = self._write_file("pr.md", pr_content)
        files_file = self._write_file("files.json", json.dumps(files_data))
        return [
            "--issue-body-file",
            str(issue_file),
            "--pr-body-file",
            str(pr_file),
            "--files-json",
            str(files_file),
        ]

    def test_valid_scoped_change(self):
        issue_content = (
            "## Objective\n"
            "Valid task objective.\n\n"
            "### Allowed paths\n"
            "- src/DXVKCompanion/Utils/CompanionVersion.cs\n"
        )
        pr_content = (
            "## Primary Issue\n"
            "Fixes #85\n\n"
            "## Summary\n"
            "Valid summary.\n\n"
            "## Scope\n"
            "Valid scope.\n\n"
            "## Verification\n"
            "Valid verification.\n\n"
            "## Documentation\n"
            "Valid documentation.\n"
        )
        files_data = [{"filename": "src/DXVKCompanion/Utils/CompanionVersion.cs"}]

        argv = self._create_standard_inputs(issue_content, pr_content, files_data)

        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            ret = preflight_check.main(argv)

        self.assertEqual(ret, 0)
        output = stdout.getvalue()
        self.assertIn("Scope check:   PASS", output)
        self.assertIn("PR hygiene:    PASS", output)
        self.assertIn("Overall:       PASS", output)
        self.assertIn(preflight_check.NOT_VERIFIED_NOTICE, output)

    def test_unconstrained_and_empty_manifest_semantics(self):
        # 1. Unconstrained displays explicit PASS (Unconstrained)
        issue_unconstrained = "### Allowed paths\n- Unconstrained\n"
        pr_content = (
            "## Primary Issue\nFixes #1\n\n## Summary\nS\n\n## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n"
        )
        argv_unconstrained = self._create_standard_inputs(
            issue_unconstrained,
            pr_content,
            [{"filename": "any/path/file.cs"}],
        )
        stdout1 = io.StringIO()
        with patch("sys.stdout", stdout1):
            ret1 = preflight_check.main(argv_unconstrained)
        self.assertEqual(ret1, 0)
        self.assertIn("Scope check:   PASS (Unconstrained)", stdout1.getvalue())

        # 2. Empty manifest passes scope vacuously
        argv_empty = self._create_standard_inputs(
            "### Allowed paths\n- src/file.cs\n",
            pr_content,
            [],
        )
        stdout2 = io.StringIO()
        with patch("sys.stdout", stdout2):
            ret2 = preflight_check.main(argv_empty)
        self.assertEqual(ret2, 0)
        self.assertIn("Scope check:   PASS", stdout2.getvalue())

    def test_prose_in_allowed_paths_issue_80_style(self):
        issue_content = (
            "## Objective\n"
            "Diagnostic action types.\n\n"
            "### Allowed paths\n"
            "- `src/DXVKCompanion/Diagnostics/DiagnosticReportGenerator.cs` (add two action-type fields)\n"
        )
        pr_content = (
            "## Primary Issue\n"
            "Fixes #80\n\n"
            "## Summary\n"
            "Valid summary.\n\n"
            "## Scope\n"
            "Valid scope.\n\n"
            "## Verification\n"
            "Valid verification.\n\n"
            "## Documentation\n"
            "Valid documentation.\n"
        )
        files_data = [{"filename": "src/DXVKCompanion/Diagnostics/DiagnosticReportGenerator.cs"}]

        argv = self._create_standard_inputs(issue_content, pr_content, files_data)

        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            ret = preflight_check.main(argv)

        self.assertEqual(ret, 1)
        output = stdout.getvalue()
        self.assertIn("Scope check:   FAIL", output)
        self.assertIn("contains whitespace or prose", output)
        self.assertIn("Overall:       FAIL", output)
        self.assertIn(preflight_check.NOT_VERIFIED_NOTICE, output)

    def test_corrected_issue_82_style_contract(self):
        issue_content = (
            "## Objective\n"
            "Diagnostic action types corrected.\n\n"
            "### Allowed paths\n"
            "- `src/DXVKCompanion/Diagnostics/DiagnosticReportGenerator.cs`\n"
        )
        pr_content = (
            "## Primary Issue\n"
            "Fixes #82\n\n"
            "## Summary\n"
            "Valid summary.\n\n"
            "## Scope\n"
            "Valid scope.\n\n"
            "## Verification\n"
            "Valid verification.\n\n"
            "## Documentation\n"
            "Valid documentation.\n"
        )
        files_data = [{"filename": "src/DXVKCompanion/Diagnostics/DiagnosticReportGenerator.cs"}]

        argv = self._create_standard_inputs(issue_content, pr_content, files_data)

        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            ret = preflight_check.main(argv)

        self.assertEqual(ret, 0)
        output = stdout.getvalue()
        self.assertIn("Scope check:   PASS", output)
        self.assertIn("PR hygiene:    PASS", output)
        self.assertIn("Overall:       PASS", output)

    def test_heading_boundaries(self):
        issue_content_normalized = (
            "### Allowed paths\n"
            "- src/allowed.cs\n\n"
            "## Next Section\n"
            "- src/unauthorized.cs\n"
        )
        pr_content = (
            "## Primary Issue\nFixes #1\n\n"
            "## Summary\nS\n\n## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n"
        )
        # 1. Allowed file passes
        argv1 = self._create_standard_inputs(
            issue_content_normalized,
            pr_content,
            [{"filename": "src/allowed.cs"}],
        )
        stdout1 = io.StringIO()
        with patch("sys.stdout", stdout1):
            self.assertEqual(preflight_check.main(argv1), 0)

        # 2. File under Next Section fails without echoing filename
        argv2 = self._create_standard_inputs(
            issue_content_normalized,
            pr_content,
            [{"filename": "src/unauthorized.cs"}],
        )
        stdout2 = io.StringIO()
        with patch("sys.stdout", stdout2):
            self.assertEqual(preflight_check.main(argv2), 1)
        out2 = stdout2.getvalue()
        self.assertIn("Scope violation: item at manifest index 0", out2)
        self.assertNotIn("src/unauthorized.cs", out2)

        # 3. Using H2 '## Allowed paths' instead of '### Allowed paths' fails
        issue_h2 = (
            "## Allowed paths\n"
            "- src/allowed.cs\n"
        )
        argv3 = self._create_standard_inputs(
            issue_h2,
            pr_content,
            [{"filename": "src/allowed.cs"}],
        )
        stdout3 = io.StringIO()
        with patch("sys.stdout", stdout3):
            self.assertEqual(preflight_check.main(argv3), 1)
        self.assertIn("missing '### Allowed paths' section", stdout3.getvalue())

    def test_missing_or_empty_allowed_paths(self):
        pr_content = (
            "## Primary Issue\nFixes #1\n\n"
            "## Summary\nS\n\n## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n"
        )
        files = [{"filename": "src/any.cs"}]

        # Missing section entirely
        argv1 = self._create_standard_inputs("## Description only", pr_content, files)
        stdout1 = io.StringIO()
        with patch("sys.stdout", stdout1):
            self.assertEqual(preflight_check.main(argv1), 1)
        self.assertIn("missing '### Allowed paths' section", stdout1.getvalue())

        # Empty section
        argv2 = self._create_standard_inputs("### Allowed paths\n\n### Next", pr_content, files)
        stdout2 = io.StringIO()
        with patch("sys.stdout", stdout2):
            self.assertEqual(preflight_check.main(argv2), 1)
        self.assertIn("is empty", stdout2.getvalue())

    def test_mixed_unconstrained_and_explicit_paths(self):
        issue_content = (
            "### Allowed paths\n"
            "- Unconstrained\n"
            "- src/extra.cs\n"
        )
        pr_content = (
            "## Primary Issue\nFixes #1\n\n"
            "## Summary\nS\n\n## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n"
        )
        argv = self._create_standard_inputs(issue_content, pr_content, [{"filename": "src/extra.cs"}])

        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            ret = preflight_check.main(argv)

        self.assertEqual(ret, 1)
        self.assertIn("mixed 'Unconstrained' and explicit path patterns", stdout.getvalue())

    def test_rename_outside_scope_and_sanitized_diagnostics(self):
        # Inverted assertion: verify raw previous_filename is NOT in output
        issue_content = (
            "### Allowed paths\n"
            "- src/new_name.cs\n"
        )
        pr_content = (
            "## Primary Issue\nFixes #1\n\n"
            "## Summary\nS\n\n## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n"
        )
        files = [
            {
                "filename": "src/new_name.cs",
                "previous_filename": "src/unauthorized_old_name.cs",
            }
        ]
        argv = self._create_standard_inputs(issue_content, pr_content, files)

        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            ret = preflight_check.main(argv)

        self.assertEqual(ret, 1)
        output = stdout.getvalue()
        self.assertIn("Scope check:   FAIL", output)
        self.assertIn("item at manifest index 0 violates allowed contract paths", output)
        self.assertNotIn("src/unauthorized_old_name.cs", output)
        self.assertNotIn("src/new_name.cs", output)

    def test_sensitive_filenames_rename_sources_and_newlines(self):
        issue_content = "### Allowed paths\n- src/valid.cs\n"
        pr_content = (
            "## Primary Issue\nFixes #1\n\n## Summary\nS\n\n## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n"
        )
        files = [
            {
                "filename": "ghp_sensitiveFilenameToken1234567890\nwithNewline.cs",
                "previous_filename": "ghp_sensitivePrevToken9999999999.cs",
            }
        ]
        argv = self._create_standard_inputs(issue_content, pr_content, files)

        stdout = io.StringIO()
        stderr = io.StringIO()
        with patch("sys.stdout", stdout), patch("sys.stderr", stderr):
            ret = preflight_check.main(argv)

        self.assertEqual(ret, 1)
        combined = stdout.getvalue() + stderr.getvalue()
        self.assertNotIn("ghp_sensitiveFilenameToken", combined)
        self.assertNotIn("ghp_sensitivePrevToken", combined)
        self.assertNotIn("withNewline", combined)
        self.assertIn("item at manifest index 0 violates allowed contract paths", stdout.getvalue())

    def test_large_violation_list_capping_and_truncation(self):
        issue_content = "### Allowed paths\n- src/allowed.cs\n"
        pr_content = (
            "## Primary Issue\nFixes #1\n\n## Summary\nS\n\n## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n"
        )
        files = [{"filename": f"src/violating_{i}.cs"} for i in range(10)]
        argv = self._create_standard_inputs(issue_content, pr_content, files)

        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            ret = preflight_check.main(argv)

        self.assertEqual(ret, 1)
        output = stdout.getvalue()
        for idx in range(5):
            self.assertIn(f"item at manifest index {idx} violates allowed contract paths", output)
        self.assertNotIn("item at manifest index 5", output)
        self.assertIn("... and 5 additional scope violation(s) truncated.", output)

    def test_utf8_bom_compatibility_across_inputs(self):
        bom = b"\xef\xbb\xbf"
        issue_bytes = bom + b"### Allowed paths\n- src/bom_file.cs\n"
        pr_bytes = bom + (
            b"## Primary Issue\nFixes #1\n\n"
            b"## Summary\nSummary\n\n"
            b"## Scope\nScope\n\n"
            b"## Verification\nVerification\n\n"
            b"## Documentation\nDocumentation\n"
        )
        files_bytes = bom + json.dumps([{"filename": "src/bom_file.cs"}]).encode("utf-8")

        issue_file = self._write_bytes("issue_bom.md", issue_bytes)
        pr_file = self._write_bytes("pr_bom.md", pr_bytes)
        files_file = self._write_bytes("files_bom.json", files_bytes)

        argv = [
            "--issue-body-file", str(issue_file),
            "--pr-body-file", str(pr_file),
            "--files-json", str(files_file),
        ]
        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            ret = preflight_check.main(argv)

        self.assertEqual(ret, 0)
        output = stdout.getvalue()
        self.assertIn("Scope check:   PASS", output)
        self.assertIn("PR hygiene:    PASS", output)
        self.assertIn("Overall:       PASS", output)

    def test_missing_and_conflicting_primary_issue(self):
        issue_content = "### Allowed paths\n- src/file.cs\n"
        files = [{"filename": "src/file.cs"}]

        # Missing primary issue
        pr_missing = (
            "## Summary\nS\n\n## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n"
        )
        argv1 = self._create_standard_inputs(issue_content, pr_missing, files)
        stdout1 = io.StringIO()
        with patch("sys.stdout", stdout1):
            self.assertEqual(preflight_check.main(argv1), 1)
        self.assertIn("no primary issue reference found", stdout1.getvalue())

        # Conflicting primary issues
        pr_conflicting = (
            "## Primary Issue\nFixes #80\nFixes #82\n\n"
            "## Summary\nS\n\n## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n"
        )
        argv2 = self._create_standard_inputs(issue_content, pr_conflicting, files)
        stdout2 = io.StringIO()
        with patch("sys.stdout", stdout2):
            self.assertEqual(preflight_check.main(argv2), 1)
        self.assertIn("conflicting primary issue references", stdout2.getvalue())

    def test_missing_pr_headings(self):
        issue_content = "### Allowed paths\n- src/file.cs\n"
        files = [{"filename": "src/file.cs"}]

        pr_missing_heading = (
            "## Primary Issue\nFixes #1\n\n"
            "## Summary\nS\n\n## Scope\nSc\n\n## Documentation\nD\n"
        )
        argv = self._create_standard_inputs(issue_content, pr_missing_heading, files)
        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            ret = preflight_check.main(argv)

        self.assertEqual(ret, 1)
        output = stdout.getvalue()
        self.assertIn("PR hygiene:    FAIL", output)
        self.assertIn("missing required section heading '## Verification'", output)

    def test_malformed_changed_file_json_and_exit_notices(self):
        issue_file = self._write_file("issue.md", "### Allowed paths\n- src/f.cs\n")
        pr_file = self._write_file(
            "pr.md",
            "## Primary Issue\nFixes #1\n\n## Summary\nS\n\n## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n",
        )

        bad_cases = [
            ("invalid json", "not contain valid JSON syntax"),
            (json.dumps({"filename": "not a list"}), "must be a JSON array"),
            (json.dumps(["not a dict"]), "is not a JSON object"),
            (json.dumps([{"filename": ""}]), "missing a non-empty string 'filename'"),
            (json.dumps([{"filename": "ok", "previous_filename": 123}]), "invalid 'previous_filename'"),
        ]

        for content, expected_msg in bad_cases:
            f_file = self._write_file("bad_files.json", content)
            argv = [
                "--issue-body-file", str(issue_file),
                "--pr-body-file", str(pr_file),
                "--files-json", str(f_file),
            ]
            stderr = io.StringIO()
            with patch("sys.stderr", stderr):
                ret = preflight_check.main(argv)
            self.assertEqual(ret, 1)
            err_text = stderr.getvalue()
            self.assertIn(expected_msg, err_text)
            self.assertIn(preflight_check.NOT_VERIFIED_NOTICE, err_text)

    def test_unreadable_or_missing_inputs_and_exit_notices(self):
        pr_file = self._write_file("pr.md", "text")
        files_file = self._write_file("files.json", "[]")

        # Nonexistent issue file
        argv = [
            "--issue-body-file", str(self.dir_path / "nonexistent.md"),
            "--pr-body-file", str(pr_file),
            "--files-json", str(files_file),
        ]
        stderr = io.StringIO()
        with patch("sys.stderr", stderr):
            ret = preflight_check.main(argv)
        self.assertEqual(ret, 1)
        err_text = stderr.getvalue()
        self.assertIn("Input --issue-body-file does not exist", err_text)
        self.assertIn(preflight_check.NOT_VERIFIED_NOTICE, err_text)

        # Directory passed as file
        argv_dir = [
            "--issue-body-file", str(self.dir_path),
            "--pr-body-file", str(pr_file),
            "--files-json", str(files_file),
        ]
        stderr2 = io.StringIO()
        with patch("sys.stderr", stderr2):
            ret2 = preflight_check.main(argv_dir)
        self.assertEqual(ret2, 1)
        err_text2 = stderr2.getvalue()
        self.assertIn("is not a regular file", err_text2)
        self.assertIn(preflight_check.NOT_VERIFIED_NOTICE, err_text2)

    def test_argparse_sanitization_and_no_flag_abbreviation(self):
        # 1. Unknown flag: fixed error text, no raw argument echo, exit status 2
        stderr1 = io.StringIO()
        with patch("sys.stderr", stderr1):
            with self.assertRaises(SystemExit) as cm:
                preflight_check.main(["--unknown-injected-flag-foo-bar"])
            self.assertEqual(cm.exception.code, 2)
        err_text1 = stderr1.getvalue()
        self.assertIn("ERROR: Invalid command-line arguments.", err_text1)
        self.assertNotIn("--unknown-injected-flag-foo-bar", err_text1)
        self.assertIn(preflight_check.NOT_VERIFIED_NOTICE, err_text1)

        # 2. Flag abbreviation rejected (allow_abbrev=False)
        stderr2 = io.StringIO()
        with patch("sys.stderr", stderr2):
            with self.assertRaises(SystemExit) as cm2:
                preflight_check.main(["--issue", "some/path.md"])
            self.assertEqual(cm2.exception.code, 2)
        err_text2 = stderr2.getvalue()
        self.assertIn("ERROR: Invalid command-line arguments.", err_text2)
        self.assertIn(preflight_check.NOT_VERIFIED_NOTICE, err_text2)

        # 3. --help displays sanitized help with NOT VERIFIED notice and exits 0
        stdout3 = io.StringIO()
        with patch("sys.stdout", stdout3):
            with self.assertRaises(SystemExit) as cm3:
                preflight_check.main(["--help"])
            self.assertEqual(cm3.exception.code, 0)
        out3 = stdout3.getvalue()
        self.assertIn("preflight_check.py", out3)
        self.assertIn(preflight_check.NOT_VERIFIED_NOTICE, out3)

    def test_sensitive_diagnostic_input(self):
        issue_content = (
            "### Allowed paths\n"
            "- `ghp_superSecretToken12345678901234567890` (some prose)\n"
        )
        pr_content = (
            "## Primary Issue\nFixes #1\n\n"
            "## Summary\nToken ghp_anotherSecretToken99999999999999999999\n\n"
            "## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n"
        )
        files = [{"filename": "src/file.cs"}]
        argv = self._create_standard_inputs(issue_content, pr_content, files)

        stdout = io.StringIO()
        stderr = io.StringIO()
        with patch("sys.stdout", stdout), patch("sys.stderr", stderr):
            preflight_check.main(argv)

        combined_output = stdout.getvalue() + stderr.getvalue()
        self.assertNotIn("ghp_superSecretToken12345678901234567890", combined_output)
        self.assertNotIn("ghp_anotherSecretToken99999999999999999999", combined_output)

    def test_appended_approval_and_duplicate_decision_blocks(self):
        issue_content = (
            "### Decision Governance Block\n"
            "- **Decision required**: Architectural choice\n"
            "- **Status**: `PENDING`\n\n"
            "### Allowed paths\n"
            "- src/DXVKCompanion/Utils/CompanionVersion.cs\n\n"
            "### Decision Governance Block\n"
            "- **Decision required**: Architectural choice\n"
            "- **Status**: `DECIDED`\n"
            "- **Source of explicit human approval**: Approved by human\n\n"
            "<!-- AI-REVIEW-RECORD: appended approval record -->\n"
            "Human said 'Approved' on 2026-10-12.\n"
        )
        pr_content = (
            "## Primary Issue\nFixes #85\n\n"
            "## Summary\nSummary\n\n## Scope\nScope\n\n## Verification\nVerification\n\n## Documentation\nDocumentation\n"
        )
        files = [{"filename": "src/DXVKCompanion/Utils/CompanionVersion.cs"}]
        argv = self._create_standard_inputs(issue_content, pr_content, files)

        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            ret = preflight_check.main(argv)

        self.assertEqual(ret, 0)
        output = stdout.getvalue()
        self.assertIn("Scope check:   PASS", output)
        self.assertIn("PR hygiene:    PASS", output)
        self.assertIn("Overall:       PASS", output)
        self.assertIn(preflight_check.NOT_VERIFIED_NOTICE, output)

    def test_in_process_guards_against_writes_and_network(self):
        issue_content = "### Allowed paths\n- src/f.cs\n"
        pr_content = (
            "## Primary Issue\nFixes #1\n\n## Summary\nS\n\n## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n"
        )
        files = [{"filename": "src/f.cs"}]
        argv = self._create_standard_inputs(issue_content, pr_content, files)

        def guarded_open(file, mode="r", *args, **kwargs):
            if any(m in mode for m in ("w", "a", "+", "x")):
                raise PermissionError("Write operation forbidden in read-only preflight.")
            return original_open(file, mode, *args, **kwargs)

        original_open = builtins.open

        with patch("builtins.open", side_effect=guarded_open), \
             patch("pathlib.Path.write_text", side_effect=PermissionError("write_text forbidden")), \
             patch("pathlib.Path.write_bytes", side_effect=PermissionError("write_bytes forbidden")), \
             patch("os.remove", side_effect=PermissionError("os.remove forbidden")), \
             patch("os.unlink", side_effect=PermissionError("os.unlink forbidden")), \
             patch("urllib.request.urlopen") as mock_urlopen, \
             patch("subprocess.run") as mock_subproc_run, \
             patch("subprocess.Popen") as mock_subproc_popen:

            stdout = io.StringIO()
            with patch("sys.stdout", stdout):
                ret = preflight_check.main(argv)

            self.assertEqual(ret, 0)
            mock_urlopen.assert_not_called()
            mock_subproc_run.assert_not_called()
            mock_subproc_popen.assert_not_called()

    def test_cloud_subprocess_startup_and_no_bytecode_or_mutations(self):
        """Cloud-only subprocess test copying scripts to clean temp tree to verify startup and zero pycache."""
        with tempfile.TemporaryDirectory() as clean_worktree:
            clean_path = Path(clean_worktree)
            scripts_dir = clean_path / "scripts" / "ai-workflow"
            scripts_dir.mkdir(parents=True, exist_ok=True)

            src_scripts = REPO_ROOT / "scripts" / "ai-workflow"
            for script_name in ["preflight_check.py", "evaluate_scope.py", "check_pr_hygiene.py", "parse_contract.py"]:
                src_file = src_scripts / script_name
                dest_file = scripts_dir / script_name
                dest_file.write_text(src_file.read_text(encoding="utf-8"), encoding="utf-8")

            # Synthetic fixture files - create all fixtures before taking baseline
            issue_f = clean_path / "issue.md"
            issue_f.write_text("### Allowed paths\n- src/clean.cs\n", encoding="utf-8")

            pr_f = clean_path / "pr.md"
            pr_f.write_text(
                "## Primary Issue\nFixes #1\n\n## Summary\nS\n\n## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n",
                encoding="utf-8",
            )

            files_f = clean_path / "files.json"
            files_f.write_text(json.dumps([{"filename": "src/clean.cs"}]), encoding="utf-8")

            bad_files_f = clean_path / "bad_files.json"
            bad_files_f.write_text(json.dumps([{"filename": "src/outside_scope.cs"}]), encoding="utf-8")

            # Compute complete recursive file path/hash baseline with all fixtures present
            baseline_hashes = compute_dir_hashes(clean_path)

            target_script = scripts_dir / "preflight_check.py"

            env = os.environ.copy()
            # Do NOT set PYTHONDONTWRITEBYTECODE externally; test the script's internal sys.dont_write_bytecode
            env.pop("PYTHONDONTWRITEBYTECODE", None)

            # 1. Successful execution
            cmd_pass = [
                sys.executable,
                str(target_script),
                "--issue-body-file", str(issue_f),
                "--pr-body-file", str(pr_f),
                "--files-json", str(files_f),
            ]
            res_pass = subprocess.run(cmd_pass, capture_output=True, text=True, env=env)
            self.assertEqual(res_pass.returncode, 0)
            self.assertIn("Scope check:   PASS", res_pass.stdout)
            self.assertIn(preflight_check.NOT_VERIFIED_NOTICE, res_pass.stdout)
            self.assertEqual(
                compute_dir_hashes(clean_path),
                baseline_hashes,
                "Tree state modified during successful preflight execution.",
            )

            # 2. Validation failure execution
            cmd_fail = [
                sys.executable,
                str(target_script),
                "--issue-body-file", str(issue_f),
                "--pr-body-file", str(pr_f),
                "--files-json", str(bad_files_f),
            ]
            res_fail = subprocess.run(cmd_fail, capture_output=True, text=True, env=env)
            self.assertEqual(res_fail.returncode, 1)
            self.assertIn("Scope check:   FAIL", res_fail.stdout)
            self.assertIn(preflight_check.NOT_VERIFIED_NOTICE, res_fail.stdout)
            self.assertEqual(
                compute_dir_hashes(clean_path),
                baseline_hashes,
                "Tree state modified during validation failure execution.",
            )

            # 3. Argument error execution
            cmd_arg_err = [
                sys.executable,
                str(target_script),
                "--invalid-flag-attempt",
            ]
            res_arg = subprocess.run(cmd_arg_err, capture_output=True, text=True, env=env)
            self.assertEqual(res_arg.returncode, 2)
            self.assertIn("ERROR: Invalid command-line arguments.", res_arg.stderr)
            self.assertIn(preflight_check.NOT_VERIFIED_NOTICE, res_arg.stderr)
            self.assertEqual(
                compute_dir_hashes(clean_path),
                baseline_hashes,
                "Tree state modified during argument error execution.",
            )

            # Assert NO __pycache__ directory was ever created
            pycache_dirs = list(clean_path.rglob("__pycache__"))
            self.assertEqual(pycache_dirs, [], "Bytecode cache directory __pycache__ must not be created.")

            # Assert NO .pyc files exist
            pyc_files = list(clean_path.rglob("*.pyc"))
            self.assertEqual(pyc_files, [], "Compiled bytecode files (*.pyc) must not be created.")


if __name__ == "__main__":
    unittest.main()
