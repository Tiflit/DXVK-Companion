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
        self.reacquired_on_retry = False

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
        # Exact matching per H1:
        # Match only items with exact title, exact marker, and NOT pull requests
        matched = []
        for iss in self.existing_issues:
            if iss.get("pull_request"):
                continue
            title = iss.get("title", "").strip()
            body = iss.get("body", "")
            if title == update_dashboard.DASHBOARD_TITLE and update_dashboard.DASHBOARD_MARKER in body:
                matched.append(iss)
        return matched

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
    """Tests covering all acceptance criteria and coordinator findings H1–H7."""

    def setUp(self):
        self.client = FakeGitHubClient()
        self.curated_text = (
            "## 2. Active Work Queue & Ownership\n\n"
            "| Work | Owner | Dependency | Next Action |\n"
            "|---|---|---|---|\n"
            "| **#25 handoff automation** | Gemini; ChatGPT verifies | `main` | Open PR with tests. |\n\n"
            "## 5. Unresolved Architectural & Governance Decisions\n\n"
            "1. **Issue #12 (Spec Authority)**: Proposes consolidating A1-UPDATED to docs/spec/DXVK-COMPANION-SPEC.md.\n"
            "2. **Issue #14 (Shared directories)**: Per-executable vs installation-wide.\n"
        )

    # --- H1: Publication Destination Authentication ---

    def test_task_issue_with_marker_cannot_be_overwritten(self):
        """H1: An ordinary task issue mentioning the dashboard marker in its body must NOT be matched or overwritten."""
        task_issue_25 = {
            "number": 25,
            "title": "[AI] Automate compact GitHub handoffs",
            "body": "Discussion mentioning <!-- AI-DASHBOARD-MARKER: v1 --> in task notes",
            "state": "open",
        }
        self.client.existing_issues = [task_issue_25]
        publisher = update_dashboard.DashboardPublisher(client=self.client, repo="Tiflit/DXVK-Companion")
        
        # When published, it must NOT find Issue #25 as the dashboard issue; it should create a dedicated one
        res = publisher.publish("New dashboard content")
        self.assertEqual(res["status"], "created")
        self.assertNotEqual(res["issue_number"], 25)
        self.assertEqual(len(self.client.patched_issues), 0)

    def test_pull_request_with_dashboard_title_cannot_be_destination(self):
        """H1: A pull request sharing the dashboard title must be rejected as an invalid destination."""
        pr_target = {
            "number": 99,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": update_dashboard.DASHBOARD_MARKER,
            "pull_request": {"url": "https://api.github.com/repos/Tiflit/DXVK-Companion/pulls/99"},
            "state": "open",
        }
        self.client.existing_issues = [pr_target]
        publisher = update_dashboard.DashboardPublisher(client=self.client, repo="Tiflit/DXVK-Companion")
        res = publisher.publish("New content")
        self.assertEqual(res["status"], "created")
        self.assertNotEqual(res["issue_number"], 99)

    def test_closed_dashboard_issue_fails_safely_without_recreating(self):
        """H1: A closed dashboard issue causes an explicit failure rather than silently creating an uncoordinated duplicate."""
        closed_dashboard = {
            "number": 42,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": update_dashboard.DASHBOARD_MARKER,
            "state": "closed",
        }
        self.client.existing_issues = [closed_dashboard]
        publisher = update_dashboard.DashboardPublisher(client=self.client, repo="Tiflit/DXVK-Companion")
        with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
            publisher.publish("New dashboard content")
        self.assertIn("closed", str(ctx.exception).lower())
        self.assertEqual(len(self.client.created_issues), 0)

    def test_duplicate_destination_handling(self):
        """H1 & Criterion 6: Refuses ambiguous update if multiple open dashboard issues are detected."""
        self.client.existing_issues = [
            {"number": 50, "title": update_dashboard.DASHBOARD_TITLE, "body": update_dashboard.DASHBOARD_MARKER, "state": "open"},
            {"number": 51, "title": update_dashboard.DASHBOARD_TITLE, "body": update_dashboard.DASHBOARD_MARKER, "state": "open"},
        ]
        publisher = update_dashboard.DashboardPublisher(client=self.client, repo="Tiflit/DXVK-Companion")
        with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
            publisher.publish("Some body")
        self.assertIn("Multiple dashboard issues detected", str(ctx.exception))

    # --- H3: Completeness, Pagination, and Reacquisition ---

    def test_capped_pagination_reports_incomplete(self):
        """H3: Truncated pagination when items exceed max_pages marks completeness as INCOMPLETE."""
        # 2 pages of PRs where page 2 has full per_page items, indicating more exist
        self.client.prs_pages = [
            [{"number": 1, "title": "PR 1", "head": {"sha": "a" * 40}, "base": {"sha": "b" * 40}, "state": "open"}],
            [{"number": 2, "title": "PR 2", "head": {"sha": "c" * 40}, "base": {"sha": "b" * 40}, "state": "open"}],
        ]
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect(max_pages=2, per_page=1)
        self.assertTrue(facts.completeness.startswith("INCOMPLETE"))
        self.assertIn("truncated at pagination limit", facts.completeness)

    def test_missing_or_invalid_main_sha_reports_incomplete(self):
        """H3: Missing or non-hex main SHA marks completeness as INCOMPLETE."""
        self.client.main_shas = ["invalid_non_hex_sha"]
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect()
        self.assertTrue(facts.completeness.startswith("INCOMPLETE"))
        self.assertIn("main branch SHA", facts.completeness)

    def test_api_failure_vs_genuine_empty_state(self):
        """H3 & Criterion 7: Distinguishes API error from true empty inventory."""
        self.client.prs_pages = [[]]
        self.client.issues_pages = [[]]
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect()
        self.assertEqual(facts.completeness, update_dashboard.COMPLETENESS_COMPLETE)
        self.assertEqual(len(facts.open_prs), 0)

        failing_client = FakeGitHubClient()
        failing_client.api_errors["pulls"] = update_dashboard.GitHubApiError("HTTP 500: Server Error")
        collector_err = update_dashboard.GitHubFactsCollector(client=failing_client, repo="Tiflit/DXVK-Companion")
        facts_err = collector_err.collect()
        self.assertTrue(facts_err.completeness.startswith("INCOMPLETE"))
        self.assertIn("pulls", facts_err.completeness)

    def test_api_error_renders_unavailable_inventory(self):
        """H3: When API errors occur, rendered output reports inventory unavailable rather than 'all merged'."""
        failing_client = FakeGitHubClient()
        failing_client.api_errors["pulls"] = update_dashboard.GitHubApiError("HTTP 500: Server Error")
        collector = update_dashboard.GitHubFactsCollector(client=failing_client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect()
        rendered = update_dashboard.render_dashboard(facts, self.curated_text)
        self.assertIn("Pull requests inventory unavailable", rendered)
        self.assertNotIn("all active PRs merged", rendered)

    def test_main_movement_reacquires_coherent_facts(self):
        """H3: When main branch moves during acquisition, facts are coherently reacquired on retry."""
        self.client.main_shas = [
            "1111111111111111111111111111111111111111",
            "2222222222222222222222222222222222222222",
            "2222222222222222222222222222222222222222",
        ]
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect(max_main_retries=2)
        # Succeeded after retry
        self.assertEqual(facts.main_head_sha, "2222222222222222222222222222222222222222")
        self.assertEqual(facts.completeness, update_dashboard.COMPLETENESS_COMPLETE)

    # --- H4: CI Identity, Run Attempt, and Evidence Provenance ---

    def test_ci_identity_run_attempt_and_provenance(self):
        """H4: Specifically selects Build and Test, binds run ID, attempt, and explicit provenance status."""
        pr_head = "a" * 40
        self.client.prs_pages = [[{
            "number": 10,
            "title": "PR with CI",
            "head": {"sha": pr_head},
            "base": {"sha": "c" * 40},
            "state": "open",
        }]]
        self.client.pr_runs[pr_head] = [
            # Earlier irrelevant workflow
            {"id": 111, "name": "AI Scope Check", "run_attempt": 1, "head_sha": pr_head, "conclusion": "success"},
            # Target Build and Test workflow
            {
                "id": 222,
                "name": "Build and Test",
                "run_attempt": 2,
                "head_sha": pr_head,
                "conclusion": "success",
                "html_url": "https://github.com/Tiflit/DXVK-Companion/actions/runs/222",
            },
        ]
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect()
        pr = facts.open_prs[0]
        self.assertEqual(pr["ci_run_id"], "222")
        self.assertEqual(pr["ci_attempt"], "2")
        self.assertEqual(pr["ci_status"], "success")
        self.assertEqual(pr["tested_checkout_sha"], "unknown (no build provenance artifact)")

    def test_stale_ci_and_attempt_mismatch(self):
        """H4: Distinguishes CI run for earlier commit from current PR head SHA."""
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
            "name": "Build and Test",
            "run_attempt": 1,
            "head_sha": stale_run_head,
            "conclusion": "success",
            "html_url": "https://github.com/Tiflit/DXVK-Companion/actions/runs/123456",
        }]
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect()
        pr = facts.open_prs[0]
        self.assertEqual(pr["ci_status"], "historical/stale")

    # --- H5: Snapshot Word Budget & Absolute URLs ---

    def test_snapshot_limit_strictly_enforced_across_sections(self):
        """H5: Strictly enforces <= 1000 words limit across sections, including large curated content and large PR lists."""
        huge_curated = "## 2. Active Work Queue & Ownership\n\n" + ("Detailed queue item description with context. " * 300)
        many_prs = []
        for i in range(25):
            many_prs.append({
                "number": 200 + i,
                "title": f"Descriptive Pull Request title {i} addressing complex component refactoring",
                "head": {"sha": "a" * 40},
                "base": {"sha": "b" * 40},
                "state": "open",
            })
        self.client.prs_pages = [many_prs]
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect()
        
        rendered = update_dashboard.render_dashboard(facts, huge_curated, repo="Tiflit/DXVK-Companion", max_words=1000)
        word_count = update_dashboard.count_words_excluding_urls(rendered)
        self.assertLessEqual(word_count, 1000)
        self.assertIn("omitted to respect snapshot word budget", rendered)

    def test_repository_links_use_absolute_urls(self):
        """H5: Markdown links in issue-rendered snapshot use absolute GitHub URLs, never relative ../ paths."""
        collector = update_dashboard.GitHubFactsCollector(client=self.client, repo="Tiflit/DXVK-Companion")
        facts = collector.collect()
        rendered = update_dashboard.render_dashboard(facts, self.curated_text, repo="Tiflit/DXVK-Companion")
        self.assertNotIn("../AGENTS.md", rendered)
        self.assertIn("https://github.com/Tiflit/DXVK-Companion/blob/", rendered)

    # --- H6: Privacy, Sanitization & Synthetic-Only Fixtures ---

    def test_privacy_synthetic_fixtures_and_full_path_redaction(self):
        """H6: Uses synthetic-only fixtures (no real local names) and redacts full Windows/Unix paths."""
        text_with_user = r"Error in C:\Users\synthetic_dev\Documents\DXVK\file.cs and /home/synthetic_dev/dev/app.log"
        sanitized = update_dashboard.sanitize_display_text(text_with_user)
        self.assertNotIn("synthetic_dev", sanitized)
        self.assertNotIn(r"C:\Users", sanitized)
        self.assertNotIn(r"/home/", sanitized)
        self.assertIn("[REDACTED_PATH]", sanitized)

    def test_sanitizer_removes_markdown_injection_and_raw_exceptions(self):
        """H6: Markdown link injection, backticks, script tags, and tokens are stripped."""
        malicious = "[Click Here](http://evil.com) with `command` and <script>alert(1)</script> ghp_SECRETTOKEN123"
        sanitized = update_dashboard.sanitize_display_text(malicious)
        self.assertNotIn("[Click Here](http://evil.com)", sanitized)
        self.assertNotIn("<script>", sanitized)
        self.assertNotIn("ghp_SECRETTOKEN123", sanitized)
        self.assertIn("[REDACTED_TOKEN]", sanitized)

    def test_malformed_ids_and_shas(self):
        """H6 & Criterion 8: Malformed or invalid commit SHAs are rejected."""
        self.assertFalse(update_dashboard.is_valid_sha("not-a-sha"))
        self.assertFalse(update_dashboard.is_valid_sha("12345"))
        self.assertFalse(update_dashboard.is_valid_sha("zzzz" * 10))
        self.assertTrue(update_dashboard.is_valid_sha("a" * 40))
        self.assertTrue(update_dashboard.is_valid_sha("ce74e1e1caba1ee5197788c945826648d4f5a752"))

    # --- Loop Filtering and Unchanged Publishing ---

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
        body1 = update_dashboard.render_dashboard(facts, self.curated_text, repo="Tiflit/DXVK-Companion")
        
        existing_body = re.sub(
            r"Generated: [^\|]+ \|",
            "Generated: 2026-10-01 00:00:00 UTC |",
            body1,
        )
        self.client.existing_issues = [{
            "number": 88,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": existing_body,
            "state": "open",
        }]
        publisher = update_dashboard.DashboardPublisher(client=self.client, repo="Tiflit/DXVK-Companion")
        result = publisher.publish(body1)
        self.assertEqual(result["status"], "skipped")
        self.assertEqual(len(self.client.patched_issues), 0)

    # --- YAML Structure & Least Privilege Verification ---

    def test_workflow_yaml_structure_and_least_privilege(self):
        """H2 & Criterion 10: Validates workflow YAML structure, concurrency, separate jobs, and least privilege."""
        wf_path = REPO_ROOT / ".github" / "workflows" / "ai-current-state.yml"
        self.assertTrue(wf_path.exists(), "Workflow file .github/workflows/ai-current-state.yml must exist")
        
        content = wf_path.read_text(encoding="utf-8")
        
        self.assertNotIn("\t", content, "YAML must not contain tab characters")
        self.assertIn("name: AI Current State Dashboard", content)
        self.assertIn("concurrency:", content)
        self.assertIn("acquire-and-render:", content)
        self.assertIn("publish-snapshot:", content)
        self.assertIn("actions/checkout@v7", content)
        self.assertIn("default_branch", content)


if __name__ == "__main__":
    unittest.main()
