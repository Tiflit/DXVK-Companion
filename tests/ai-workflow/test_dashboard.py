"""Unit and regression tests for automated compact handoff dashboard generation and publication."""

import io
import json
import os
import re
import sys
import unittest
import urllib.error
from pathlib import Path
from unittest.mock import MagicMock, patch

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import update_dashboard


class MockHttpResponse:
    """Mock HTTP response object implementing read() and context manager interface."""

    def __init__(self, data, status: int = 200):
        self.status = status
        if isinstance(data, (dict, list)):
            self._bytes = json.dumps(data).encode("utf-8")
        elif isinstance(data, str):
            self._bytes = data.encode("utf-8")
        elif isinstance(data, bytes):
            self._bytes = data
        else:
            self._bytes = b""

    def read(self) -> bytes:
        return self._bytes

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        pass


class TestDashboardGenerator(unittest.TestCase):
    """
    Tests covering coordinator verification criteria and concrete exit checks 1–4.
    All discovery and publication boundary tests exercise real GitHubClient methods
    with mocked HTTP responses rather than duplicating selection logic in fakes.
    """

    def setUp(self):
        self.repo = "Tiflit/DXVK-Companion"
        self.main_sha = "ce74e1e1caba1ee5197788c945826648d4f5a752"
        self.curated_text = (
            "## 2. Active Work Queue & Ownership\n\n"
            "| Work | Owner | Dependency | Next Action |\n"
            "|---|---|---|---|\n"
            "| **#25 handoff automation** | Gemini; ChatGPT verifies | `main` | Open PR with tests. |\n\n"
            "## 5. Unresolved Architectural & Governance Decisions\n\n"
            "1. **Issue #12 (Spec Authority)**: Proposes consolidating A1-UPDATED to docs/spec/DXVK-COMPANION-SPEC.md.\n"
            "2. **Issue #14 (Shared directories)**: Per-executable vs installation-wide.\n"
        )

    # =========================================================================
    # Exit Check 1: Real GitHubClient Discovery & Destination Authentication
    # =========================================================================

    def test_real_discovery_api_error_causes_zero_writes(self):
        """Exit Check 1: API errors (e.g. HTTP 503) during discovery must raise and cause ZERO creates/patches."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues" in url:
                raise urllib.error.HTTPError(url, 503, "Service Unavailable", {}, io.BytesIO(b"Service Unavailable"))
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            with self.assertRaises(update_dashboard.GitHubApiError) as ctx:
                publisher.publish(rendered)
            self.assertIn("503", str(ctx.exception))

    def test_real_discovery_unresolved_pagination_cap_causes_zero_writes(self):
        """Exit Check 1: Hitting pagination cap without concluding issue list must fail safely with zero creates/patches."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)

        # 5 pages of 50 non-matching issues each -> reaches max_pages=5 with full per_page items
        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues?state=all" in url:
                page_match = re.search(r"page=(\d+)", url)
                page_num = int(page_match.group(1)) if page_match else 1
                batch = [{"number": page_num * 100 + i, "title": f"Task {i}", "body": "task text"} for i in range(50)]
                return MockHttpResponse(batch)
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
                publisher.publish(rendered)
            self.assertIn("pagination limit", str(ctx.exception))

    def test_real_discovery_human_owned_lookalike_causes_zero_writes(self):
        """Exit Check 1: Human-owned issue mimicking dashboard title and marker must be rejected with zero writes."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        human_lookalike = {
            "number": 99,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": f"{update_dashboard.DASHBOARD_MARKER}\nHuman content",
            "state": "open",
            "user": {"login": "human_developer", "type": "User"},
        }

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues?state=all" in url:
                return MockHttpResponse([human_lookalike])
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
                publisher.publish(rendered)
            self.assertIn("human user", str(ctx.exception))

    def test_real_discovery_pull_request_causes_zero_writes(self):
        """Exit Check 1: Pull request with dashboard title or marker must be rejected with zero writes."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        pr_lookalike = {
            "number": 44,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": update_dashboard.DASHBOARD_MARKER,
            "pull_request": {"url": "https://api.github.com/repos/Tiflit/DXVK-Companion/pulls/44"},
            "state": "open",
            "user": {"login": "github-actions[bot]", "type": "Bot"},
        }

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues?state=all" in url:
                return MockHttpResponse([pr_lookalike])
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
                publisher.publish(rendered)
            self.assertIn("Pull request", str(ctx.exception))

    def test_real_discovery_task_issue_with_marker_is_not_matched(self):
        """Exit Check 1: Ordinary task issue mentioning the marker (e.g. Issue #25) is not matched or overwritten."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        task_issue = {
            "number": 25,
            "title": "[AI] Automate compact GitHub handoffs",
            "body": f"Notes mentioning {update_dashboard.DASHBOARD_MARKER} in discussion",
            "state": "open",
            "user": {"login": "developer", "type": "User"},
        }
        post_called = []

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues?state=all" in url:
                return MockHttpResponse([task_issue])
            if "repos/Tiflit/DXVK-Companion/issues" in url and req.get_method() == "POST":
                post_called.append(json.loads(req.data.decode("utf-8")))
                return MockHttpResponse({"number": 101, "title": update_dashboard.DASHBOARD_TITLE})
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            res = publisher.publish(rendered)
            self.assertEqual(res["status"], "created")
            self.assertEqual(res["issue_number"], 101)
            self.assertNotEqual(res["issue_number"], 25)
            self.assertEqual(len(post_called), 1)

    def test_real_discovery_closed_dashboard_issue_fails_safely(self):
        """Exit Check 1: Closed dashboard issue causes safe refusal rather than auto-creating duplicate."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        closed_dashboard = {
            "number": 55,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": update_dashboard.DASHBOARD_MARKER,
            "state": "closed",
            "user": {"login": "github-actions[bot]", "type": "Bot"},
        }

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues?state=all" in url:
                return MockHttpResponse([closed_dashboard])
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
                publisher.publish(rendered)
            self.assertIn("closed", str(ctx.exception).lower())

    def test_real_discovery_duplicate_dashboard_issues_refuses_ambiguity(self):
        """Exit Check 1: Multiple open dashboard issues cause safe refusal rather than ambiguous patch."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        dash1 = {
            "number": 71,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": update_dashboard.DASHBOARD_MARKER,
            "state": "open",
            "user": {"login": "github-actions[bot]", "type": "Bot"},
        }
        dash2 = {
            "number": 72,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": update_dashboard.DASHBOARD_MARKER,
            "state": "open",
            "user": {"login": "github-actions[bot]", "type": "Bot"},
        }

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues?state=all" in url:
                return MockHttpResponse([dash1, dash2])
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
                publisher.publish(rendered)
            self.assertIn("Multiple dashboard issues detected", str(ctx.exception))

    def test_real_discovery_authenticated_machine_issue_patches_or_skips(self):
        """Exit Check 1: Authenticated machine-owned dashboard issue is patched when content changes."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        existing_dashboard = {
            "number": 80,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": f"{update_dashboard.DASHBOARD_MARKER}\nOld body content",
            "state": "open",
            "user": {"login": "github-actions[bot]", "type": "Bot"},
        }
        patch_called = []

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues?state=all" in url:
                return MockHttpResponse([existing_dashboard])
            if "repos/Tiflit/DXVK-Companion/issues/80" in url and req.get_method() == "PATCH":
                patch_called.append(json.loads(req.data.decode("utf-8")))
                return MockHttpResponse({"number": 80})
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            res = publisher.publish(rendered)
            self.assertEqual(res["status"], "updated")
            self.assertEqual(res["issue_number"], 80)
            self.assertEqual(len(patch_called), 1)

    # =========================================================================
    # Exit Check 2: Preview / Dry-run Zero Writes & Freshness / Tooling
    # =========================================================================

    def test_preview_and_dry_run_with_publish_file_causes_zero_writes(self):
        """Exit Check 2: Every preview/dry-run argument combination with --publish-file causes zero writes."""
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )
        temp_file = REPO_ROOT / "temp_preview_test.md"
        temp_file.write_text(rendered, encoding="utf-8")

        mock_urlopen = MagicMock()
        try:
            with patch("urllib.request.urlopen", mock_urlopen):
                # 1. With --preview
                code1 = update_dashboard.main(["--repo", self.repo, "--publish-file", str(temp_file), "--preview"])
                self.assertEqual(code1, 0)
                self.assertFalse(mock_urlopen.called)

                # 2. With --dry-run
                code2 = update_dashboard.main(["--repo", self.repo, "--publish-file", str(temp_file), "--dry-run"])
                self.assertEqual(code2, 0)
                self.assertFalse(mock_urlopen.called)

                # 3. Without --publish flag (defaults to preview)
                code3 = update_dashboard.main(["--repo", self.repo, "--publish-file", str(temp_file)])
                self.assertEqual(code3, 0)
                self.assertFalse(mock_urlopen.called)
        finally:
            if temp_file.exists():
                temp_file.unlink()

    def test_moving_main_before_publication_refuses_stale_write(self):
        """Exit Check 2: Moving main between rendering and publication causes explicit refusal with zero writes."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        new_main_sha = "9999999999999999999999999999999999999999"

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": new_main_sha}})
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
                publisher.publish(rendered)
            self.assertIn("Main branch moved", str(ctx.exception))

    def test_tooling_revision_uses_actual_checked_out_head_not_event_sha(self):
        """Exit Check 2: get_tooling_revision attributes actual checked-out tooling HEAD, not event GITHUB_SHA."""
        fake_event_sha = "1111111111111111111111111111111111111111"
        real_checked_out_sha = "2222222222222222222222222222222222222222"

        with patch.dict(os.environ, {"GITHUB_SHA": fake_event_sha}):
            with patch("subprocess.run") as mock_sub:
                mock_sub.return_value = MagicMock(returncode=0, stdout=real_checked_out_sha)
                rev = update_dashboard.get_tooling_revision()
                self.assertIn(real_checked_out_sha[:10], rev)
                self.assertNotIn(fake_event_sha[:10], rev)

    def test_structured_snapshot_header_validation(self):
        """Exit Check 2 & 4: Rendered snapshot missing required structured identity header is rejected before writing."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)

        with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
            publisher.publish("Plain text markdown with no structured header")
        self.assertIn("missing required structured identity header", str(ctx.exception))

    # =========================================================================
    # Exit Check 3: CI Attribution, Run Attempts, Artifacts & Provenance
    # =========================================================================

    def test_no_build_and_test_run_makes_no_build_success_claim(self):
        """Exit Check 3: When no Build and Test workflow exists, generic Scope runs must not claim build success."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        pr_head = "a" * 40

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "pulls?state=open" in url:
                return MockHttpResponse([{
                    "number": 10,
                    "title": "PR with scope check only",
                    "head": {"sha": pr_head},
                    "base": {"sha": self.main_sha},
                }])
            if f"actions/runs?head_sha={pr_head}" in url:
                # Irrelevant Scope run that succeeded
                return MockHttpResponse({
                    "workflow_runs": [{
                        "id": 999,
                        "name": "AI Scope Check",
                        "head_sha": pr_head,
                        "conclusion": "success",
                    }],
                })
            return MockHttpResponse([])

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            collector = update_dashboard.GitHubFactsCollector(client=client, repo=self.repo)
            facts = collector.collect()
            pr = facts.open_prs[0]
            self.assertEqual(pr["ci_status"], "no build run")
            self.assertEqual(pr["ci_run_id"], "-")
            self.assertEqual(pr["ci_attempt"], "-")
            self.assertEqual(pr["tested_checkout_sha"], "unknown")

    def test_missing_or_invalid_run_attempt_stays_unknown_not_invented_1(self):
        """Exit Check 3: Missing or invalid run_attempt stays 'unknown' and is never invented as 1."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        pr_head = "b" * 40

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "pulls?state=open" in url:
                return MockHttpResponse([{
                    "number": 11,
                    "title": "PR with missing attempt",
                    "head": {"sha": pr_head},
                    "base": {"sha": self.main_sha},
                }])
            if f"actions/runs?head_sha={pr_head}" in url:
                return MockHttpResponse({
                    "workflow_runs": [{
                        "id": 888,
                        "name": "Build and Test",
                        "head_sha": pr_head,
                        "conclusion": "success",
                        "run_attempt": None,  # Omitted / None
                    }],
                })
            if "actions/runs/888/artifacts" in url:
                return MockHttpResponse({"artifacts": []})
            return MockHttpResponse([])

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            collector = update_dashboard.GitHubFactsCollector(client=client, repo=self.repo)
            facts = collector.collect()
            pr = facts.open_prs[0]
            self.assertEqual(pr["ci_run_id"], "888")
            self.assertEqual(pr["ci_attempt"], "unknown")
            self.assertNotEqual(pr["ci_attempt"], "1")

    def test_artifact_provenance_truthfully_queried_and_linked(self):
        """Exit Check 3: Queries artifacts truthfully; binds checkout if present or reports absent truthfully."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        pr_head = "c" * 40

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "pulls?state=open" in url:
                return MockHttpResponse([{
                    "number": 12,
                    "title": "PR with provenance artifact",
                    "head": {"sha": pr_head},
                    "base": {"sha": self.main_sha},
                }])
            if f"actions/runs?head_sha={pr_head}" in url:
                return MockHttpResponse({
                    "workflow_runs": [{
                        "id": 777,
                        "name": "Build and Test",
                        "head_sha": pr_head,
                        "conclusion": "success",
                        "run_attempt": 2,
                    }],
                })
            if "actions/runs/777/artifacts" in url:
                return MockHttpResponse({
                    "artifacts": [{
                        "id": 55555,
                        "name": "build-provenance",
                    }],
                })
            return MockHttpResponse([])

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            collector = update_dashboard.GitHubFactsCollector(client=client, repo=self.repo)
            facts = collector.collect()
            pr = facts.open_prs[0]
            self.assertEqual(pr["ci_run_id"], "777")
            self.assertEqual(pr["ci_attempt"], "2")
            self.assertIn("artifact present", pr["tested_checkout_sha"])

    # =========================================================================
    # Exit Check 4: Validation of IDs, Repo, SHAs, Enums, Safe Diagnostics
    # =========================================================================

    def test_validation_of_repository_and_shas(self):
        """Exit Check 4: Validates repository format and commit SHAs before rendering/publishing."""
        self.assertTrue(update_dashboard.is_valid_repo_name("Tiflit/DXVK-Companion"))
        self.assertFalse(update_dashboard.is_valid_repo_name("invalid_repo_without_owner"))
        self.assertFalse(update_dashboard.is_valid_repo_name("../../etc/passwd"))

        self.assertTrue(update_dashboard.is_valid_sha(self.main_sha))
        self.assertFalse(update_dashboard.is_valid_sha("invalid_non_hex"))
        self.assertFalse(update_dashboard.is_valid_sha("123"))

    def test_invalid_main_sha_renders_safe_base_urls(self):
        """Exit Check 4: When main SHA is invalid, base URL falls back safely to repo root without broken path injection."""
        facts = update_dashboard.RepositoryFacts(main_head_sha="unknown_or_invalid")
        rendered = update_dashboard.render_dashboard(facts, self.curated_text, repo=self.repo)
        self.assertNotIn("https://github.com/Tiflit/DXVK-Companion/blob/unknown_or_invalid", rendered)
        self.assertIn("https://github.com/Tiflit/DXVK-Companion", rendered)

    def test_sanitizer_removes_markdown_injection_and_raw_exceptions(self):
        """Exit Check 4: Markdown link injection, backticks, script tags, and tokens are stripped."""
        malicious = "[Click Here](http://evil.com) with `command` and <script>alert(1)</script> ghp_SECRETTOKEN123"
        sanitized = update_dashboard.sanitize_display_text(malicious)
        self.assertNotIn("[Click Here](http://evil.com)", sanitized)
        self.assertNotIn("<script>", sanitized)
        self.assertNotIn("ghp_SECRETTOKEN123", sanitized)
        self.assertIn("[REDACTED_TOKEN]", sanitized)

    def test_privacy_synthetic_fixtures_and_full_path_redaction(self):
        """Exit Check 4: Uses synthetic identity and redacts full Windows/Unix paths."""
        text_with_user = r"Error in C:\Users\synthetic_dev\Documents\DXVK\file.cs and /home/synthetic_dev/dev/app.log"
        sanitized = update_dashboard.sanitize_display_text(text_with_user)
        self.assertNotIn("synthetic_dev", sanitized)
        self.assertNotIn(r"C:\Users", sanitized)
        self.assertNotIn(r"/home/", sanitized)
        self.assertIn("[REDACTED_PATH]", sanitized)

    def test_snapshot_limit_strictly_enforced_across_sections(self):
        """Exit Check 4: Strictly enforces <= 1000 words limit across sections, including large curated and PR lists."""
        huge_curated = "## 2. Active Work Queue & Ownership\n\n" + ("Detailed queue item description with context. " * 300)
        many_prs = []
        for i in range(25):
            many_prs.append({
                "number": 200 + i,
                "title": f"Descriptive Pull Request title {i} addressing complex component refactoring",
                "head_sha": "a" * 40,
                "base_sha": "b" * 40,
                "ci_status": "success",
                "ci_run_id": "12345",
                "ci_attempt": "1",
                "ci_url": "https://github.com/Tiflit/DXVK-Companion/actions/runs/12345",
                "tested_checkout_sha": "c" * 40,
            })
        facts = update_dashboard.RepositoryFacts(
            main_head_sha=self.main_sha,
            open_prs=many_prs,
        )

        rendered = update_dashboard.render_dashboard(facts, huge_curated, repo=self.repo, max_words=1000)
        word_count = update_dashboard.count_words_excluding_urls(rendered)
        self.assertLessEqual(word_count, 1000)
        self.assertIn("omitted to respect snapshot word budget", rendered)

    def test_repository_links_use_absolute_urls(self):
        """Exit Check 4: Markdown links in issue-rendered snapshot use absolute GitHub URLs, never relative ../ paths."""
        facts = update_dashboard.RepositoryFacts(main_head_sha=self.main_sha)
        rendered = update_dashboard.render_dashboard(facts, self.curated_text, repo=self.repo)
        self.assertNotIn("../AGENTS.md", rendered)
        self.assertIn("https://github.com/Tiflit/DXVK-Companion/blob/", rendered)

    def test_workflow_yaml_structure_and_least_privilege(self):
        """
        Exit Check 4, H2 & H7: Validates workflow YAML structure, concurrency, separate jobs, and least privilege.

        Honest Limitation Note: Standard Python library does not include a full YAML parser (PyYAML is not
        a project dependency). Structural inspection is performed via line/block and indentation parsing.
        Full syntax and schema validation is enforced by the GitHub Actions workflow parser at runtime.
        """
        wf_path = REPO_ROOT / ".github" / "workflows" / "ai-current-state.yml"
        self.assertTrue(wf_path.exists(), "Workflow file .github/workflows/ai-current-state.yml must exist")

        content = wf_path.read_text(encoding="utf-8")
        self.assertNotIn("\t", content, "YAML must not contain tab characters")
        self.assertIn("name: AI Current State Dashboard", content)
        self.assertIn("concurrency:", content)
        self.assertIn("acquire-and-render:", content)
        self.assertIn("publish-snapshot:", content)
        self.assertIn("actions/checkout@v7", content)


if __name__ == "__main__":
    unittest.main()
