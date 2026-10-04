"""Automates compact current-state facts and dashboard orientation for AI agent sessions."""

from __future__ import annotations

import argparse
import datetime
import json
import os
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
COMPLETENESS_COMPLETE = "COMPLETE"


class SecurityValidationError(Exception):
    """Raised when security boundaries, injection risks, or duplicate targets are detected."""
    pass


class GitHubApiError(Exception):
    """Raised when GitHub API requests fail."""
    pass


def get_tooling_revision() -> str:
    """Returns the tooling script path and commit SHA if available."""
    script_rel = "scripts/ai-workflow/update_dashboard.py"
    sha = os.environ.get("GITHUB_SHA")
    if not sha:
        try:
            res = subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True, timeout=5)
            if res.returncode == 0 and res.stdout.strip():
                sha = res.stdout.strip()
        except Exception:
            pass
    if sha and is_valid_sha(sha):
        return f"{script_rel}@{sha[:10]}"
    return script_rel


def is_valid_sha(sha: Optional[str]) -> bool:
    """Validates that a string is a 40-character hex commit SHA."""
    if not sha or not isinstance(sha, str):
        return False
    return bool(re.match(r"^[0-9a-fA-F]{40}$", sha.strip()))


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
    issues_error: Optional[str] = None


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
                content = resp.read().decode("utf-8")
                if content:
                    return json.loads(content)
                return {}
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
        return res.get("workflow_runs", [])

    def search_issues(self, query: str) -> List[Dict[str, Any]]:
        """
        Searches issues (including closed) for exact dedicated dashboard issue match.
        Filters out pull requests and non-matching titles/markers.
        """
        matched = []
        # Paginate across issues state=all
        for page in range(1, 4):
            try:
                issues = self._request("GET", f"repos/{self.repo}/issues?state=all&per_page=50&page={page}")
                if not issues:
                    break
                for iss in issues:
                    if iss.get("pull_request"):
                        continue
                    title = (iss.get("title") or "").strip()
                    body = iss.get("body") or ""
                    if title == DASHBOARD_TITLE and DASHBOARD_MARKER in body:
                        matched.append(iss)
            except Exception:
                break
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
            completeness = f"INCOMPLETE: Missing or invalid main branch SHA ('{main_sha}')"

        # 2. Fetch open pull requests with pagination
        open_prs: List[Dict[str, Any]] = []
        queries.append("pulls?state=open")
        try:
            for page in range(1, max_pages + 1):
                page_prs = self.client.fetch_paged("pulls?state=open", per_page=per_page, page=page)
                if not page_prs:
                    break
                for pr in page_prs:
                    pr_num = pr.get("number")
                    pr_title = sanitize_display_text(pr.get("title"), max_len=70)
                    head_info = pr.get("head") or {}
                    base_info = pr.get("base") or {}
                    head_sha = head_info.get("sha") if isinstance(head_info, dict) else "unknown"
                    base_sha = base_info.get("sha") if isinstance(base_info, dict) else "unknown"
                    if not is_valid_sha(head_sha):
                        head_sha = "unknown"
                    if not is_valid_sha(base_sha):
                        base_sha = "unknown"

                    # Look up specifically Build and Test CI runs for PR head (H4)
                    ci_status = "none"
                    ci_run_id = "-"
                    ci_attempt = "-"
                    ci_head_sha = "unknown"
                    tested_checkout = "unknown (no build provenance artifact)"
                    if is_valid_sha(head_sha):
                        try:
                            runs = self.client.get_runs_for_commit(head_sha)
                            target_run = None
                            for r in runs:
                                if r.get("name") == "Build and Test":
                                    target_run = r
                                    break
                            if not target_run and runs:
                                target_run = runs[0]

                            if target_run:
                                ci_run_id = str(target_run.get("id", "-"))
                                ci_attempt = str(target_run.get("run_attempt", "1"))
                                ci_head_sha = target_run.get("head_sha", "unknown")
                                conclusion = target_run.get("conclusion") or target_run.get("status") or "unknown"
                                if ci_head_sha != head_sha:
                                    ci_status = "historical/stale"
                                else:
                                    ci_status = conclusion
                        except Exception:
                            ci_status = "unavailable"

                    open_prs.append({
                        "number": pr_num,
                        "title": pr_title,
                        "head_sha": head_sha,
                        "base_sha": base_sha,
                        "ci_status": ci_status,
                        "ci_run_id": ci_run_id,
                        "ci_attempt": ci_attempt,
                        "ci_head_sha": ci_head_sha,
                        "tested_checkout_sha": tested_checkout,
                    })

                if page == max_pages and len(page_prs) == per_page:
                    is_truncated = True
        except Exception as e:
            prs_error = sanitize_diagnostic(str(e))
            if completeness == COMPLETENESS_COMPLETE:
                completeness = f"INCOMPLETE: Failed to fetch open pulls: {prs_error}"

        # 3. Fetch open issues with pagination
        open_issues: List[Dict[str, Any]] = []
        queries.append("issues?state=open")
        try:
            for page in range(1, max_pages + 1):
                page_issues = self.client.fetch_paged("issues?state=open", per_page=per_page, page=page)
                if not page_issues:
                    break
                for iss in page_issues:
                    if iss.get("pull_request"):
                        continue
                    iss_title_raw = iss.get("title") or ""
                    if iss_title_raw.strip().startswith("[AI Dashboard]"):
                        continue
                    
                    iss_num = iss.get("number")
                    iss_title = sanitize_display_text(iss_title_raw, max_len=75)
                    labels = [sanitize_display_text(l.get("name", ""), max_len=20) for l in iss.get("labels", []) if isinstance(l, dict)]
                    
                    open_issues.append({
                        "number": iss_num,
                        "title": iss_title,
                        "labels": labels,
                    })

                if page == max_pages and len(page_issues) == per_page:
                    is_truncated = True
        except Exception as e:
            issues_error = sanitize_diagnostic(str(e))
            if completeness == COMPLETENESS_COMPLETE:
                completeness = f"INCOMPLETE: Failed to fetch open issues: {issues_error}"

        # 4. Fetch recent runs on default branch
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
            # If main runs fail, record diagnostic
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
            issues_error=issues_error,
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


def extract_curated_content(curated_text: str) -> str:
    """Extracts curated work queue and governance sections from docs/AI-CURRENT-STATE.md."""
    if not curated_text:
        return ""
    
    extracted = []
    
    # Extract Work Queue section
    queue_match = re.search(r"(## 2\. Active Work Queue & Ownership[\s\S]*?)(?=\n## 3\.|\Z)", curated_text)
    if queue_match:
        extracted.append(queue_match.group(1).strip())
    
    # Extract Unresolved Decisions section
    decisions_match = re.search(r"(## 5\. Unresolved Architectural & Governance Decisions[\s\S]*?)(?=\Z)", curated_text)
    if decisions_match:
        extracted.append(decisions_match.group(1).strip())
    
    if not extracted:
        return curated_text.strip()
    
    return "\n\n".join(extracted)


def render_dashboard(
    facts: RepositoryFacts,
    curated_text: str,
    repo: str = "Tiflit/DXVK-Companion",
    max_words: int = 1000,
) -> str:
    """Renders the markdown dashboard bounded strictly to max_words excluding URLs, using absolute GitHub links."""
    base_url = f"https://github.com/{repo}/blob/{facts.main_head_sha}"
    issues_url = f"https://github.com/{repo}/issues"
    pulls_url = f"https://github.com/{repo}/pulls"
    
    tooling_rev = get_tooling_revision()
    status_badge = f"`{facts.completeness}`"
    header = (
        f"# AI Current State & Handoff Dashboard\n\n"
        f"{DASHBOARD_MARKER}\n\n"
        f"> **Automated Snapshot** | Generated: {facts.capture_time_utc} | Main: `{facts.main_head_sha[:10] if is_valid_sha(facts.main_head_sha) else facts.main_head_sha}` | Status: {status_badge}\n"
        f"> Tooling: `{tooling_rev}` | Repo: `{repo}` | Queries: `{', '.join(facts.queries_executed)}`\n"
    )

    startup_route = (
        "\n## 3. Fresh-Session Startup Route\n\n"
        f"1. **Operating Rules**: Read [AGENTS.md]({base_url}/AGENTS.md) for role allocation and invariants.\n"
        f"2. **Live Dashboard**: Read this issue (or [docs/AI-CURRENT-STATE.md]({base_url}/docs/AI-CURRENT-STATE.md) if offline/stale).\n"
        f"3. **Assigned Task Contract**: Read `gh issue view <number>` (authoritative scope & acceptance criteria).\n"
        f"4. **Latest Relevant Activity/Evidence**: Inspect latest session in `docs/ai-journal/` or task PR review packet.\n"
        f"5. **Selective History**: Historical pilot logs ([docs/AI-PILOT-LOG.md]({base_url}/docs/AI-PILOT-LOG.md)) are for selective reference only.\n"
    )

    def render_doc(limit_issues: Optional[int] = None, limit_prs: Optional[int] = None, link_curated_only: bool = False) -> str:
        sec1 = ["\n## 1. Live Repository Status\n"]
        sec1.append(f"- **Default Branch (`main`)**: `{facts.main_head_sha}`\n")
        
        # PRs
        sec1.append(f"### Open Pull Requests ({len(facts.open_prs)})\n")
        if facts.prs_error:
            sec1.append(f"_Pull requests inventory unavailable due to API error: {facts.prs_error}_\n")
        elif not facts.open_prs:
            sec1.append("_None (all active PRs merged)._\n")
        else:
            sec1.append("| PR | Title | Head SHA | Base SHA | CI Run | Attempt | Status |\n")
            sec1.append("|---|---|---|---|---|---|---|\n")
            displayed_prs = facts.open_prs[:limit_prs] if limit_prs else facts.open_prs
            for pr in displayed_prs:
                h_short = pr['head_sha'][:7] if is_valid_sha(pr['head_sha']) else pr['head_sha']
                b_short = pr['base_sha'][:7] if is_valid_sha(pr['base_sha']) else pr['base_sha']
                sec1.append(f"| **#{pr['number']}** | {pr['title']} | `{h_short}` | `{b_short}` | {pr['ci_run_id']} | {pr.get('ci_attempt', '-')} | {pr['ci_status']} |\n")
            if limit_prs and len(facts.open_prs) > limit_prs:
                omitted_prs = len(facts.open_prs) - limit_prs
                sec1.append(f"\n> ... [{omitted_prs} PRs omitted to respect snapshot word budget; view all via [{pulls_url}]({pulls_url})]\n")

        # Issues
        sec1.append(f"\n### Open Task Issues ({len(facts.open_issues)})\n")
        if facts.issues_error:
            sec1.append(f"_Task issues inventory unavailable due to API error: {facts.issues_error}_\n")
        elif not facts.open_issues:
            sec1.append("_No open task issues._\n")
        else:
            sec1.append("| Issue | Title | Labels |\n")
            sec1.append("|---|---|---|\n")
            displayed_issues = facts.open_issues[:limit_issues] if limit_issues else facts.open_issues
            for iss in displayed_issues:
                labels_str = ", ".join(iss['labels']) if iss['labels'] else "-"
                sec1.append(f"| **#{iss['number']}** | {iss['title']} | {labels_str} |\n")
            if limit_issues and len(facts.open_issues) > limit_issues:
                omitted_iss = len(facts.open_issues) - limit_issues
                sec1.append(f"\n> ... [{omitted_iss} issues omitted to respect snapshot word budget; view all via [{issues_url}]({issues_url})]\n")

        # Curated
        if link_curated_only:
            sec2 = (
                "\n## 2. Curated Work Queue & Governance\n\n"
                f"> [Curated work queue and governance details omitted to respect snapshot word budget; view full orientation in docs/AI-CURRENT-STATE.md]({base_url}/docs/AI-CURRENT-STATE.md)\n"
            )
        else:
            sec2 = "\n## 2. Curated Work Queue & Governance\n\n" + extract_curated_content(curated_text) + "\n"

        return header + "".join(sec1) + sec2 + startup_route

    # Multi-stage strict budget enforcement (H5)
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

    # Stage 4: Strict hard word trim
    words = doc.split()
    trimmed_words = words[:max_words - 20]
    hard_trimmed = " ".join(trimmed_words) + f"\n\n> ... [Snapshot truncated to respect {max_words}-word budget; see [{base_url}/docs/AI-CURRENT-STATE.md]({base_url}/docs/AI-CURRENT-STATE.md)]"
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

    def publish(self, new_body: str, preview: bool = False) -> Dict[str, Any]:
        """Publishes the rendered dashboard to the authenticated dedicated dashboard issue."""
        if preview:
            return {"status": "preview", "action": "none"}

        # Search for existing dedicated dashboard issue
        matched = self.client.search_issues(DASHBOARD_MARKER)
        
        # Check for closed dashboard issue (H1)
        closed_matched = [m for m in matched if m.get("state") == "closed"]
        if closed_matched:
            raise SecurityValidationError(
                f"Dashboard issue #{closed_matched[0]['number']} is closed. "
                "Manual administrative action required to reopen or recreate."
            )

        open_matched = [m for m in matched if m.get("state") == "open"]

        if len(open_matched) > 1:
            issue_nums = [f"#{m.get('number')}" for m in open_matched]
            raise SecurityValidationError(
                f"Multiple dashboard issues detected ({', '.join(issue_nums)}). "
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

        # Create initial dashboard issue
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

    client = GitHubClient(token=args.token, repo=args.repo)
    publisher = DashboardPublisher(client=client, repo=args.repo)

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
    if args.publish_file and os.path.exists(args.publish_file):
        try:
            with open(args.publish_file, "r", encoding="utf-8") as f:
                rendered_content = f.read()
            res = publisher.publish(rendered_content)
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
    rendered = render_dashboard(facts, curated_text, repo=args.repo)

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

    if args.preview or args.dry_run or not args.publish:
        print(rendered)
        return 0

    # 6. Publish to GitHub
    try:
        res = publisher.publish(rendered)
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
