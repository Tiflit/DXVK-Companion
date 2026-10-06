"""Automates compact current-state facts and dashboard orientation for AI agent sessions."""

from __future__ import annotations

import argparse
import datetime
import json
import os
import posixpath
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

DASHBOARD_TITLE = "[AI Dashboard] Current Repository State & Handoff Orientation"
DASHBOARD_MARKER = "<!-- AI-DASHBOARD-MARKER: v1 -->"
DASHBOARD_BOT_LOGIN = "github-actions[bot]"
COMPLETENESS_COMPLETE = "COMPLETE"

VALID_CI_CONCLUSIONS = {
    "success",
    "failure",
    "neutral",
    "cancelled",
    "timed_out",
    "action_required",
    "stale",
    "historical/stale",
    "in_progress",
    "queued",
    "none",
    "no build run",
    "unavailable",
    "unknown",
}


class SecurityValidationError(Exception):
    """Raised when security boundaries, injection risks, or duplicate targets are detected."""
    pass


class GitHubApiError(Exception):
    """Raised when GitHub API requests fail."""
    pass


def is_valid_sha(sha: Optional[str]) -> bool:
    """Validates that a string is a 40-character hex commit SHA."""
    if not sha or not isinstance(sha, str):
        return False
    return bool(re.match(r"^[0-9a-fA-F]{40}$", sha.strip()))


def is_valid_repo_name(repo: Optional[str]) -> bool:
    """Validates that a repository name matches owner/repo format."""
    if not repo or not isinstance(repo, str):
        return False
    return bool(re.match(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", repo.strip()))


def is_valid_positive_int(val: Any) -> bool:
    """Validates that a value is a positive integer or positive integer digit string."""
    if isinstance(val, int) and val > 0:
        return True
    if isinstance(val, str) and val.isdigit() and int(val) > 0:
        return True
    return False


def get_tooling_revision(repo_root: Optional[Path] = None) -> str:
    """
    Returns the tooling script path and commit SHA of actual checked-out tooling HEAD.
    Attributes the actual git commit in the checked-out workspace first, not event GITHUB_SHA.
    Emits full 40-character hex commit SHA.
    """
    script_rel = "scripts/ai-workflow/update_dashboard.py"
    sha = None
    try:
        cwd = str(repo_root) if repo_root else None
        res = subprocess.run(["git", "rev-parse", "HEAD"], cwd=cwd, capture_output=True, text=True, timeout=5)
        if res.returncode == 0 and res.stdout.strip():
            candidate = res.stdout.strip()
            if is_valid_sha(candidate):
                sha = candidate
    except Exception:
        pass

    # Only fall back to TOOLING_SHA if git is unavailable
    if not sha:
        env_sha = os.environ.get("TOOLING_SHA")
        if env_sha and is_valid_sha(env_sha):
            sha = env_sha

    if sha and is_valid_sha(sha):
        return f"{script_rel}@{sha}"
    return script_rel


def sanitize_display_text(text: Optional[str], max_len: int = 100) -> str:
    """
    Sanitizes untrusted display text for safe inclusion in Markdown and tables.
    Redacts full local paths, usernames, auth tokens, removes markdown link injection,
    strips HTML/backticks, escapes table pipes, and bounds length.
    """
    if not text:
        return ""

    sanitized = re.sub(r"[\r\n\t]+", " ", str(text))

    # Strip HTML tags
    sanitized = re.sub(r"<[^>]+>", "", sanitized)

    # Strip Markdown link syntax: [title](url) -> title
    sanitized = re.sub(r"\[([^\]]+)\]\([^\)]+\)", r"\1", sanitized)

    # Strip backticks
    sanitized = sanitized.replace("`", "")

    # Redact full local Windows drive paths: C:\Users\... or D:\dev\...
    sanitized = re.sub(r"[A-Za-z]:\\[^:\r\n\t|<>\"`]+", "[REDACTED_PATH]", sanitized)

    # Redact full Unix paths: /home/... or /Users/...
    sanitized = re.sub(r"/(?:home|Users)/[^/\s]+(?:/[^\s]*)?", "[REDACTED_PATH]", sanitized)

    # Redact tokens (ghp_, github_pat_, Bearer ...)
    sanitized = re.sub(r"(?:ghp_|github_pat_|bearer\s+)[A-Za-z0-9_]+", "[REDACTED_TOKEN]", sanitized, flags=re.I)

    # Escape Markdown table pipes
    sanitized = sanitized.replace("|", "/")

    # Collapse consecutive whitespace
    sanitized = re.sub(r"\s+", " ", sanitized).strip()

    if len(sanitized) > max_len:
        sanitized = sanitized[: max_len - 3].rstrip() + "..."

    return sanitized


def sanitize_diagnostic(msg: str) -> str:
    """Sanitizes diagnostic and error messages to prevent leaking paths, tokens, or raw queries."""
    if not msg:
        return ""
    clean = re.sub(r"[A-Za-z]:\\[^:\r\n\t|<>\"`]+", "[REDACTED_PATH]", str(msg))
    clean = re.sub(r"/(?:home|Users)/[^/\s]+(?:/[^\s]*)?", "[REDACTED_PATH]", clean)
    clean = re.sub(r"(?:ghp_|github_pat_|bearer\s+)[A-Za-z0-9_]+", "[REDACTED_TOKEN]", clean, flags=re.I)
    clean = re.sub(r"\?[\w=&-]+", "?[REDACTED_QUERY]", clean)
    return clean.strip()


def count_words_excluding_urls(text: str) -> int:
    """Counts whitespace-separated words excluding URLs and Markdown formatting symbols."""
    no_urls = re.sub(r"https?://\S+", "", text)
    cleaned = re.sub(r"[#|\-*`_>~]", " ", no_urls)
    return len(cleaned.split())


@dataclass
class RepositoryFacts:
    """Structured facts collected from GitHub API."""
    main_head_sha: str
    open_prs: List[Dict[str, Any]] = field(default_factory=list)
    open_issues: List[Dict[str, Any]] = field(default_factory=list)
    main_runs: List[Dict[str, Any]] = field(default_factory=list)
    completeness: str = COMPLETENESS_COMPLETE
    capture_time_utc: str = ""
    queries_executed: List[str] = field(default_factory=list)
    source_repo: str = ""
    prs_error: Optional[str] = None
    prs_truncated: bool = False
    issues_error: Optional[str] = None
    issues_truncated: bool = False
    open_observations_count: Optional[int] = None
    observations_truncated: bool = False
    observations_error: Optional[str] = None


class GitHubClient:
    """Standard-library HTTP client for GitHub REST API."""

    def __init__(self, token: Optional[str] = None, repo: Optional[str] = None, base_url: str = "https://api.github.com"):
        self.token = token
        self.repo = repo
        self.base_url = base_url.rstrip("/")

    def _request(self, method: str, endpoint: str, body: Optional[Dict[str, Any]] = None, timeout: int = 30) -> Any:
        url = f"{self.base_url}/{endpoint.lstrip('/')}"
        headers = {
            "Accept": "application/vnd.github.v3+json",
            "User-Agent": "DXVK-Companion-AI-Workflow",
        }
        if self.token:
            headers["Authorization"] = f"Bearer {self.token}"

        data = None
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"

        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                content = resp.read()
                if not content:
                    return {}
                try:
                    return json.loads(content.decode("utf-8"))
                except Exception:
                    return content
        except urllib.error.HTTPError as e:
            msg = e.read().decode("utf-8", errors="replace")
            clean_msg = sanitize_diagnostic(msg)
            raise GitHubApiError(f"HTTP {e.code} on {method} {url}: {clean_msg}") from e
        except urllib.error.URLError as e:
            clean_reason = sanitize_diagnostic(str(e.reason))
            raise GitHubApiError(f"Network error on {method} {url}: {clean_reason}") from e

    def get_ref(self, ref: str) -> Dict[str, Any]:
        return self._request("GET", f"repos/{self.repo}/git/ref/{ref}")

    def fetch_paged(self, endpoint: str, per_page: int = 30, page: int = 1) -> List[Dict[str, Any]]:
        sep = "&" if "?" in endpoint else "?"
        url = f"repos/{self.repo}/{endpoint}{sep}per_page={per_page}&page={page}"
        return self._request("GET", url)

    def get_runs_for_commit(self, commit_sha: str) -> List[Dict[str, Any]]:
        url = f"repos/{self.repo}/actions/runs?head_sha={commit_sha}&per_page=20"
        res = self._request("GET", url)
        if isinstance(res, dict):
            return res.get("workflow_runs", [])
        return []

    def get_run_artifacts(self, run_id: int) -> List[Dict[str, Any]]:
        """Fetches artifacts list for a workflow run."""
        url = f"repos/{self.repo}/actions/runs/{run_id}/artifacts"
        res = self._request("GET", url)
        if isinstance(res, dict):
            return res.get("artifacts", [])
        return []

    def search_issues_paged(
        self,
        query: str,
        per_page: int = 30,
        page: int = 1,
        sort: str = "created",
        order: str = "desc",
    ) -> Dict[str, Any]:
        """
        Searches issues via GitHub Search API with pagination.
        Used for bounded queries that filter out labels or types before pagination.
        """
        params = f"q={urllib.parse.quote_plus(query)}&sort={sort}&order={order}&per_page={per_page}&page={page}"
        url = f"search/issues?{params}"
        res = self._request("GET", url)
        if not isinstance(res, dict):
            raise GitHubApiError(
                f"Unexpected response format during issue search on page {page}: expected dict, got {type(res).__name__}"
            )
        if not isinstance(res.get("items"), list):
            raise GitHubApiError(
                f"Unexpected response format during issue search on page {page}: missing or invalid 'items' field"
            )
        return res

    def search_issues(
        self,
        query: str = DASHBOARD_MARKER,
        max_pages: int = 5,
        per_page: int = 50,
    ) -> List[Dict[str, Any]]:
        """
        Searches issues across all states (open and closed) to locate the dedicated dashboard issue.
        Authenticates exact machine-owned identity (github-actions[bot]), title, and marker.
        Ignores unrelated human, PR, or other bot lookalikes (DoS prevention).
        Fails safely on API errors or unresolved pagination caps.
        """
        matched = []
        for page in range(1, max_pages + 1):
            issues = self._request("GET", f"repos/{self.repo}/issues?state=all&per_page={per_page}&page={page}")
            if not isinstance(issues, list):
                raise GitHubApiError(f"Unexpected response format during issue discovery on page {page}")

            if not issues:
                break

            for iss in issues:
                is_pr = bool(iss.get("pull_request"))
                title = (iss.get("title") or "").strip()
                body = iss.get("body") or ""

                has_marker = DASHBOARD_MARKER in body
                has_title = title == DASHBOARD_TITLE

                # Select strictly authenticated machine-owned dashboard destination (Item 4)
                if not is_pr and has_title and has_marker:
                    user_info = iss.get("user") or {}
                    user_login = (user_info.get("login") or "").strip()

                    # Require creator login exactly 'github-actions[bot]'.
                    # All other creators (human accounts, PRs, other bot accounts like
                    # unrelated-app[bot], dependabot[bot], etc.) are ignored so they neither
                    # receive writes nor veto discovery of a genuine dashboard.
                    if user_login == DASHBOARD_BOT_LOGIN:
                        matched.append(iss)
                    # Note: Unrelated human/PR/other-bot entries mimicking title/marker are ignored
                    # rather than raising an error, preventing denial-of-service via public issues.

            if len(issues) < per_page:
                break

            if page == max_pages:
                raise SecurityValidationError(
                    f"Dashboard discovery reached pagination limit ({max_pages} pages / {max_pages * per_page} items) "
                    "without concluding repository issue inventory; refusing creation to prevent duplicate."
                )

        return matched

    def create_issue(self, title: str, body: str, labels: Optional[List[str]] = None) -> Dict[str, Any]:
        payload: Dict[str, Any] = {"title": title, "body": body}
        if labels:
            payload["labels"] = labels
        return self._request("POST", f"repos/{self.repo}/issues", body=payload)

    def patch_issue(self, issue_number: int, body: str) -> Dict[str, Any]:
        return self._request("PATCH", f"repos/{self.repo}/issues/{issue_number}", body={"body": body})


class GitHubFactsCollector:
    """Collects repository facts, PR status, issue backlog, and CI runs."""

    def __init__(self, client: Any, repo: str):
        self.client = client
        self.repo = repo

    def _acquire_facts_once(self, max_pages: int, per_page: int) -> Tuple[RepositoryFacts, bool]:
        capture_time = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
        queries: List[str] = []
        completeness = COMPLETENESS_COMPLETE
        prs_error = None
        issues_error = None
        is_truncated = False

        # 1. Fetch main ref
        queries.append("ref:heads/main")
        try:
            ref_data = self.client.get_ref("heads/main")
            main_sha = ref_data.get("object", {}).get("sha", "unknown")
        except Exception as e:
            main_sha = "unknown"
            completeness = f"INCOMPLETE: Failed to fetch main ref: {sanitize_diagnostic(str(e))}"

        if not is_valid_sha(main_sha) and not completeness.startswith("INCOMPLETE"):
            completeness = f"INCOMPLETE: Missing or invalid main branch SHA ('{sanitize_diagnostic(main_sha)}')"

        # 2. Fetch open pull requests with pagination
        open_prs: List[Dict[str, Any]] = []
        prs_truncated = False
        queries.append("pulls?state=open")
        try:
            for page in range(1, max_pages + 1):
                page_prs = self.client.fetch_paged("pulls?state=open", per_page=per_page, page=page)
                if not page_prs:
                    break
                for pr in page_prs:
                    pr_num = pr.get("number")
                    if not is_valid_positive_int(pr_num):
                        continue
                    pr_num = int(pr_num)
                    pr_title = sanitize_display_text(pr.get("title"), max_len=70)
                    head_info = pr.get("head") or {}
                    base_info = pr.get("base") or {}
                    head_sha = head_info.get("sha") if isinstance(head_info, dict) else "unknown"
                    base_sha = base_info.get("sha") if isinstance(base_info, dict) else "unknown"
                    if not is_valid_sha(head_sha):
                        head_sha = "invalid-sha"
                    if not is_valid_sha(base_sha):
                        base_sha = "invalid-sha"

                    # Look up specifically Build and Test CI runs for PR head (Exit Check 3)
                    ci_status = "none"
                    ci_run_id = "-"
                    ci_attempt = "-"
                    ci_url = ""
                    ci_head_sha = "unknown"
                    build_provenance = "unknown"

                    if is_valid_sha(head_sha):
                        try:
                            runs = self.client.get_runs_for_commit(head_sha)
                            target_run = None
                            for r in runs:
                                if r.get("name") == "Build and Test":
                                    target_run = r
                                    break

                            if target_run:
                                raw_run_id = target_run.get("id")
                                if is_valid_positive_int(raw_run_id):
                                    ci_run_id = str(raw_run_id)
                                    ci_url = target_run.get("html_url") or f"https://github.com/{self.repo}/actions/runs/{ci_run_id}"

                                raw_attempt = target_run.get("run_attempt")
                                if is_valid_positive_int(raw_attempt):
                                    ci_attempt = str(raw_attempt)
                                else:
                                    ci_attempt = "unknown"

                                ci_head_sha = target_run.get("head_sha", "unknown")
                                conclusion = target_run.get("conclusion") or target_run.get("status") or "unknown"
                                if ci_head_sha != head_sha:
                                    ci_status = "historical/stale"
                                elif conclusion in VALID_CI_CONCLUSIONS:
                                    ci_status = conclusion
                                else:
                                    ci_status = "unknown"

                                # Truthfully query run artifacts (Item 2)
                                if is_valid_positive_int(raw_run_id):
                                    try:
                                        artifacts = self.client.get_run_artifacts(int(raw_run_id))
                                        has_provenance = any(a.get("name") == "build-provenance" for a in artifacts)
                                        if has_provenance:
                                            build_provenance = "present (checkout unparsed)"
                                        else:
                                            build_provenance = "not found in run"
                                    except Exception:
                                        build_provenance = "query failed"
                            else:
                                ci_status = "no build run"
                        except Exception:
                            ci_status = "unavailable"

                    open_prs.append({
                        "number": pr_num,
                        "title": pr_title,
                        "head_sha": head_sha,
                        "base_sha": base_sha,
                        "ci_status": ci_status,
                        "ci_run_id": ci_run_id,
                        "ci_url": ci_url,
                        "ci_attempt": ci_attempt,
                        "ci_head_sha": ci_head_sha,
                        "build_provenance": build_provenance,
                    })

                if page == max_pages and len(page_prs) == per_page:
                    prs_truncated = True
                    is_truncated = True
        except Exception as e:
            prs_error = sanitize_diagnostic(str(e))
            if completeness == COMPLETENESS_COMPLETE:
                completeness = f"INCOMPLETE: Failed to fetch open pulls: {prs_error}"

        # 3. Fetch open observations with bounded pagination
        open_observations_count: int = 0
        obs_error: Optional[str] = None
        obs_truncated: bool = False
        queries.append("issues?state=open&labels=ai-observation")
        try:
            for page in range(1, max_pages + 1):
                page_obs = self.client.fetch_paged(
                    "issues?state=open&labels=ai-observation", per_page=per_page, page=page
                )
                if not page_obs:
                    break
                for iss in page_obs:
                    if iss.get("pull_request"):
                        continue
                    iss_num = iss.get("number")
                    if not is_valid_positive_int(iss_num):
                        continue
                    iss_title_raw = iss.get("title") or ""
                    if iss_title_raw.strip().startswith("[AI Dashboard]"):
                        continue
                    open_observations_count += 1

                if len(page_obs) < per_page:
                    break
                if page == max_pages:
                    obs_truncated = True
        except Exception as e:
            obs_error = sanitize_diagnostic(str(e))
            if completeness == COMPLETENESS_COMPLETE:
                completeness = f"INCOMPLETE: Failed to fetch observations: {obs_error}"

        if obs_truncated:
            is_truncated = True

        # 4. Fetch open task issues via bounded query excluding observations before pagination
        open_issues: List[Dict[str, Any]] = []
        issues_truncated = False
        task_query = f"repo:{self.repo} is:issue is:open -label:ai-observation"
        queries.append(f"search/issues?q={task_query}")
        try:
            for page in range(1, max_pages + 1):
                search_res = self.client.search_issues_paged(
                    task_query, per_page=per_page, page=page, sort="created", order="desc"
                )
                items = search_res.get("items", []) if isinstance(search_res, dict) else []
                total_count = search_res.get("total_count", 0) if isinstance(search_res, dict) else 0

                # Propagate search timeout / partial match flag (GitHub incomplete_results)
                if bool(search_res.get("incomplete_results")):
                    issues_truncated = True
                    is_truncated = True
                    if completeness == COMPLETENESS_COMPLETE:
                        completeness = "INCOMPLETE: Search query returned incomplete results (incomplete_results=true)"

                for iss in items:
                    if iss.get("pull_request"):
                        continue
                    iss_num = iss.get("number")
                    if not is_valid_positive_int(iss_num):
                        continue
                    iss_num = int(iss_num)
                    iss_title_raw = iss.get("title") or ""
                    if iss_title_raw.strip().startswith("[AI Dashboard]"):
                        continue

                    # Defense in depth: filter out issues carrying 'ai-observation'
                    has_obs_label = any(
                        (l.get("name") if isinstance(l, dict) else str(l)) == "ai-observation"
                        for l in iss.get("labels", [])
                    )
                    if has_obs_label:
                        continue

                    iss_title = sanitize_display_text(iss_title_raw, max_len=75)
                    labels = [
                        sanitize_display_text(l.get("name", ""), max_len=20)
                        for l in iss.get("labels", [])
                        if isinstance(l, dict)
                    ]

                    open_issues.append({
                        "number": iss_num,
                        "title": iss_title,
                        "labels": labels,
                    })

                if len(items) < per_page or len(open_issues) >= total_count:
                    break

                if page == max_pages:
                    issues_truncated = True
                    is_truncated = True
        except Exception as e:
            issues_error = sanitize_diagnostic(str(e))
            if completeness == COMPLETENESS_COMPLETE:
                completeness = f"INCOMPLETE: Failed to fetch open issues: {issues_error}"

        # 5. Fetch recent runs on default branch
        main_runs: List[Dict[str, Any]] = []
        queries.append("actions/runs?branch=main")
        try:
            raw_runs = self.client.fetch_paged("actions/runs?branch=main", per_page=5, page=1)
            if isinstance(raw_runs, dict):
                raw_runs = raw_runs.get("workflow_runs", [])
            for r in (raw_runs or [])[:5]:
                main_runs.append({
                    "id": r.get("id"),
                    "name": sanitize_display_text(r.get("name"), max_len=30),
                    "head_sha": r.get("head_sha", "unknown"),
                    "conclusion": r.get("conclusion") or r.get("status") or "unknown",
                    "created_at": r.get("created_at", ""),
                })
        except Exception as e:
            if completeness == COMPLETENESS_COMPLETE:
                completeness = f"INCOMPLETE: Failed fetching main runs: {sanitize_diagnostic(str(e))}"

        if is_truncated and completeness == COMPLETENESS_COMPLETE:
            completeness = "INCOMPLETE: Item inventory truncated at pagination limit"

        facts = RepositoryFacts(
            main_head_sha=main_sha,
            open_prs=open_prs,
            open_issues=open_issues,
            main_runs=main_runs,
            completeness=completeness,
            capture_time_utc=capture_time,
            queries_executed=queries,
            source_repo=self.repo,
            prs_error=prs_error,
            prs_truncated=prs_truncated,
            issues_error=issues_error,
            issues_truncated=issues_truncated,
            open_observations_count=None if obs_error else open_observations_count,
            observations_truncated=obs_truncated,
            observations_error=obs_error,
        )
        return facts, is_truncated

    def collect(self, max_pages: int = 3, per_page: int = 30, max_main_retries: int = 2) -> RepositoryFacts:
        """Collects repository facts with full coherent reacquisition if main advances."""
        for attempt in range(max_main_retries + 1):
            facts, _ = self._acquire_facts_once(max_pages=max_pages, per_page=per_page)

            # Recheck main ref
            try:
                recheck_ref = self.client.get_ref("heads/main")
                end_sha = recheck_ref.get("object", {}).get("sha", "unknown")
            except Exception as e:
                facts.completeness = f"INCOMPLETE: Failed rechecking main ref: {sanitize_diagnostic(str(e))}"
                return facts

            if end_sha == facts.main_head_sha:
                return facts

            # Main advanced during acquisition
            if attempt == max_main_retries:
                facts.completeness = f"INCOMPLETE: Main advanced during acquisition ({facts.main_head_sha} -> {end_sha})"
                facts.main_head_sha = end_sha
                return facts

        return facts


def normalize_curated_links(content: str, base_url: str, doc_dir: str = "docs") -> str:
    """
    Normalizes relative markdown links in curated content so they resolve to absolute
    GitHub URLs within the Issue context, while preserving absolute URLs, mailto, and anchors.
    """
    if not content or not base_url:
        return content

    def replace_link(match: re.Match) -> str:
        prefix = match.group(1) or ""
        text = match.group(2)
        target = match.group(3).strip()
        title_part = match.group(4) or ""

        clean_target = target.lstrip("<").rstrip(">")

        # Preserve absolute URLs, mailto, and in-page anchors
        if (
            re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*://", clean_target)
            or clean_target.startswith("#")
            or clean_target.startswith("mailto:")
        ):
            return match.group(0)

        path_part, sep, anchor = clean_target.partition("#")
        clean_path = path_part.strip()

        if not clean_path:
            return match.group(0)

        if clean_path.startswith("/"):
            normalized = clean_path.lstrip("/")
        else:
            combined = posixpath.join(doc_dir, clean_path)
            normalized = posixpath.normpath(combined)

        normalized = normalized.lstrip("./")
        while normalized.startswith("../"):
            normalized = normalized[3:]

        full_url = f"{base_url.rstrip('/')}/{normalized}"
        if sep and anchor:
            full_url = f"{full_url}#{anchor}"

        return f"{prefix}[{text}]({full_url}{title_part})"

    pattern = r"(!?)\[([^\]]+)\]\(([^)\s]+)(\s+\"[^\"]*\")?\)"
    return re.sub(pattern, replace_link, content)


def extract_curated_content(curated_text: str, base_url: Optional[str] = None) -> str:
    """
    Extracts curated work queue and governance sections from docs/AI-CURRENT-STATE.md.
    Detects each required section independently. For missing/unrecognized sections,
    emits explicit diagnostic warnings and an absolute source-document link,
    preserving recognized sections and never falling back to the full document.
    """
    doc_link = (
        f"[docs/AI-CURRENT-STATE.md]({base_url.rstrip('/')}/docs/AI-CURRENT-STATE.md)"
        if base_url
        else "`docs/AI-CURRENT-STATE.md`"
    )

    if not curated_text or not curated_text.strip():
        return (
            f"> **Warning**: Curated orientation content is empty; "
            f"view full repository orientation in {doc_link}."
        )

    extracted = []

    # 1. Extract Work Queue section (supporting both naming variants)
    queue_match = re.search(
        r"(## 2\.\s+Active Work[^\n]*[\s\S]*?)(?=\n---\s*\n\s*## 3\.|\n## 3\.|\Z)",
        curated_text,
    )
    if queue_match:
        extracted.append(queue_match.group(1).strip())
    else:
        extracted.append(
            f"> **Warning**: Active work governance section could not be extracted from curated orientation; "
            f"view full active work queue and governance rules in {doc_link}."
        )

    # 2. Extract Decisions section (delimit before Section 6 or subsequent numbered section)
    decisions_match = re.search(
        r"(## 5\.\s+[^\n]*?Decisions[^\n]*[\s\S]*?)(?=\n---\s*\n\s*## 6\.|\n## 6\.|\n## [0-9]+\.|\Z)",
        curated_text,
    )
    if decisions_match:
        extracted.append(decisions_match.group(1).strip())
    else:
        extracted.append(
            f"> **Warning**: Architectural and governance decisions section could not be extracted from curated orientation; "
            f"view decisions and policy status in {doc_link}."
        )

    res = "\n\n".join(extracted)

    if base_url:
        res = normalize_curated_links(res, base_url)

    return res


def render_dashboard(
    facts: RepositoryFacts,
    curated_text: str,
    repo: str = "Tiflit/DXVK-Companion",
    max_words: int = 1000,
) -> str:
    """Renders the markdown dashboard bounded strictly to max_words excluding URLs, using absolute GitHub links."""
    if not is_valid_repo_name(repo):
        raise SecurityValidationError(f"Invalid repository identity '{repo}'")

    if is_valid_sha(facts.main_head_sha):
        base_url = f"https://github.com/{repo}/blob/{facts.main_head_sha}"
        main_display = facts.main_head_sha
    else:
        base_url = f"https://github.com/{repo}"
        main_display = sanitize_display_text(facts.main_head_sha, max_len=15)

    issues_url = f"https://github.com/{repo}/issues"
    pulls_url = f"https://github.com/{repo}/pulls"

    tooling_rev = get_tooling_revision()
    status_badge = f"`{facts.completeness}`"
    capture_time = facts.capture_time_utc or datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
    header = (
        f"# AI Current State & Handoff Dashboard\n\n"
        f"{DASHBOARD_MARKER}\n\n"
        f"> **Automated Snapshot** | Generated: {capture_time} | Main: `{main_display}` | Status: {status_badge}\n"
        f"> Tooling: `{tooling_rev}` | Repo: `{repo}` | Queries: `{', '.join(facts.queries_executed)}`\n"
        f"> Note: Status reflects machine-acquired fact completeness; curated guidance is policy prose from docs/AI-CURRENT-STATE.md (not automatically validated).\n"
    )

    startup_route = (
        "\n## 3. Fresh-Session Startup Route\n\n"
        f"1. **Operating Rules**: Read [AGENTS.md]({base_url}/AGENTS.md) for role allocation and invariants.\n"
        f"2. **Live Dashboard**: Inspect this machine-owned issue for compact orientation. Underlying GitHub records are authoritative; static [docs/AI-CURRENT-STATE.md]({base_url}/docs/AI-CURRENT-STATE.md) provides stable governance rules, not live identities.\n"
        f"3. **Assigned Task Contract**: Read `gh issue view <number>` (authoritative scope & acceptance criteria).\n"
        f"4. **Latest Relevant Activity/Evidence**: Inspect latest session in `docs/ai-journal/` or task PR review packet.\n"
        f"5. **Selective History**: Historical pilot logs ([docs/AI-PILOT-LOG.md]({base_url}/docs/AI-PILOT-LOG.md)) are for selective reference only.\n"
    )

    def render_doc(limit_issues: Optional[int] = None, limit_prs: Optional[int] = None, link_curated_only: bool = False) -> str:
        sec1 = ["\n## 1. Live Repository Status\n"]
        sec1.append(f"- **Default Branch (`main`)**: `{facts.main_head_sha}`\n")

        obs_url = f"https://github.com/{repo}/issues?q=is%3Aissue+is%3Aopen+label%3Aai-observation"
        if facts.observations_error:
            sec1.append(f"- **Open Observations**: _Unavailable due to API error: {facts.observations_error}_\n")
        elif facts.observations_truncated:
            sec1.append(f"- **Open Observations**: [>={facts.open_observations_count} (incomplete at limit)]({obs_url})\n")
        elif facts.open_observations_count is not None:
            sec1.append(f"- **Open Observations**: [{facts.open_observations_count}]({obs_url})\n")
        else:
            sec1.append("- **Open Observations**: _Unavailable_\n")

        # PRs
        if facts.prs_error:
            sec1.append("### Open Pull Requests (_Unavailable_)\n")
            sec1.append(f"_Pull requests inventory unavailable due to API error: {facts.prs_error}_\n")
        elif facts.prs_truncated and not facts.open_prs:
            sec1.append("### Open Pull Requests (_Incomplete_)\n")
            sec1.append("_Pull requests inventory incomplete (truncated at pagination limit)._\n")
        elif facts.prs_truncated:
            sec1.append(f"### Open Pull Requests (>={len(facts.open_prs)} [incomplete at limit])\n")
        elif not facts.open_prs:
            sec1.append("### Open Pull Requests (0)\n")
            sec1.append("_None (all active PRs merged)._\n")
        else:
            sec1.append(f"### Open Pull Requests ({len(facts.open_prs)})\n")

        if not facts.prs_error and not (facts.prs_truncated and not facts.open_prs) and facts.open_prs:
            sec1.append("| PR | Title | Head SHA | Base SHA | CI Run | Attempt | Status | Build Provenance |\n")
            sec1.append("|---|---|---|---|---|---|---|---|\n")
            displayed_prs = facts.open_prs[:limit_prs] if limit_prs else facts.open_prs
            for pr in displayed_prs:
                h_short = pr['head_sha'][:7] if is_valid_sha(pr['head_sha']) else pr['head_sha']
                b_short = pr['base_sha'][:7] if is_valid_sha(pr['base_sha']) else pr['base_sha']
                run_cell = f"[{pr['ci_run_id']}]({pr['ci_url']})" if pr.get('ci_url') else pr['ci_run_id']
                prov = pr.get('build_provenance', 'unknown')
                sec1.append(
                    f"| **#{pr['number']}** | {pr['title']} | `{h_short}` | `{b_short}` | "
                    f"{run_cell} | {pr.get('ci_attempt', '-')} | {pr['ci_status']} | {prov} |\n"
                )
            if limit_prs and len(facts.open_prs) > limit_prs:
                omitted_prs = len(facts.open_prs) - limit_prs
                sec1.append(f"\n> ... [{omitted_prs} PRs omitted to respect snapshot word budget; view all via [{pulls_url}]({pulls_url})]\n")
            elif facts.prs_truncated:
                sec1.append(f"\n> _Note: Pull requests inventory truncated at pagination limit; view all via [{pulls_url}]({pulls_url})_\n")

        # Issues
        if facts.issues_error:
            sec1.append("\n### Open Task Issues (_Unavailable_)\n")
            sec1.append(f"_Task issues inventory unavailable due to API error: {facts.issues_error}_\n")
        elif facts.issues_truncated and not facts.open_issues:
            sec1.append("\n### Open Task Issues (_Incomplete_)\n")
            sec1.append("_Task issues inventory incomplete (truncated at pagination limit)._\n")
        elif facts.issues_truncated:
            sec1.append(f"\n### Open Task Issues (>={len(facts.open_issues)} [incomplete at limit])\n")
        elif not facts.open_issues:
            sec1.append("\n### Open Task Issues (0)\n")
            sec1.append("_No open task issues._\n")
        else:
            sec1.append(f"\n### Open Task Issues ({len(facts.open_issues)})\n")

        if not facts.issues_error and not (facts.issues_truncated and not facts.open_issues) and facts.open_issues:
            sec1.append("| Issue | Title | Labels |\n")
            sec1.append("|---|---|---|\n")
            displayed_issues = facts.open_issues[:limit_issues] if limit_issues else facts.open_issues
            for iss in displayed_issues:
                labels_str = ", ".join(iss['labels']) if iss['labels'] else "-"
                sec1.append(f"| **#{iss['number']}** | {iss['title']} | {labels_str} |\n")
            if limit_issues and len(facts.open_issues) > limit_issues:
                omitted_iss = len(facts.open_issues) - limit_issues
                sec1.append(f"\n> ... [{omitted_iss} issues omitted to respect snapshot word budget; view all via [{issues_url}]({issues_url})]\n")
            elif facts.issues_truncated:
                sec1.append(f"\n> _Note: Task issues inventory truncated at pagination limit; view all via [{issues_url}]({issues_url})_\n")

        # Curated
        if link_curated_only:
            sec2 = (
                "\n## 2. Curated Work Queue & Governance\n\n"
                f"> [Curated work queue and governance details omitted to respect snapshot word budget; view full orientation in docs/AI-CURRENT-STATE.md]({base_url}/docs/AI-CURRENT-STATE.md)\n"
            )
        else:
            sec2 = "\n## 2. Curated Work Queue & Governance\n\n" + extract_curated_content(curated_text, base_url=base_url) + "\n"

        return header + "".join(sec1) + sec2 + startup_route

    # Multi-stage strict budget enforcement
    doc = render_doc()
    if count_words_excluding_urls(doc) <= max_words:
        return doc

    # Stage 1: Truncate issues to top 5
    doc = render_doc(limit_issues=5)
    if count_words_excluding_urls(doc) <= max_words:
        return doc

    # Stage 2: Truncate PRs to top 5 as well
    doc = render_doc(limit_issues=5, limit_prs=5)
    if count_words_excluding_urls(doc) <= max_words:
        return doc

    # Stage 3: Replace curated details with absolute orientation link
    doc = render_doc(limit_issues=5, limit_prs=5, link_curated_only=True)
    if count_words_excluding_urls(doc) <= max_words:
        return doc

    # Stage 4: Strict hard word trim, preserving identity header intact
    words = doc.split()
    trimmed_words = words[:max_words - 20]
    hard_trimmed = header + "\n\n" + " ".join(trimmed_words) + f"\n\n> ... [Snapshot truncated to respect {max_words}-word budget; see [{base_url}/docs/AI-CURRENT-STATE.md]({base_url}/docs/AI-CURRENT-STATE.md)]"
    return hard_trimmed


class DashboardPublisher:
    """Manages idempotent publication to the dedicated machine-owned GitHub dashboard issue."""

    def __init__(self, client: Any, repo: str):
        self.client = client
        self.repo = repo

    def should_skip_event(self, event_data: Optional[Dict[str, Any]]) -> Tuple[bool, str]:
        """Detects if event was triggered by the dashboard issue itself or bot action (loop prevention)."""
        if not event_data or not isinstance(event_data, dict):
            return False, ""

        issue_info = event_data.get("issue") or {}
        title = (issue_info.get("title") or "").strip()
        body = issue_info.get("body") or ""
        sender = event_data.get("sender") or {}
        sender_login = sender.get("login") or ""

        # Skip if triggered by dashboard issue itself
        if title == DASHBOARD_TITLE or DASHBOARD_MARKER in body:
            return True, "Triggered by dashboard issue itself (loop prevention)."

        # Skip if sender is github-actions[bot] on issue edited
        if sender_login == "github-actions[bot]" and event_data.get("action") == "edited":
            return True, "Triggered by github-actions[bot] issue edit (loop prevention)."

        return False, ""

    def is_content_unchanged(self, existing_body: str, new_body: str) -> bool:
        """Compares bodies ignoring the generation timestamp line."""
        norm_existing = re.sub(r"Generated:\s*[^\|]+", "Generated: <TS>", existing_body or "").strip()
        norm_new = re.sub(r"Generated:\s*[^\|]+", "Generated: <TS>", new_body or "").strip()
        return norm_existing == norm_new

    def publish(
        self,
        new_body: str,
        preview: bool = False,
    ) -> Dict[str, Any]:
        """Publishes the rendered dashboard to the authenticated dedicated dashboard issue."""
        if preview:
            return {"status": "preview", "action": "none"}

        # Validate exactly one outgoing dashboard marker (Item 5)
        marker_count = new_body.count(DASHBOARD_MARKER)
        if marker_count != 1:
            raise SecurityValidationError(
                f"Outgoing snapshot must contain exactly one dashboard marker ({DASHBOARD_MARKER}); found {marker_count}."
            )

        # Validate repository name
        if not is_valid_repo_name(self.repo):
            raise SecurityValidationError(f"Invalid repository identity: '{self.repo}'")

        # Validate structured snapshot header before publishing
        header_match = re.search(
            r"> \*\*Automated Snapshot\*\* \| Generated: ([^\|]+) \| Main: `([^`]+)` \| Status: `([^`]+)`\n> Tooling: `([^`]+)` \| Repo: `([^`]+)`(?: \| Queries:[^\n]*)?",
            new_body,
        )
        if not header_match:
            raise SecurityValidationError("Rendered snapshot is missing required structured identity header.")

        snap_gen = header_match.group(1).strip()
        snap_main = header_match.group(2).strip()
        snap_status = header_match.group(3).strip()
        snap_tooling = header_match.group(4).strip()
        snap_repo = header_match.group(5).strip()

        if snap_repo != self.repo:
            raise SecurityValidationError(f"Snapshot repo '{snap_repo}' does not match publisher repo '{self.repo}'.")

        # Validate full 40-character main SHA (Item 3)
        if not is_valid_sha(snap_main):
            raise SecurityValidationError(f"Snapshot contains missing or non-40-hex main SHA: '{snap_main}'")

        # Validate full 40-character tooling SHA (Item 3)
        tooling_match = re.search(r"@([0-9a-fA-F]{40})$", snap_tooling)
        if not tooling_match:
            raise SecurityValidationError(f"Snapshot contains missing or non-40-hex tooling SHA: '{snap_tooling}'")

        # Recheck live main immediately before writing: must match full 40-char SHA (Item 3)
        live_ref = self.client.get_ref("heads/main")
        live_main_sha = live_ref.get("object", {}).get("sha", "")
        if not is_valid_sha(live_main_sha):
            raise SecurityValidationError(f"Live main ref is invalid or missing SHA: '{live_main_sha}'")

        if live_main_sha != snap_main:
            raise SecurityValidationError(
                f"Main branch moved ({snap_main} -> {live_main_sha}) "
                "between snapshot generation and publication; refusing stale publication."
            )

        # Check current tooling HEAD: must match full 40-char SHA (Item 3)
        current_tooling = get_tooling_revision()
        if current_tooling != snap_tooling:
            raise SecurityValidationError(
                f"Tooling changed ({snap_tooling} -> {current_tooling}) before publication; refusing stale publication."
            )

        # Search for existing dedicated dashboard issue
        matched = self.client.search_issues(DASHBOARD_MARKER)

        # Check for closed dashboard issue (Item 4)
        closed_matched = [m for m in matched if m.get("state") == "closed"]
        if closed_matched:
            raise SecurityValidationError(
                f"Authenticated dashboard issue #{closed_matched[0]['number']} is closed. "
                "Manual administrative action required to reopen or recreate."
            )

        open_matched = [m for m in matched if m.get("state") == "open"]

        if len(open_matched) > 1:
            issue_nums = [f"#{m.get('number')}" for m in open_matched]
            raise SecurityValidationError(
                f"Multiple authenticated dashboard issues detected ({', '.join(issue_nums)}). "
                "Refusing ambiguous publication. Consolidate to a single dashboard issue."
            )

        if len(open_matched) == 1:
            target_issue = open_matched[0]
            issue_number = target_issue.get("number")
            existing_body = target_issue.get("body") or ""

            if self.is_content_unchanged(existing_body, new_body):
                return {
                    "status": "skipped",
                    "reason": "unchanged",
                    "issue_number": issue_number,
                }

            self.client.patch_issue(issue_number, new_body)
            return {
                "status": "updated",
                "action": "patched",
                "issue_number": issue_number,
            }

        # Create initial dedicated dashboard issue
        new_issue = self.client.create_issue(
            title=DASHBOARD_TITLE,
            body=new_body,
            labels=["ai-dashboard"],
        )
        return {
            "status": "created",
            "action": "created",
            "issue_number": new_issue.get("number"),
        }


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Automate compact GitHub handoff dashboard.")
    parser.add_argument("--repo", default=os.environ.get("GH_REPO") or os.environ.get("GITHUB_REPOSITORY"), help="GitHub repository (owner/repo)")
    parser.add_argument("--token", default=os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN"), help="GitHub authorization token")
    parser.add_argument("--preview", action="store_true", help="Print dashboard preview without publishing")
    parser.add_argument("--dry-run", action="store_true", help="Alias for --preview")
    parser.add_argument("--publish", action="store_true", help="Publish snapshot to GitHub dashboard issue")
    parser.add_argument("--publish-file", help="Publish content from pre-rendered file")
    parser.add_argument("--curated-file", default="docs/AI-CURRENT-STATE.md", help="Path to curated orientation markdown")
    parser.add_argument("--output-file", help="Write rendered markdown to file")
    parser.add_argument("--event-path", default=os.environ.get("GITHUB_EVENT_PATH"), help="Path to GITHUB_EVENT_PATH JSON")

    args = parser.parse_args(argv)

    if not args.repo:
        sys.stderr.write("Error: Repository not specified. Set --repo or GH_REPO/GITHUB_REPOSITORY.\n")
        return 1

    if not is_valid_repo_name(args.repo):
        sys.stderr.write(f"Error: Invalid repository format '{args.repo}'. Must be owner/repo.\n")
        return 1

    client = GitHubClient(token=args.token, repo=args.repo)
    publisher = DashboardPublisher(client=client, repo=args.repo)

    # Preview contract: any preview or dry-run argument combination causes zero writes
    is_preview = bool(args.preview or args.dry_run or not args.publish)

    # 1. Event-aware loop check
    if args.event_path and os.path.exists(args.event_path):
        try:
            with open(args.event_path, "r", encoding="utf-8") as f:
                event_data = json.load(f)
            skip, reason = publisher.should_skip_event(event_data)
            if skip:
                print(f"Skipping dashboard update: {reason}")
                return 0
        except Exception as e:
            sys.stderr.write(f"Warning: Failed reading event path: {e}\n")

    # If publishing pre-rendered file directly (Job 2 in workflow)
    if args.publish_file:
        if not os.path.exists(args.publish_file):
            sys.stderr.write(f"Error: Publish file '{args.publish_file}' does not exist.\n")
            return 1
        with open(args.publish_file, "r", encoding="utf-8") as f:
            rendered_content = f.read()

        if is_preview:
            # Preview mode: print content, perform zero writes
            print(f"[PREVIEW] Dashboard snapshot content (zero writes):\n{rendered_content}")
            return 0

        try:
            res = publisher.publish(rendered_content, preview=False)
            print(f"Dashboard publication result: {res}")
            return 0
        except SecurityValidationError as e:
            sys.stderr.write(f"Security/Validation error during publication: {e}\n")
            return 2
        except Exception as e:
            sys.stderr.write(f"Publication failed: {e}\n")
            return 1

    # 2. Collect facts
    collector = GitHubFactsCollector(client=client, repo=args.repo)
    facts = collector.collect()

    # 3. Read curated content
    curated_text = ""
    if args.curated_file and os.path.exists(args.curated_file):
        try:
            with open(args.curated_file, "r", encoding="utf-8") as f:
                curated_text = f.read()
        except Exception as e:
            sys.stderr.write(f"Warning: Failed reading curated file {args.curated_file}: {e}\n")

    # 4. Render markdown snapshot
    try:
        rendered = render_dashboard(facts, curated_text, repo=args.repo)
    except SecurityValidationError as e:
        sys.stderr.write(f"Security/Validation error during rendering: {e}\n")
        return 2

    # 5. Output handling
    if args.output_file:
        try:
            Path(args.output_file).parent.mkdir(parents=True, exist_ok=True)
            with open(args.output_file, "w", encoding="utf-8") as f:
                f.write(rendered)
            print(f"Wrote dashboard snapshot to {args.output_file}")
        except Exception as e:
            sys.stderr.write(f"Error writing to {args.output_file}: {e}\n")
            return 1

    if is_preview:
        print(rendered)
        return 0

    # 6. Publish to GitHub
    try:
        res = publisher.publish(rendered, preview=False)
        print(f"Dashboard publication result: {res}")
        return 0
    except SecurityValidationError as e:
        sys.stderr.write(f"Security/Validation error during publication: {e}\n")
        return 2
    except Exception as e:
        sys.stderr.write(f"Publication failed: {e}\n")
        return 1


if __name__ == "__main__":
    sys.exit(main())
