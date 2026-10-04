"""Automates compact current-state facts and dashboard orientation for AI agent sessions."""

from __future__ import annotations

import argparse
import datetime
import json
import os
import re
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
TOOLING_REVISION = "scripts/ai-workflow/update_dashboard.py (Issue #25)"


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


def sanitize_display_text(text: Optional[str], max_len: int = 100) -> str:
    """
    Sanitizes untrusted display text for safe inclusion in Markdown and tables.
    Redacts local paths, usernames, auth tokens, escapes table pipes, and bounds length.
    """
    if not text:
        return ""
    
    # Replace control characters and newlines with spaces
    sanitized = re.sub(r"[\r\n\t]+", " ", str(text))
    
    # Strip HTML tags
    sanitized = re.sub(r"<[^>]+>", "", sanitized)
    
    # Redact sensitive paths and local usernames (e.g. C:\Users\<user>\ or /home/<user>/)
    sanitized = re.sub(r"([A-Za-z]:\\[Uu]sers\\)[^\\]+", r"\1[REDACTED_USER]", sanitized)
    sanitized = re.sub(r"(/home/)[^/]+", r"\1[REDACTED_USER]", sanitized)
    
    # Redact tokens (ghp_, github_pat_, Bearer ...)
    sanitized = re.sub(r"(?:ghp_|github_pat_|bearer\s+)[A-Za-z0-9_]+", "[REDACTED_TOKEN]", sanitized, flags=re.I)
    
    # Escape Markdown table pipes to avoid breaking table layout
    sanitized = sanitized.replace("|", "/")
    
    # Collapse consecutive whitespace
    sanitized = re.sub(r"\s+", " ", sanitized).strip()
    
    if len(sanitized) > max_len:
        sanitized = sanitized[: max_len - 3].rstrip() + "..."
    
    return sanitized


def count_words_excluding_urls(text: str) -> int:
    """Counts whitespace-separated words excluding URLs and Markdown formatting characters."""
    # Strip URLs
    no_urls = re.sub(r"https?://\S+", "", text)
    # Strip Markdown symbols
    cleaned = re.sub(r"[#|\-*`_>~]", " ", no_urls)
    words = cleaned.split()
    return len(words)


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
            raise GitHubApiError(f"HTTP {e.code} on {method} {url}: {msg}") from e
        except urllib.error.URLError as e:
            raise GitHubApiError(f"Network error on {method} {url}: {e.reason}") from e

    def get_ref(self, ref: str) -> Dict[str, Any]:
        return self._request("GET", f"repos/{self.repo}/git/ref/{ref}")

    def fetch_paged(self, endpoint: str, per_page: int = 30, page: int = 1) -> List[Dict[str, Any]]:
        sep = "&" if "?" in endpoint else "?"
        url = f"repos/{self.repo}/{endpoint}{sep}per_page={per_page}&page={page}"
        return self._request("GET", url)

    def get_runs_for_commit(self, commit_sha: str) -> List[Dict[str, Any]]:
        url = f"repos/{self.repo}/actions/runs?head_sha={commit_sha}&per_page=5"
        res = self._request("GET", url)
        return res.get("workflow_runs", [])

    def search_issues(self, query: str) -> List[Dict[str, Any]]:
        # Fetch open issues
        issues = self._request("GET", f"repos/{self.repo}/issues?state=open&per_page=50")
        matched = []
        for iss in issues:
            body = iss.get("body") or ""
            title = iss.get("title") or ""
            if DASHBOARD_MARKER in body or DASHBOARD_TITLE in title:
                matched.append(iss)
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

    def collect(self, max_pages: int = 3, per_page: int = 30, max_main_retries: int = 2) -> RepositoryFacts:
        capture_time = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
        queries: List[str] = []
        completeness = COMPLETENESS_COMPLETE

        # 1. Fetch initial main ref
        queries.append("ref:heads/main")
        try:
            ref_data = self.client.get_ref("heads/main")
            start_sha = ref_data.get("object", {}).get("sha", "unknown")
        except Exception as e:
            start_sha = "unknown"
            completeness = f"INCOMPLETE: Failed to fetch main ref: {e}"

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

                    # Query CI runs for PR head
                    ci_status = "unknown"
                    ci_run_id = "unknown"
                    ci_head_sha = "unknown"
                    tested_checkout = "unknown"
                    if is_valid_sha(head_sha):
                        try:
                            runs = self.client.get_runs_for_commit(head_sha)
                            if runs:
                                latest_run = runs[0]
                                ci_run_id = str(latest_run.get("id", "unknown"))
                                ci_head_sha = latest_run.get("head_sha", "unknown")
                                conclusion = latest_run.get("conclusion") or latest_run.get("status") or "unknown"
                                
                                # Verify if run head matches current PR head
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
                        "ci_head_sha": ci_head_sha,
                        "tested_checkout_sha": tested_checkout,
                    })
        except Exception as e:
            completeness = f"INCOMPLETE: Failed to fetch open pulls: {e}"

        # 3. Fetch open issues with pagination
        open_issues: List[Dict[str, Any]] = []
        queries.append("issues?state=open")
        try:
            for page in range(1, max_pages + 1):
                page_issues = self.client.fetch_paged("issues?state=open", per_page=per_page, page=page)
                if not page_issues:
                    break
                for iss in page_issues:
                    # Filter out PRs and the AI Dashboard issue itself
                    if "pull_request" in iss:
                        continue
                    iss_title_raw = iss.get("title") or ""
                    if iss_title_raw.strip().startswith("[AI Dashboard]"):
                        continue
                    
                    iss_num = iss.get("number")
                    iss_title = sanitize_display_text(iss_title_raw, max_len=75)
                    labels = [sanitize_display_text(l.get("name", ""), max_len=20) for l in iss.get("labels", []) if isinstance(l, dict)]
                    assignees = [sanitize_display_text(a.get("login", ""), max_len=20) for a in iss.get("assignees", []) if isinstance(a, dict)]
                    
                    open_issues.append({
                        "number": iss_num,
                        "title": iss_title,
                        "labels": labels,
                        "assignees": assignees,
                    })
        except Exception as e:
            completeness = f"INCOMPLETE: Failed to fetch open issues: {e}"

        # 4. Fetch recent runs on default branch
        main_runs: List[Dict[str, Any]] = []
        queries.append("actions/runs?branch=main")
        try:
            raw_runs = self.client.fetch_paged("actions/runs?branch=main", per_page=5, page=1)
            # Some APIs return {"workflow_runs": [...]}, others list
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
            # Non-fatal if actions runs cannot be retrieved
            pass

        # 5. Check if main moved during acquisition
        queries.append("recheck:ref:heads/main")
        try:
            current_sha = start_sha
            for attempt in range(max_main_retries + 1):
                recheck_ref = self.client.get_ref("heads/main")
                end_sha = recheck_ref.get("object", {}).get("sha", "unknown")
                if end_sha == start_sha:
                    current_sha = end_sha
                    break
                if attempt == max_main_retries:
                    completeness = f"INCOMPLETE: Main advanced during acquisition ({start_sha} -> {end_sha})"
                    current_sha = end_sha
        except Exception as e:
            completeness = f"INCOMPLETE: Failed rechecking main ref: {e}"

        return RepositoryFacts(
            main_head_sha=current_sha,
            open_prs=open_prs,
            open_issues=open_issues,
            main_runs=main_runs,
            completeness=completeness,
            capture_time_utc=capture_time,
            queries_executed=queries,
            source_repo=self.repo,
        )


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
        # Fallback to full text if headings not matched
        return curated_text.strip()
    
    return "\n\n".join(extracted)


def render_dashboard(facts: RepositoryFacts, curated_text: str, max_words: int = 1000) -> str:
    """Renders the markdown dashboard bounded to max_words excluding URLs."""
    status_badge = f"`{facts.completeness}`"
    header = (
        f"# AI Current State & Handoff Dashboard\n\n"
        f"{DASHBOARD_MARKER}\n\n"
        f"> **Automated Snapshot** | Generated: {facts.capture_time_utc} | Main: `{facts.main_head_sha[:10] if is_valid_sha(facts.main_head_sha) else facts.main_head_sha}` | Status: {status_badge}\n"
        f"> Tooling: `{TOOLING_REVISION}` | Repo: `{facts.source_repo}` | Queries: `{', '.join(facts.queries_executed)}`\n"
    )

    # 1. Live Repository Status
    sec1 = ["\n## 1. Live Repository Status\n"]
    sec1.append(f"- **Default Branch (`main`)**: `{facts.main_head_sha}`\n")
    
    # Open PRs table
    sec1.append(f"### Open Pull Requests ({len(facts.open_prs)})\n")
    if not facts.open_prs:
        sec1.append("_None (all active PRs merged)._\n")
    else:
        sec1.append("| PR | Title | Head SHA | Base SHA | CI Run | Tested Checkout | Status |\n")
        sec1.append("|---|---|---|---|---|---|---|\n")
        for pr in facts.open_prs:
            h_short = pr['head_sha'][:7] if is_valid_sha(pr['head_sha']) else pr['head_sha']
            b_short = pr['base_sha'][:7] if is_valid_sha(pr['base_sha']) else pr['base_sha']
            c_short = pr['tested_checkout_sha'][:7] if is_valid_sha(pr['tested_checkout_sha']) else pr['tested_checkout_sha']
            sec1.append(f"| **#{pr['number']}** | {pr['title']} | `{h_short}` | `{b_short}` | {pr['ci_run_id']} | `{c_short}` | {pr['ci_status']} |\n")

    # Open Task Issues table
    sec1.append(f"\n### Open Task Issues ({len(facts.open_issues)})\n")
    if not facts.open_issues:
        sec1.append("_No open task issues._\n")
    else:
        sec1.append("| Issue | Title | Labels | Assignee |\n")
        sec1.append("|---|---|---|---|\n")
        for iss in facts.open_issues:
            labels_str = ", ".join(iss['labels']) if iss['labels'] else "-"
            assignee_str = ", ".join(iss['assignees']) if iss['assignees'] else "-"
            sec1.append(f"| **#{iss['number']}** | {iss['title']} | {labels_str} | {assignee_str} |\n")

    # Recent main CI runs
    if facts.main_runs:
        sec1.append("\n### Recent Default-Branch CI\n")
        sec1.append("| Run ID | Name | Commit | Conclusion |\n")
        sec1.append("|---|---|---|---|\n")
        for r in facts.main_runs:
            c_short = r['head_sha'][:7] if is_valid_sha(r['head_sha']) else r['head_sha']
            sec1.append(f"| {r['id']} | {r['name']} | `{c_short}` | {r['conclusion']} |\n")

    # 2. Curated Content
    sec2 = "\n## 2. Curated Work Queue & Governance\n\n" + extract_curated_content(curated_text) + "\n"

    # 3. Startup Route
    sec3 = (
        "\n## 3. Fresh-Session Startup Route\n\n"
        "1. **Operating Rules**: Read [AGENTS.md](../AGENTS.md) for role allocation and invariants.\n"
        "2. **Live Dashboard**: Read this issue/document for current SHAs, open PRs, and active ownership.\n"
        "3. **Assigned Task Contract**: Read `gh issue view <number>` (authoritative scope & acceptance criteria).\n"
        "4. **Latest Relevant Activity/Evidence**: Inspect latest task journal file in `docs/ai-journal/` or PR review packet.\n"
        "5. **Selective History**: Historical pilot logs (`docs/AI-PILOT-LOG.md`) are for selective deep reference only.\n"
    )

    full_doc = header + "".join(sec1) + sec2 + sec3

    # Check word budget
    word_count = count_words_excluding_urls(full_doc)
    if word_count > max_words:
        # Progressively truncate issues if oversized
        excess_issues = len(facts.open_issues)
        keep_issues = max(5, excess_issues // 2)
        omitted = excess_issues - keep_issues
        
        # Re-render with truncated issues list
        sec1_truncated = ["\n## 1. Live Repository Status\n"]
        sec1_truncated.append(f"- **Default Branch (`main`)**: `{facts.main_head_sha}`\n")
        sec1_truncated.append(f"### Open Pull Requests ({len(facts.open_prs)})\n")
        if not facts.open_prs:
            sec1_truncated.append("_None (all active PRs merged)._\n")
        else:
            sec1_truncated.append("| PR | Title | Head SHA | Base SHA | CI Run | Status |\n|---|---|---|---|---|---|\n")
            for pr in facts.open_prs:
                h_short = pr['head_sha'][:7] if is_valid_sha(pr['head_sha']) else pr['head_sha']
                sec1_truncated.append(f"| **#{pr['number']}** | {pr['title']} | `{h_short}` | `{pr['base_sha'][:7]}` | {pr['ci_run_id']} | {pr['ci_status']} |\n")
        
        sec1_truncated.append(f"\n### Open Task Issues ({len(facts.open_issues)})\n")
        sec1_truncated.append("| Issue | Title | Labels |\n|---|---|---|\n")
        for iss in facts.open_issues[:keep_issues]:
            labels_str = ", ".join(iss['labels']) if iss['labels'] else "-"
            sec1_truncated.append(f"| **#{iss['number']}** | {iss['title']} | {labels_str} |\n")
        sec1_truncated.append(f"\n> ... [{omitted} issues omitted to respect snapshot word budget; view all via `gh issue list`]\n")

        full_doc = header + "".join(sec1_truncated) + sec2 + sec3

    return full_doc


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
        title = issue_info.get("title") or ""
        body = issue_info.get("body") or ""
        sender = event_data.get("sender") or {}
        sender_login = sender.get("login") or ""

        # Skip if triggered by dashboard issue itself
        if DASHBOARD_TITLE in title or DASHBOARD_MARKER in body:
            return True, "Triggered by dashboard issue itself (loop prevention)."

        # Skip if sender is github-actions[bot] on issue edited
        if sender_login == "github-actions[bot]" and event_data.get("action") == "edited":
            return True, "Triggered by github-actions[bot] issue edit (loop prevention)."

        return False, ""

    def is_content_unchanged(self, existing_body: str, new_body: str) -> bool:
        """Compares bodies ignoring the generation timestamp line."""
        # Normalize bodies by removing generated timestamp line
        norm_existing = re.sub(r"Generated:\s*[^\|]+", "Generated: <TS>", existing_body or "").strip()
        norm_new = re.sub(r"Generated:\s*[^\|]+", "Generated: <TS>", new_body or "").strip()
        return norm_existing == norm_new

    def publish(self, new_body: str, preview: bool = False) -> Dict[str, Any]:
        """Publishes the rendered dashboard to the dedicated dashboard issue."""
        if preview:
            return {"status": "preview", "action": "none"}

        # Search for existing dashboard issue
        matched = self.client.search_issues(DASHBOARD_MARKER)
        
        if len(matched) > 1:
            issue_nums = [f"#{m.get('number')}" for m in matched]
            raise SecurityValidationError(
                f"Multiple dashboard issues detected ({', '.join(issue_nums)}). "
                f"Refusing ambiguous publication. Consolidate to a single dashboard issue."
            )

        if len(matched) == 1:
            target_issue = matched[0]
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

        # Create the initial dashboard issue
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
    rendered = render_dashboard(facts, curated_text)

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
