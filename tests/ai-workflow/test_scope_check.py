import os
import sys
import unittest
from pathlib import Path

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


if __name__ == "__main__":
    unittest.main()
