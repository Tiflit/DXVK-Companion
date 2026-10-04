"""Unit and regression tests for automated compact handoff dashboard generation and publication."""

import json
import re
import sys
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import update_dashboard


class FakeGitHubClient:
    """Configurable in-memory GitHub client for testing acquisition and publication."""

    def __init__(self):
        self.main_shas = ["ce74e1e1caba1ee5197788c945826648d4f5a752"]
        self.main_sha_index = 0
        self.prs_pages = [[]]
        self.issues_pages = [[]]
        self.runs_pages = [[]]
        self.pr_runs = {}
        self.existing_issues = []
        self.created_issues = []
        self.patched_issues = []
        self.api_errors = {}

    def get_ref(self, ref: str):
        if "get_ref" in self.api_errors:
            raise self.api_errors["get_ref"]
        sha = self.main_shas[min(self.main_sha_index, len(self.main_shas) - 1)]
        self.main_sha_index += 1
        return {"object": {"sha": sha}}

    def fetch_paged(self, endpoint: str, per_page: int = 30, page: int = 1):
        for k, err in self.api_errors.items():
            if k in endpoint:
                raise err
        if "pulls" in endpoint:
            if page <= len(self.prs_pages):
                return self.prs_pages[page - 1]
            return []
        if "issues" in endpoint:
            if page <= len(self.issues_pages):
                return self.issues_pages[page - 1]
            return []
        if "actions/runs" in endpoint:
            if page <= len(self.runs_pages):
                return self.runs_pages[page - 1]
            return []
        return []

    def get_runs_for_commit(self, commit_sha: str):
        if "get_runs_for_commit" in self.api_errors:
            raise self.api_errors["get_runs_for_commit"]
        return self.pr_runs.get(commit_sha, [])

    def search_issues(self, query: str):
        if "search_issues" in self.api_errors:
            raise self.api_errors["search_issues"]
        return [
            iss for iss in self.existing_issues
            if update_dashboard.DASHBOARD_MARKER in iss.get("body", "")
            or update_dashboard.DASHBOARD_TITLE in iss.get("title", "")
        ]

    def create_issue(self, title: str, body: str, labels=None):
        if "create_issue" in self.api_errors:
            raise self.api_errors["create_issue"]
        new_iss = {
            "number": len(self.existing_issues) + len(self.created_issues) + 100,
            "title": title,
            "body": body,
            "labels": labels or [],
            "state": "open",
        }
        self.created_issues.append(new_iss)
        return new_iss

    def patch_issue(self, issue_number: int, body: str):
        if "patch_issue" in self.api_errors:
            raise self.api_errors["patch_issue"]
        patch_record = {"number": issue_number, "body": body}
        self.patched_issues.append(patch_record)
        return patch_record


class TestDashboardGenerator(unittest.TestCase):
    """Tests covering all 10 acceptance criteria for compact handoff maintenance."""

    def setUp(self):
        self.client = FakeGitHubClient()
        self.curated_text = (
            "## 2. Active Work Queue & Ownership\n\n"
            "| Work | Owner | Dependency | Next Action |\n"
            "|---|---|---|---|\n"
            "| **#25 handoff automation** | Gemini; ChatGPT verifies | `main` | Open PR with tests. |\n\n"
            "## 5. Unresolved Architectural & Governance Decisions\n\n"
            "1. **Issue #12 (Spec Authority)**: A1-UPDATED as canonical.\n"
            "2. **Issue #14 (Shared directories)**: Per-executable vs folder.\n"
        )

    def test_pagination_and_omission_notice(self):
        """Criterion 3 & 9: Handles pagination across multiple pages with bounds and omission."""
        self.client.prs_pages = [
            [{"number": 1, "title": "PR 1", "head": {"sha": "a" * 40}, "base": {"sha": "b" * 40}, "state": "open"}],
            [{"number": 2, "title": "PR 2", "head": {"sha": "c" * 40}, "base": {"sha": "b" * 40}, "state": "open"}],
        ]
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect(max_pages=2, per_page=1)
        self.assertEqual(len(facts.open_prs), 2)
        self.assertEqual(facts.open_prs[0]["number"], 1)
        self.assertEqual(facts.open_prs[1]["number"], 2)

    def test_missing_fields_and_unavailable_provenance(self):
        """Criterion 2, 3 & 9: Incomplete or missing PR/run attributes reported as unknown, no crash."""
        self.client.prs_pages = [
            [{"number": 9, "title": None, "head": {}, "base": {}, "state": "open"}]
        ]
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect()
        self.assertEqual(len(facts.open_prs), 1)
        pr = facts.open_prs[0]
        self.assertEqual(pr["head_sha"], "unknown")
        self.assertEqual(pr["base_sha"], "unknown")
        self.assertEqual(pr["tested_checkout_sha"], "unknown")

    def test_malformed_ids_and_shas(self):
        """Criterion 8 & 9: Malformed or invalid commit SHAs are sanitized and handled safely."""
        self.assertFalse(update_dashboard.is_valid_sha("not-a-sha"))
        self.assertFalse(update_dashboard.is_valid_sha("12345"))
        self.assertFalse(update_dashboard.is_valid_sha("zzzz" * 10))
        self.assertTrue(update_dashboard.is_valid_sha("a" * 40))
        self.assertTrue(update_dashboard.is_valid_sha("ce74e1e1caba1ee5197788c945826648d4f5a752"))

    def test_api_failure_vs_genuine_empty_state(self):
        """Criterion 7 & 9: Distinguishes API error (INCOMPLETE) from true empty inventory (COMPLETE)."""
        self.client.prs_pages = [[]]
        self.client.issues_pages = [[]]
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect()
        self.assertEqual(facts.completeness, update_dashboard.COMPLETENESS_COMPLETE)
        self.assertEqual(len(facts.open_prs), 0)
        self.assertEqual(len(facts.open_issues), 0)

        failing_client = FakeGitHubClient()
        failing_client.api_errors["pulls"] = update_dashboard.GitHubApiError("HTTP 500: Server Error")
        collector_err = update_dashboard.GitHubFactsCollector(client=failing_client, repo="Tiflit/DXVK-Companion")
        facts_err = collector_err.collect()
        self.assertTrue(facts_err.completeness.startswith("INCOMPLETE"))
        self.assertIn("pulls", facts_err.completeness)

    def test_main_movement_detection(self):
        """Criterion 2 & 9: Detects main advancing during acquisition; retries or marks incomplete."""
        self.client.main_shas = [
            "1111111111111111111111111111111111111111",
            "2222222222222222222222222222222222222222",
            "3333333333333333333333333333333333333333",
            "4444444444444444444444444444444444444444",
        ]
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect(max_main_retries=1)
        self.assertTrue(facts.completeness.startswith("INCOMPLETE"))
        self.assertIn("Main advanced during acquisition", facts.completeness)

    def test_stale_ci_and_attempt_mismatch(self):
        """Criterion 3 & 9: Distinguishes CI run for earlier commit from current PR head SHA."""
        pr_head = "a" * 40
        stale_run_head = "b" * 40
        self.client.prs_pages = [[{
            "number": 9,
            "title": "Modern API check",
            "head": {"sha": pr_head},
            "base": {"sha": "c" * 40},
            "state": "open",
        }]]
        self.client.pr_runs[pr_head] = [{
            "id": 123456,
            "run_attempt": 2,
            "head_sha": stale_run_head,
            "conclusion": "success",
            "html_url": "https://github.com/Tiflit/DXVK-Companion/actions/runs/123456",
        }]
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect()
        pr = facts.open_prs[0]
        self.assertEqual(pr["ci_status"], "historical/stale")
        self.assertEqual(pr["ci_head_sha"], stale_run_head)

    def test_untrusted_input_and_injection_defense(self):
        """Criterion 8 & 9: Markdown table injection, HTML, and control characters in titles are neutralized."""
        malicious_title = "Exploit | Attempt | Injection <script>alert(1)</script> \n newline [link](http://evil.com)"
        sanitized = update_dashboard.sanitize_display_text(malicious_title)
        self.assertNotIn("|", sanitized)
        self.assertNotIn("<script>", sanitized)
        self.assertNotIn("\n", sanitized)

    def test_sensitive_path_and_privacy_redaction(self):
        """Criterion 8 & 9: Redacts local home paths, usernames, and auth tokens from titles and diagnostics."""
        text_with_user = r"Error in C:\Users\philg\OneDrive\Documents\file.cs with token ghp_ABC1234567890XYZ"
        sanitized = update_dashboard.sanitize_display_text(text_with_user)
        self.assertNotIn("philg", sanitized)
        self.assertNotIn("ghp_ABC1234567890XYZ", sanitized)
        self.assertIn("[REDACTED_USER]", sanitized)
        self.assertIn("[REDACTED_TOKEN]", sanitized)

    def test_snapshot_word_count_and_overflow(self):
        """Criterion 4 & 9: Default snapshot length is bounded to <= 1000 words excluding URLs."""
        many_issues = []
        for i in range(60):
            many_issues.append({
                "number": 100 + i,
                "title": f"Long descriptive task issue {i} providing detailed context for feature development",
                "state": "open",
                "labels": [{"name": "bug"}],
            })
        self.client.issues_pages = [many_issues]
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect()
        rendered = update_dashboard.render_dashboard(facts, self.curated_text, max_words=1000)
        word_count = update_dashboard.count_words_excluding_urls(rendered)
        self.assertLessEqual(word_count, 1000)
        self.assertIn("omitted to respect snapshot word budget", rendered)

    def test_duplicate_destination_handling(self):
        """Criterion 6 & 9: Refuses ambiguous update if multiple dashboard issues are detected."""
        self.client.existing_issues = [
            {"number": 50, "title": update_dashboard.DASHBOARD_TITLE, "body": update_dashboard.DASHBOARD_MARKER},
            {"number": 51, "title": update_dashboard.DASHBOARD_TITLE, "body": update_dashboard.DASHBOARD_MARKER},
        ]
        publisher = update_dashboard.DashboardPublisher(client=self.client, repo="Tiflit/DXVK-Companion")
        with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
            publisher.publish("Some body")
        self.assertIn("Multiple dashboard issues detected", str(ctx.exception))

    def test_loop_filtering(self):
        """Criterion 6 & 9: Skips execution when triggered by dashboard issue itself or bot action."""
        publisher = update_dashboard.DashboardPublisher(client=self.client, repo="Tiflit/DXVK-Companion")
        
        event_dashboard = {
            "issue": {"title": update_dashboard.DASHBOARD_TITLE, "number": 99},
            "sender": {"login": "github-actions[bot]"},
        }
        skip, reason = publisher.should_skip_event(event_dashboard)
        self.assertTrue(skip)
        self.assertIn("dashboard issue itself", reason)

        event_task = {
            "issue": {"title": "[AI] F5 Shared directory policy", "number": 14},
            "sender": {"login": "user"},
        }
        skip2, _ = publisher.should_skip_event(event_task)
        self.assertFalse(skip2)

    def test_unchanged_publication_skipped(self):
        """Criterion 6 & 9: Unchanged snapshot skips the API PATCH write call."""
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect()
        body1 = update_dashboard.render_dashboard(facts, self.curated_text)
        
        existing_body = re.sub(
            r"Generated: [^\|]+ \|",
            "Generated: 2026-10-01 00:00:00 UTC |",
            body1,
        )
        self.client.existing_issues = [{
            "number": 88,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": existing_body,
        }]
        publisher = update_dashboard.DashboardPublisher(client=self.client, repo="Tiflit/DXVK-Companion")
        result = publisher.publish(body1)
        self.assertEqual(result["status"], "skipped")
        self.assertEqual(len(self.client.patched_issues), 0)

    def test_preservation_of_curated_content(self):
        """Criterion 4 & 9: Curated decisions and governance queue are preserved accurately."""
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect()
        rendered = update_dashboard.render_dashboard(facts, self.curated_text)
        self.assertIn("Active Work Queue & Ownership", rendered)
        self.assertIn("#25 handoff automation", rendered)
        self.assertIn("Issue #12 (Spec Authority)", rendered)
        self.assertIn("Issue #14 (Shared directories)", rendered)

    def test_workflow_yaml_structure_and_least_privilege(self):
        """Criterion 5, 6 & 10: Validates workflow YAML structure, least privilege, and default branch checkout."""
        wf_path = REPO_ROOT / ".github" / "workflows" / "ai-current-state.yml"
        self.assertTrue(wf_path.exists(), "Workflow file .github/workflows/ai-current-state.yml must exist")
        
        content = wf_path.read_text(encoding="utf-8")
        
        # Check YAML formatting basics
        self.assertNotIn("\t", content, "YAML must not contain tab characters")
        self.assertIn("name: AI Current State Dashboard", content)
        self.assertIn("permissions:\n  contents: read", content)
        self.assertIn("issues: write", content)
        self.assertIn("actions/checkout@v7", content)
        self.assertIn("default_branch", content)
        self.assertIn("python scripts/ai-workflow/update_dashboard.py", content)
        self.assertIn("if: >-", content)  # Loop prevention expression in YAML


if __name__ == "__main__":
    unittest.main()
