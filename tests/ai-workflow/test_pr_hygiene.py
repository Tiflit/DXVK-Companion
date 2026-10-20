import os
import io
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

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


class TestDocumentationDisposition(unittest.TestCase):
    def check(self, text, **kwargs):
        body = ("## Primary Issue\nFixes #188\n## Summary\nS\n## Scope\nS\n"
                "## Verification\nV\n## Documentation\n" + text)
        return check_pr_hygiene.verify_pr_body(body, require_documentation=True, **kwargs)

    def test_dispositions_and_optional_complete_evidence(self):
        text = "- `docs/guide.md` — Explains the revised offline validation contract."
        unknown = self.check(text)
        self.assertTrue(unknown.is_valid)
        self.assertTrue(any("inclusion: NOT VERIFIED" in n for n in unknown.documentation_notes))
        for key in ("changed_files", "repository_files"):
            self.assertFalse(self.check(text, **{key: []}).is_valid)
            self.assertTrue(self.check(text, **{key: ["docs/guide.md"]}).is_valid)
            self.assertFalse(self.check(text, **{key: [None]}).is_valid)
        self.assertTrue(self.check(text, changed_files=["docs/guide.md"], repository_files=["docs/guide.md"]).is_valid)
        self.assertTrue(self.check("None — This repairs synthetic fixtures without changing public behavior.").is_valid)
        self.assertTrue(self.check(text + "\nREADME.md - Describes the supported installation workflow clearly.").is_valid)

    def test_placeholders_and_malformed_sections(self):
        for text in ("", "<!-- Fill later -->", "None", "N/A", "Done", "Updated.",
                     "None — no documentation changes are required.",
                     "None — TODO explain this later please.",
                     "None — <provide a specific reason here>",
                     "Unrelated boilerplate that says all tests passed.",
                     "`docs/guide.md` — Updated.",
                     "None — This repairs synthetic fixtures without changing public behavior.\n## Documentation\nDone",
                     "```\nNone — This repairs synthetic fixtures without changing public behavior.\n```"):
            with self.subTest(text=text):
                self.assertFalse(self.check(text).is_valid)

    def test_paths_are_literal_relative_and_diagnostics_do_not_echo_input(self):
        for path in ("../PrivateSentinel.md", "/PrivateSentinel.md", "C:/PrivateSentinel.md",
                     "docs/../PrivateSentinel.md", "docs//PrivateSentinel.md", "docs/./PrivateSentinel.md",
                     "https://example.invalid/PrivateSentinel.md", "docs/PrivateSentinel*.md",
                     "docs/PrivateSentinel%2f.md", "docs\\PrivateSentinel.md", "src/PrivateSentinel.cs"):
            result = self.check(f"`{path}` — Explains the revised offline validation contract.")
            self.assertFalse(result.is_valid)
            self.assertNotIn("PrivateSentinel", str(result.diagnostics))

    def test_comments_section_boundaries_and_legacy_signature(self):
        text = "None — This repairs synthetic fixtures without changing public behavior."
        self.assertTrue(self.check("<!-- guidance -->\n" + text + "\n## Review\nHistorical text").is_valid)
        self.assertFalse(self.check("<!-- " + text + " -->").is_valid)
        legacy = TestPrHygiene()
        legacy.setUp()
        self.assertTrue(check_pr_hygiene.verify_pr_body(legacy.valid_pr_body).is_valid)
        self.assertFalse(check_pr_hygiene.verify_pr_body(legacy.valid_pr_body, require_documentation=True).is_valid)

    def test_crlf_and_cli_adoption(self):
        body = ("## Primary Issue\r\nFixes #188\r\n## Summary\r\nS\r\n## Scope\r\nS\r\n"
                "## Verification\r\nV\r\n## Documentation\r\n"
                "None — This repairs synthetic fixtures without changing public behavior.\r\n")
        self.assertTrue(check_pr_hygiene.verify_pr_body(body, require_documentation=True).is_valid)
        for disposition, expected in (("Done", 1), ("None — This repairs synthetic fixtures without changing public behavior.", 0)):
            candidate = body.split("## Documentation")[0] + "## Documentation\n" + disposition
            with patch.dict(os.environ, {"GH_TOKEN": "synthetic", "GH_REPO": "example/repository"}, clear=True), \
                 patch.object(sys, "argv", ["check_pr_hygiene.py", "--pr-number", "188"]), \
                 patch.object(check_pr_hygiene, "fetch_github_api", return_value={"body": candidate}) as fetch, \
                 patch("sys.stdout", new_callable=io.StringIO), patch("sys.stderr", new_callable=io.StringIO):
                self.assertEqual(check_pr_hygiene.main(), expected)
                self.assertEqual(fetch.call_count, 1)


if __name__ == "__main__":
    unittest.main()

