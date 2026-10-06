import json
import os
import sys
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import evaluate_scope


class TestScopeCheck(unittest.TestCase):
    """Regression tests for repository scope evaluation against Issue contracts."""

    def test_exact_file_match(self):
        patterns = ["src/DXVKCompanion/Utils/CompanionVersion.cs"]
        files = [
            {"filename": "src/DXVKCompanion/Utils/CompanionVersion.cs", "status": "modified"}
        ]
        result = evaluate_scope.check_files(files, patterns, is_unconstrained=False)
        self.assertTrue(result.is_valid)
        self.assertEqual(result.violations, [])

    def test_exact_file_match_rejects_similar_or_extended_names(self):
        patterns = ["src/DXVKCompanion/Utils/CompanionVersion.cs"]
        files = [
            {"filename": "src/DXVKCompanion/Utils/CompanionVersion.cs.bak", "status": "added"},
            {"filename": "src/DXVKCompanion/Utils/Other.cs", "status": "modified"},
        ]
        result = evaluate_scope.check_files(files, patterns, is_unconstrained=False)
        self.assertFalse(result.is_valid)
        self.assertEqual(len(result.violations), 2)

    def test_recursive_directory_wildcard(self):
        patterns = ["scripts/ai-workflow/**"]
        valid_files = [
            {"filename": "scripts/ai-workflow/evaluate_scope.py", "status": "added"},
            {"filename": "scripts/ai-workflow/sub/helper.py", "status": "modified"},
        ]
        result = evaluate_scope.check_files(valid_files, patterns, is_unconstrained=False)
        self.assertTrue(result.is_valid)

        invalid_files = [
            {"filename": "scripts/ai-workflow-other/evil.py", "status": "added"},
            {"filename": "scripts/ai-workflow.py", "status": "modified"},
        ]
        result_invalid = evaluate_scope.check_files(invalid_files, patterns, is_unconstrained=False)
        self.assertFalse(result_invalid.is_valid)
        self.assertEqual(len(result_invalid.violations), 2)

    def test_trailing_slash_directory_prefix(self):
        patterns = ["docs/"]
        valid_files = [
            {"filename": "docs/AI-DEVELOPMENT-WORKFLOW.md", "status": "modified"},
            {"filename": "docs/sub/notes.txt", "status": "added"},
        ]
        result = evaluate_scope.check_files(valid_files, patterns, is_unconstrained=False)
        self.assertTrue(result.is_valid)

        invalid_files = [
            {"filename": "docs_archive/old.md", "status": "modified"},
        ]
        result_invalid = evaluate_scope.check_files(invalid_files, patterns, is_unconstrained=False)
        self.assertFalse(result_invalid.is_valid)
        self.assertEqual(len(result_invalid.violations), 1)

    def test_folder_boundary_safety(self):
        patterns = ["src/foo"]
        # src/foobar.cs should NOT match src/foo unless src/foo is a directory prefix with slash
        files = [
            {"filename": "src/foobar.cs", "status": "modified"},
        ]
        result = evaluate_scope.check_files(files, patterns, is_unconstrained=False)
        self.assertFalse(result.is_valid)

    def test_unconstrained_scope_allows_any_file(self):
        files = [
            {"filename": "src/AnyFile.cs", "status": "modified"},
            {"filename": "tests/AnyTest.cs", "status": "added"},
            {"filename": "random/file.txt", "status": "deleted"},
        ]
        result = evaluate_scope.check_files(files, patterns=[], is_unconstrained=True)
        self.assertTrue(result.is_valid)
        self.assertEqual(result.violations, [])

    def test_renamed_file_checks_both_old_and_new_paths(self):
        patterns = ["src/allowed/**"]

        # Case 1: Old path in scope, new path out of scope -> VIOLATION
        f1 = [
            {
                "filename": "unallowed/new.cs",
                "previous_filename": "src/allowed/old.cs",
                "status": "renamed",
            }
        ]
        res1 = evaluate_scope.check_files(f1, patterns, is_unconstrained=False)
        self.assertFalse(res1.is_valid)
        self.assertIn("unallowed/new.cs", res1.violations)

        # Case 2: Old path out of scope, new path in scope -> VIOLATION
        f2 = [
            {
                "filename": "src/allowed/new.cs",
                "previous_filename": "unallowed/old.cs",
                "status": "renamed",
            }
        ]
        res2 = evaluate_scope.check_files(f2, patterns, is_unconstrained=False)
        self.assertFalse(res2.is_valid)
        self.assertTrue(any("unallowed/old.cs" in v for v in res2.violations))

        # Case 3: Both old and new path in scope -> VALID
        f3 = [
            {
                "filename": "src/allowed/new.cs",
                "previous_filename": "src/allowed/old.cs",
                "status": "renamed",
            }
        ]
        res3 = evaluate_scope.check_files(f3, patterns, is_unconstrained=False)
        self.assertTrue(res3.is_valid)

    def test_deleted_file_checked(self):
        patterns = ["src/allowed.cs"]
        f1 = [{"filename": "src/forbidden.cs", "status": "deleted"}]
        res = evaluate_scope.check_files(f1, patterns, is_unconstrained=False)
        self.assertFalse(res.is_valid)
        self.assertEqual(len(res.violations), 1)

    def test_api_mode_rejects_issue_carrying_ai_observation_label_even_with_valid_paths(self):
        """Scope guard: Live API mode rejects primary issue with 'ai-observation' label before parsing paths."""
        pr_data = {"body": "Closes #88\n\nAutomated implementation attempt."}
        issue_data = {
            "number": 88,
            "title": "[Observation] Bounded finding",
            "labels": [{"name": "ai-observation"}],
            "body": "### Allowed paths\n- src/DXVKCompanion/Utils/CompanionVersion.cs\n",
        }

        def mock_fetch_api(url, token):
            if "pulls/50" in url:
                return pr_data
            if "issues/88" in url:
                return issue_data
            return {}

        with patch.dict(os.environ, {"GH_TOKEN": "mock-token", "GH_REPO": "Tiflit/DXVK-Companion"}):
            with patch("evaluate_scope.fetch_github_api", side_effect=mock_fetch_api):
                with patch("evaluate_scope.fetch_pr_files") as mock_fetch_files:
                    code = evaluate_scope.main(["--pr-number", "50"])
                    self.assertEqual(code, 1, "Must exit with code 1 when issue carries 'ai-observation'")
                    mock_fetch_files.assert_not_called()

    def test_api_mode_accepts_ordinary_task_issue(self):
        """Ordinary task compatibility: Live API mode evaluates allowed paths for tasks without observation label."""
        pr_data = {"body": "Resolves #89\n\nAuthorized task."}
        issue_data = {
            "number": 89,
            "title": "[AI] Authorized Task",
            "labels": [{"name": "task"}],
            "body": "### Allowed paths\n- src/DXVKCompanion/Utils/CompanionVersion.cs\n",
        }
        pr_files = [{"filename": "src/DXVKCompanion/Utils/CompanionVersion.cs", "status": "modified"}]

        def mock_fetch_api(url, token):
            if "pulls/51" in url:
                return pr_data
            if "issues/89" in url:
                return issue_data
            return {}

        with patch.dict(os.environ, {"GH_TOKEN": "mock-token", "GH_REPO": "Tiflit/DXVK-Companion"}):
            with patch("evaluate_scope.fetch_github_api", side_effect=mock_fetch_api):
                with patch("evaluate_scope.fetch_pr_files", return_value=pr_files):
                    code = evaluate_scope.main(["--pr-number", "51"])
                    self.assertEqual(code, 0, "Must exit with code 0 for valid task within scope")

    def test_local_file_mode_evaluates_paths_without_requiring_label_metadata(self):
        """Local mode operates purely on file text and changed files list without claiming label metadata."""
        import tempfile
        with tempfile.TemporaryDirectory() as tmpdir:
            tmppath = Path(tmpdir)
            issue_file = tmppath / "issue.md"
            issue_file.write_text("### Allowed paths\n- src/**\n", encoding="utf-8")

            files_json = tmppath / "files.json"
            files_json.write_text(json.dumps([{"filename": "src/DXVKCompanion/App.cs"}]), encoding="utf-8")

            code = evaluate_scope.main([
                "--issue-body-file", str(issue_file),
                "--files-json", str(files_json),
            ])
            self.assertEqual(code, 0, "Local mode should parse body paths without label metadata")


if __name__ == "__main__":
    unittest.main()
