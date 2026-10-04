import os
import sys
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import check_pr_hygiene


class TestPrHygiene(unittest.TestCase):
    """Regression tests for PR metadata and template hygiene checks."""

    def setUp(self):
        self.valid_pr_body = (
            "## Primary Issue\n\n"
            "Fixes #11\n\n"
            "## Summary\n\n"
            "This PR hardens review packet provenance.\n\n"
            "## Scope\n\n"
            "Modifies workflow scripts and fixtures.\n\n"
            "## Verification\n\n"
            "All unit tests pass.\n\n"
            "## Documentation\n\n"
            "Updated AI-DEVELOPMENT-WORKFLOW.md.\n"
        )

    def test_complete_pr_metadata_passes(self):
        result = check_pr_hygiene.verify_pr_body(self.valid_pr_body)
        self.assertTrue(result.is_valid)
        self.assertEqual(result.missing_sections, [])
        self.assertEqual(result.primary_issue, 11)

    def test_missing_summary_section_fails(self):
        bad_body = self.valid_pr_body.replace("## Summary", "## Background")
        result = check_pr_hygiene.verify_pr_body(bad_body)
        self.assertFalse(result.is_valid)
        self.assertIn("Summary", result.missing_sections)

    def test_missing_scope_section_fails(self):
        bad_body = self.valid_pr_body.replace("## Scope", "")
        result = check_pr_hygiene.verify_pr_body(bad_body)
        self.assertFalse(result.is_valid)
        self.assertIn("Scope", result.missing_sections)

    def test_missing_verification_section_fails(self):
        bad_body = self.valid_pr_body.replace("## Verification", "## Testing")
        result = check_pr_hygiene.verify_pr_body(bad_body)
        self.assertFalse(result.is_valid)
        self.assertIn("Verification", result.missing_sections)

    def test_missing_documentation_section_fails(self):
        bad_body = self.valid_pr_body.replace("## Documentation", "")
        result = check_pr_hygiene.verify_pr_body(bad_body)
        self.assertFalse(result.is_valid)
        self.assertIn("Documentation", result.missing_sections)

    def test_missing_primary_issue_fails(self):
        # PR with all headings but no primary issue
        body = (
            "## Summary\n\nDone\n\n"
            "## Scope\n\nDone\n\n"
            "## Verification\n\nDone\n\n"
            "## Documentation\n\nDone\n"
        )
        result = check_pr_hygiene.verify_pr_body(body)
        self.assertFalse(result.is_valid)
        self.assertIn("Primary Issue", result.missing_sections)

    def test_heading_case_and_whitespace_insensitivity(self):
        body = (
            "##  primary issue  \n\nFixes #11\n\n"
            "##  SUMMARY  \n\nDone\n\n"
            "## scope \n\nDone\n\n"
            "## verification\n\nDone\n\n"
            "## documentation \n\nDone\n"
        )
        result = check_pr_hygiene.verify_pr_body(body)
        self.assertTrue(result.is_valid)
        self.assertEqual(result.primary_issue, 11)

    def test_unedited_pr_template_fails_hygiene(self):

        # The shipped .github/pull_request_template.md without user edits must fail hygiene (F8)
        template_file = REPO_ROOT / ".github" / "pull_request_template.md"
        self.assertTrue(template_file.exists())
        content = template_file.read_text(encoding="utf-8")
        result = check_pr_hygiene.verify_pr_body(content)
        self.assertFalse(result.is_valid)
        self.assertIn("Primary Issue", result.missing_sections)


if __name__ == "__main__":
    unittest.main()

