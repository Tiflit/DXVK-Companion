import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import preflight_check


class TestPreflightCheck(unittest.TestCase):
    """Regression test suite for read-only operator-invoked preflight CLI."""

    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.dir_path = Path(self.temp_dir.name)

    def tearDown(self):
        self.temp_dir.cleanup()

    def _write_file(self, name: str, content: str) -> Path:
        file_path = self.dir_path / name
        file_path.write_text(content, encoding="utf-8")
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
        self.assertIn("NOT VERIFIED: Authority/approval", output)

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
        # Section boundary terminated by ## Next Heading
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

        # 2. File under Next Section fails
        argv2 = self._create_standard_inputs(
            issue_content_normalized,
            pr_content,
            [{"filename": "src/unauthorized.cs"}],
        )
        stdout2 = io.StringIO()
        with patch("sys.stdout", stdout2):
            self.assertEqual(preflight_check.main(argv2), 1)

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

    def test_rename_outside_scope(self):
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
        self.assertIn("src/unauthorized_old_name.cs", output)

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

        # Missing ## Verification
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

    def test_malformed_changed_file_json(self):
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
            self.assertIn(expected_msg, stderr.getvalue())

    def test_unreadable_or_missing_inputs(self):
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
        self.assertIn("Input --issue-body-file does not exist", stderr.getvalue())

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
        self.assertIn("is not a regular file", stderr2.getvalue())

    def test_sensitive_diagnostic_input(self):
        # Sensitive token in issue prose and PR heading
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
        # Verify that preflight explicitly disclaims decision verification
        self.assertIn("NOT VERIFIED: Authority/approval", output)

    def test_no_network_subprocess_or_file_write_attempts(self):
        issue_content = "### Allowed paths\n- src/f.cs\n"
        pr_content = (
            "## Primary Issue\nFixes #1\n\n## Summary\nS\n\n## Scope\nSc\n\n## Verification\nV\n\n## Documentation\nD\n"
        )
        files = [{"filename": "src/f.cs"}]
        argv = self._create_standard_inputs(issue_content, pr_content, files)

        before_files = set(self.dir_path.iterdir())

        with patch("urllib.request.urlopen") as mock_urlopen, \
             patch("subprocess.run") as mock_subproc_run, \
             patch("subprocess.Popen") as mock_subproc_popen:

            stdout = io.StringIO()
            with patch("sys.stdout", stdout):
                ret = preflight_check.main(argv)

            self.assertEqual(ret, 0)
            mock_urlopen.assert_not_called()
            mock_subproc_run.assert_not_called()
            mock_subproc_popen.assert_not_called()

        after_files = set(self.dir_path.iterdir())
        # No new files created by the CLI
        self.assertEqual(before_files, after_files)


if __name__ == "__main__":
    unittest.main()
