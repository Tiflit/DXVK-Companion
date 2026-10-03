import os
import sys
import unittest
from pathlib import Path

# Add scripts/ai-workflow to path for imports
REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import parse_contract


class TestContractParser(unittest.TestCase):
    """Regression tests for parsing Primary Issue references and Allowed paths contracts."""

    def test_primary_issue_from_dedicated_heading(self):
        pr_body = (
            "## Primary Issue\n\n"
            "Fixes #11\n\n"
            "## Summary\n"
            "Summary text here."
        )
        issue_ref = parse_contract.extract_primary_issue(pr_body)
        self.assertEqual(issue_ref, 11)

    def test_primary_issue_from_heading_raw_number(self):
        pr_body = (
            "## Primary Issue\n\n"
            "#42\n\n"
            "## Summary\n"
            "Some content"
        )
        issue_ref = parse_contract.extract_primary_issue(pr_body)
        self.assertEqual(issue_ref, 42)

    def test_primary_issue_from_heading_resolves_syntax(self):
        pr_body = (
            "## Primary Issue\n\n"
            "Resolves: #9\n\n"
            "## Summary\n"
            "Some content"
        )
        issue_ref = parse_contract.extract_primary_issue(pr_body)
        self.assertEqual(issue_ref, 9)

    def test_primary_issue_from_standard_closing_keywords(self):
        cases = [
            ("Fixes #123 in this PR.", 123),
            ("Closes #456 cleanly.", 456),
            ("RESOLVES #789", 789),
            ("fixes: #10", 10),
            ("closes #99", 99),
        ]
        for body, expected in cases:
            with self.subTest(body=body):
                self.assertEqual(parse_contract.extract_primary_issue(body), expected)

    def test_primary_issue_ignores_contextual_links(self):
        # When a primary keyword is present alongside contextual links,
        # the primary keyword MUST be chosen over contextual links.
        pr_body = (
            "## Summary\n\n"
            "This work relates to #7 and is tracked in #8, but Fixes #11.\n"
            "Related foundation: #10."
        )
        issue_ref = parse_contract.extract_primary_issue(pr_body)
        self.assertEqual(issue_ref, 11)

    def test_primary_issue_fails_when_only_contextual_links_present(self):
        pr_body = (
            "## Summary\n\n"
            "This change relates to #7 and is tracked in #8.\n"
            "See also #9 and foundation #10."
        )
        with self.assertRaises(parse_contract.ContractParseError) as cm:
            parse_contract.extract_primary_issue(pr_body)
        self.assertIn("contextual", str(cm.exception).lower())

    def test_primary_issue_fails_on_conflicting_references(self):
        pr_body = (
            "## Summary\n\n"
            "Fixes #11 and Closes #12 simultaneously."
        )
        with self.assertRaises(parse_contract.ContractParseError) as cm:
            parse_contract.extract_primary_issue(pr_body)
        self.assertIn("conflicting", str(cm.exception).lower())

    def test_primary_issue_fails_when_missing(self):
        pr_body = (
            "## Summary\n\n"
            "Just some text without any issue link."
        )
        with self.assertRaises(parse_contract.ContractParseError) as cm:
            parse_contract.extract_primary_issue(pr_body)
        self.assertIn("no primary issue", str(cm.exception).lower())

    def test_parse_allowed_paths_valid_list(self):
        issue_body = (
            "### Objective\n\nSome text\n\n"
            "### Allowed paths\n\n"
            "- src/DXVKCompanion/Utils/CompanionVersion.cs\n"
            "- tests/DXVKCompanion.PhaseA.Tests/CompanionVersionTests.cs\n"
            "- scripts/ai-workflow/**\n"
            "- docs/\n\n"
            "### Forbidden or out-of-scope changes\n\nNo refactoring."
        )
        allowed = parse_contract.parse_allowed_paths(issue_body)
        expected = [
            "src/DXVKCompanion/Utils/CompanionVersion.cs",
            "tests/DXVKCompanion.PhaseA.Tests/CompanionVersionTests.cs",
            "scripts/ai-workflow/**",
            "docs/",
        ]
        self.assertEqual(allowed.patterns, expected)
        self.assertFalse(allowed.is_unconstrained)

    def test_parse_allowed_paths_with_backticks_and_asterisks(self):
        issue_body = (
            "### Allowed paths\n\n"
            "* `src/DXVKCompanion/Utils/CompanionVersion.cs`\n"
            "- `tests/DXVKCompanion.PhaseA.Tests/**`\n"
        )
        allowed = parse_contract.parse_allowed_paths(issue_body)
        self.assertEqual(
            allowed.patterns,
            [
                "src/DXVKCompanion/Utils/CompanionVersion.cs",
                "tests/DXVKCompanion.PhaseA.Tests/**",
            ],
        )
        self.assertFalse(allowed.is_unconstrained)

    def test_parse_allowed_paths_standalone_unconstrained(self):
        variations = [
            "### Allowed paths\n\nUnconstrained\n\n### Next",
            "### Allowed paths\n\n- Unconstrained\n",
            "### Allowed paths\n\n* `Unconstrained`\n",
            "### Allowed paths\n\n  unconstrained  \n",
        ]
        for body in variations:
            with self.subTest(body=body):
                allowed = parse_contract.parse_allowed_paths(body)
                self.assertTrue(allowed.is_unconstrained)
                self.assertEqual(allowed.patterns, [])

    def test_parse_allowed_paths_rejects_loose_synonyms(self):
        bad_bodies = [
            "### Allowed paths\n\nNot yet constrained\n",
            "### Allowed paths\n\n- None\n",
            "### Allowed paths\n\nAny\n",
            "### Allowed paths\n\nN/A\n",
            "### Allowed paths\n\nScope is open for investigation\n",
        ]
        for body in bad_bodies:
            with self.subTest(body=body):
                with self.assertRaises(parse_contract.ContractParseError):
                    parse_contract.parse_allowed_paths(body)

    def test_parse_allowed_paths_missing_section_fails(self):
        body = "### Objective\n\nNo allowed paths section here."
        with self.assertRaises(parse_contract.ContractParseError) as cm:
            parse_contract.parse_allowed_paths(body)
        self.assertIn("missing", str(cm.exception).lower())

    def test_parse_allowed_paths_empty_section_fails(self):
        body = "### Allowed paths\n\n\n### Forbidden\n\nNothing."
        with self.assertRaises(parse_contract.ContractParseError) as cm:
            parse_contract.parse_allowed_paths(body)
        self.assertIn("empty", str(cm.exception).lower())

    def test_parse_allowed_paths_rejects_invalid_path_syntax(self):
        invalid_cases = [
            "- /absolute/path.cs",
            "- ../parent/path.cs",
            "- C:/windows/path.cs",
            "- \\windows\\backslash.cs",
        ]
        for path_entry in invalid_cases:
            body = f"### Allowed paths\n\n{path_entry}\n"
            with self.subTest(entry=path_entry):
                with self.assertRaises(parse_contract.ContractParseError):
                    parse_contract.parse_allowed_paths(body)


if __name__ == "__main__":
    unittest.main()
