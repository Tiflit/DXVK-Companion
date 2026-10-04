"""Generates a compact, evidence-bound, read-only handoff snapshot for an AI development task."""

from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

try:
    from .parse_contract import ContractParseError, extract_primary_issue
    from .parse_trx import (
        SecurityValidationError,
        TrxTotals,
        parse_trx_content,
        parse_trx_file,
        safe_extract_json_from_zip,
        safe_extract_zip,
    )
    from .update_dashboard import (
        GitHubApiError,
        GitHubClient,
        count_words_excluding_urls,
        is_valid_positive_int,
        is_valid_repo_name,
        is_valid_sha,
        sanitize_diagnostic,
        sanitize_display_text,
    )
except ImportError:
    from parse_contract import ContractParseError, extract_primary_issue
    from parse_trx import (
        SecurityValidationError,
        TrxTotals,
        parse_trx_content,
        parse_trx_file,
        safe_extract_json_from_zip,
        safe_extract_zip,
    )
    from update_dashboard import (
        GitHubApiError,
        GitHubClient,
        count_words_excluding_urls,
        is_valid_positive_int,
        is_valid_repo_name,
        is_valid_sha,
        sanitize_diagnostic,
        sanitize_display_text,
    )


MAX_HANDOFF_WORDS = 300


class HandoffGitHubClient(GitHubClient):
    """HTTP client for GitHub API with PR/Issue helpers and safe artifact download."""

    def get_pr(self, pr_number: int) -> Dict[str, Any]:
        return self._request("GET", f"repos/{self.repo}/pulls/{pr_number}")

    def get_issue(self, issue_number: int) -> Dict[str, Any]:
        return self._request("GET", f"repos/{self.repo}/issues/{issue_number}")

    def download_bytes(self, url: str, max_bytes: int = 50 * 1024 * 1024, timeout: int = 30) -> bytes:
        import urllib.parse
        import urllib.request
        init_parsed = urllib.parse.urlparse(url)
        headers = {
            "User-Agent": "DXVK-Companion-AI-Workflow",
        }
        if self.token and init_parsed.netloc.endswith("github.com"):
            headers["Authorization"] = f"Bearer {self.token}"

        class SafeRedirectHandler(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, hdrs, newurl):
                new_parsed = urllib.parse.urlparse(newurl)
                if new_parsed.netloc != init_parsed.netloc:
                    clean_headers = {k: v for k, v in req.headers.items() if k.lower() != "authorization"}
                    clean_headers["User-Agent"] = "DXVK-Companion-AI-Workflow"
                    return urllib.request.Request(newurl, headers=clean_headers)
                return super().redirect_request(req, fp, code, msg, hdrs, newurl)

        opener = urllib.request.build_opener(SafeRedirectHandler)
        req = urllib.request.Request(url, headers=headers)

        with opener.open(req, timeout=timeout) as resp:
            chunks: List[bytes] = []
            downloaded = 0
            while True:
                chunk = resp.read(65536)
                if not chunk:
                    break
                downloaded += len(chunk)
                if downloaded > max_bytes:
                    raise SecurityValidationError(f"Download exceeded maximum size ({max_bytes} bytes).")
                chunks.append(chunk)
            return b"".join(chunks)


GitHubClient = HandoffGitHubClient


@dataclass
class LocalWorkspaceIdentity:
    """Separately labeled local workspace identity from git CLI."""
    branch: str = "unknown"
    head_sha: str = "unknown"
    status_summary: str = "clean"
    is_git: bool = False


@dataclass
class HandoffSnapshot:
    """Immutable data structure representing the collected handoff facts."""
    capture_time_utc: str
    repo: str
    pr_number: int
    pr_title: str
    pr_state: str
    pr_merged: bool
    live_pr_head_sha: str
    live_pr_base_sha: str
    live_default_branch_ref: str
    live_default_branch_sha: str
    base_has_moved: bool
    primary_issue_number: Optional[int] = None
    primary_issue_title: str = ""
    primary_issue_state: str = ""
    decision_required: Optional[str] = None
    decision_status: Optional[str] = None
    decision_approval_source: Optional[str] = None
    local_workspace: Optional[LocalWorkspaceIdentity] = None
    ci_run_id: Optional[str] = None
    ci_run_name: Optional[str] = None
    ci_run_attempt: Optional[str] = None
    ci_run_conclusion: Optional[str] = None
    ci_head_sha: Optional[str] = None
    tested_checkout_sha: Optional[str] = None
    tested_checkout_ref: Optional[str] = None
    trx_totals: Optional[TrxTotals] = None
    preserved_reviews_count: int = 0
    preserved_review_ids: List[str] = field(default_factory=list)
    next_owner: str = "ChatGPT"
    next_action: str = "Independent verification"
    uncertainties: List[str] = field(default_factory=list)


def inspect_local_workspace(cwd: Optional[Path] = None) -> LocalWorkspaceIdentity:
    """Inspects local git workspace identity without altering state."""
    try:
        work_dir = str(cwd) if cwd else os.getcwd()
        head_proc = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=work_dir,
            capture_output=True,
            text=True,
            timeout=5,
        )
        if head_proc.returncode != 0:
            return LocalWorkspaceIdentity(is_git=False)

        head_sha = head_proc.stdout.strip()
        if not is_valid_sha(head_sha):
            return LocalWorkspaceIdentity(is_git=False)

        branch_proc = subprocess.run(
            ["git", "rev-parse", "--abbrev-ref", "HEAD"],
            cwd=work_dir,
            capture_output=True,
            text=True,
            timeout=5,
        )
        branch = branch_proc.stdout.strip() if branch_proc.returncode == 0 else "unknown"

        status_proc = subprocess.run(
            ["git", "status", "--porcelain"],
            cwd=work_dir,
            capture_output=True,
            text=True,
            timeout=5,
        )
        status_summary = "dirty" if status_proc.stdout.strip() else "clean"

        return LocalWorkspaceIdentity(
            branch=branch,
            head_sha=head_sha,
            status_summary=status_summary,
            is_git=True,
        )
    except Exception:
        return LocalWorkspaceIdentity(is_git=False)


def extract_decision_block(issue_body: str) -> Tuple[Optional[str], Optional[str], Optional[str]]:
    """
    Extracts Decision Governance Block from an Issue description.
    Returns: (decision_required, status, approval_source)
    """
    if not issue_body:
        return None, None, None

    req_match = re.search(r"\*\*Decision required\*\*:\s*([^\r\n]+)", issue_body, re.IGNORECASE)
    status_match = re.search(r"\*\*Status\*\*:\s*([^\r\n]+)", issue_body, re.IGNORECASE)
    source_match = re.search(r"\*\*Source of explicit human approval\*\*:\s*([^\r\n]+)", issue_body, re.IGNORECASE)

    decision_req = req_match.group(1).strip() if req_match else None
    status = status_match.group(1).strip() if status_match else None
    source = source_match.group(1).strip() if source_match else None

    return decision_req, status, source


def mask_code_spans(text: str) -> str:
    """Masks inline code spans and fenced code blocks with whitespace of equal length."""
    def mask_match(m):
        return " " * len(m.group(0))

    masked = re.sub(r"```[\s\S]*?```", mask_match, text)
    masked = re.sub(r"`[^`\r\n]*`", mask_match, masked)
    return masked


def extract_review_record_ids(pr_body: str) -> List[str]:
    """Extracts all protected review record IDs from a PR description outside code blocks."""
    if not pr_body:
        return []
    masked = mask_code_spans(pr_body)
    pattern = r"<!--\s*AI-REVIEW-RECORD:\s*([A-Za-z0-9_.:-]+)\s*-->"
    return re.findall(pattern, masked)


def collect_handoff_snapshot(
    client: GitHubClient,
    repo: str,
    pr_number: int,
    issue_number: Optional[int] = None,
    worktree_path: Optional[Path] = None,
) -> HandoffSnapshot:
    """
    Collects live GitHub PR, base branch, CI run, and issue metadata.
    Validates all SHAs and ensures acquired facts are distinct from model conclusions.
    """
    capture_time = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")

    # 1. Fetch live PR details
    pr_data = client.get_pr(pr_number)
    pr_title = pr_data.get("title", "")
    pr_state = pr_data.get("state", "open")
    pr_merged = pr_data.get("merged", False)
    pr_body = pr_data.get("body", "") or ""

    head_data = pr_data.get("head", {})
    base_data = pr_data.get("base", {})

    live_pr_head_sha = head_data.get("sha", "")
    if not is_valid_sha(live_pr_head_sha):
        raise ValueError(f"GitHub returned invalid PR head SHA: '{live_pr_head_sha}'")

    live_pr_base_sha = base_data.get("sha", "")
    if not is_valid_sha(live_pr_base_sha):
        raise ValueError(f"GitHub returned invalid PR base SHA: '{live_pr_base_sha}'")

    default_branch_ref = base_data.get("ref", "main")

    # 2. Fetch live default branch commit
    try:
        ref_data = client.get_ref(f"heads/{default_branch_ref}")
        live_default_sha = ref_data.get("object", {}).get("sha", "")
    except Exception:
        live_default_sha = "unknown"

    if live_default_sha != "unknown" and not is_valid_sha(live_default_sha):
        raise ValueError(f"GitHub returned invalid default branch SHA: '{live_default_sha}'")

    base_has_moved = (live_default_sha != "unknown" and live_default_sha != live_pr_base_sha)

    # 3. Resolve primary task issue
    resolved_issue_number = issue_number
    if resolved_issue_number is None:
        try:
            resolved_issue_number = extract_primary_issue(pr_body)
        except ContractParseError:
            resolved_issue_number = None

    primary_issue_title = ""
    primary_issue_state = ""
    decision_required = None
    decision_status = None
    decision_approval_source = None

    if resolved_issue_number:
        try:
            issue_data = client.get_issue(resolved_issue_number)
            primary_issue_title = issue_data.get("title", "")
            primary_issue_state = issue_data.get("state", "")
            issue_body = issue_data.get("body", "") or ""
            decision_required, decision_status, decision_approval_source = extract_decision_block(issue_body)
        except Exception:
            primary_issue_title = "unavailable"

    # 4. Review preservation check in PR body
    preserved_ids = extract_review_record_ids(pr_body)

    # 5. Local workspace identity
    local_ws = inspect_local_workspace(worktree_path)

    # 6. CI run evidence and verification
    ci_run_id = None
    ci_run_name = None
    ci_run_attempt = None
    ci_run_conclusion = None
    ci_head_sha = None
    tested_checkout_sha = None
    tested_checkout_ref = None
    trx_totals: Optional[TrxTotals] = None
    uncertainties: List[str] = []

    try:
        runs = client.get_runs_for_commit(live_pr_head_sha)
        build_run = None
        for run in runs:
            if run.get("name") == "Build and Test":
                build_run = run
                break
        if not build_run and runs:
            build_run = runs[0]

        if build_run:
            ci_run_id = str(build_run.get("id"))
            ci_run_name = build_run.get("name")
            ci_run_attempt = str(build_run.get("run_attempt", 1))
            ci_run_conclusion = build_run.get("conclusion") or build_run.get("status", "unknown")
            ci_head_sha = build_run.get("head_sha")

            # Check artifacts
            artifacts = client.get_run_artifacts(int(ci_run_id)) if ci_run_id.isdigit() else []
            prov_art = next((a for a in artifacts if a.get("name") == "build-provenance"), None)
            if prov_art and hasattr(client, "download_bytes"):
                try:
                    zip_bytes = client.download_bytes(prov_art["archive_download_url"])
                    prov_json = safe_extract_json_from_zip(zip_bytes, "build-provenance.json")
                    tested_checkout_sha = prov_json.get("head_sha")
                    tested_checkout_ref = prov_json.get("ref")
                except Exception as e:
                    uncertainties.append(f"Provenance artifact parsing failed: {sanitize_diagnostic(str(e))}")

            trx_art = next((a for a in artifacts if a.get("name") == "trx-test-reports"), None)
            if trx_art and hasattr(client, "download_bytes"):
                try:
                    trx_zip = client.download_bytes(trx_art["archive_download_url"])
                    # safe_extract_zip to temp dir and parse
                    import tempfile
                    with tempfile.TemporaryDirectory() as tmp_dir:
                        safe_extract_zip(trx_zip, Path(tmp_dir))
                        for f in Path(tmp_dir).rglob("*.trx"):
                            trx_totals = parse_trx_file(f)
                            break
                except Exception as e:
                    uncertainties.append(f"TRX artifact parsing failed: {sanitize_diagnostic(str(e))}")
        else:
            uncertainties.append(f"No CI workflow run found matching head SHA {live_pr_head_sha[:7]}")
    except Exception as e:
        uncertainties.append(f"CI query failed: {sanitize_diagnostic(str(e))}")

    # Determine next owner & action
    if decision_status and "pending" in decision_status.lower() and decision_approval_source in (None, "", "None", "None (approval pending)"):
        next_owner = "Human"
        next_action = f"Explicit decision required on Issue #{resolved_issue_number}"
    elif ci_run_conclusion in ("failure", "timed_out", "cancelled"):
        next_owner = "Gemini"
        next_action = "Bounded repair for failing CI check"
    elif pr_state == "open" and not preserved_ids:
        next_owner = "ChatGPT"
        next_action = "Independent review and verification"
    elif pr_state == "open" and preserved_ids:
        next_owner = "Human"
        next_action = "Final review and merge decision"
    else:
        next_owner = "Human"
        next_action = "Lifecycle closeout"

    return HandoffSnapshot(
        capture_time_utc=capture_time,
        repo=repo,
        pr_number=pr_number,
        pr_title=pr_title,
        pr_state=pr_state,
        pr_merged=pr_merged,
        live_pr_head_sha=live_pr_head_sha,
        live_pr_base_sha=live_pr_base_sha,
        live_default_branch_ref=default_branch_ref,
        live_default_branch_sha=live_default_sha,
        base_has_moved=base_has_moved,
        primary_issue_number=resolved_issue_number,
        primary_issue_title=primary_issue_title,
        primary_issue_state=primary_issue_state,
        decision_required=decision_required,
        decision_status=decision_status,
        decision_approval_source=decision_approval_source,
        local_workspace=local_ws,
        ci_run_id=ci_run_id,
        ci_run_name=ci_run_name,
        ci_run_attempt=ci_run_attempt,
        ci_run_conclusion=ci_run_conclusion,
        ci_head_sha=ci_head_sha,
        tested_checkout_sha=tested_checkout_sha,
        tested_checkout_ref=tested_checkout_ref,
        trx_totals=trx_totals,
        preserved_reviews_count=len(preserved_ids),
        preserved_review_ids=preserved_ids,
        next_owner=next_owner,
        next_action=next_action,
        uncertainties=uncertainties,
    )


def format_handoff_markdown(snap: HandoffSnapshot, max_words: int = MAX_HANDOFF_WORDS) -> str:
    """Formats the handoff snapshot into a compact, evidence-bound markdown summary."""
    lines: List[str] = []

    lines.append(f"# Task Handoff Snapshot: PR #{snap.pr_number}")
    lines.append("")
    lines.append(f"> **Captured**: {snap.capture_time_utc} | **Repo**: `{snap.repo}`")
    lines.append("")

    # 1. Acquired Facts: Task & Live Revisions
    lines.append("## 1. Verified GitHub Revisions & Task State")
    if snap.primary_issue_number:
        clean_issue_title = sanitize_display_text(snap.primary_issue_title, max_len=60)
        lines.append(f"- **Task Contract**: Issue #{snap.primary_issue_number} (`{clean_issue_title}`) [{snap.primary_issue_state.upper()}]")
    else:
        lines.append("- **Task Contract**: UNRESOLVED (No primary issue link found)")

    lines.append(f"- **Pull Request**: PR #{snap.pr_number} (`{sanitize_display_text(snap.pr_title, max_len=60)}`) [{snap.pr_state.upper()}]")
    lines.append(f"- **Live PR Head SHA**: `{snap.live_pr_head_sha}`")
    lines.append(f"- **Live PR Base SHA**: `{snap.live_pr_base_sha}`")
    if snap.base_has_moved:
        lines.append(f"- **Live Default Branch (`{snap.live_default_branch_ref}`)**: `{snap.live_default_branch_sha}` (BASE MOVED)")
    else:
        lines.append(f"- **Live Default Branch (`{snap.live_default_branch_ref}`)**: `{snap.live_default_branch_sha}` (synced)")

    if snap.local_workspace and snap.local_workspace.is_git:
        lines.append(f"- **Local Workspace (Separate Identity)**: branch `{snap.local_workspace.branch}` @ `{snap.local_workspace.head_sha}` ({snap.local_workspace.status_summary})")

    lines.append("")

    # 2. Acquired Facts: CI Evidence & Tests
    lines.append("## 2. CI Verification & Evidence Provenance")
    if snap.ci_run_id:
        name_str = f" ({snap.ci_run_name})" if snap.ci_run_name else ""
        lines.append(f"- **Triggering Run**: ID `{snap.ci_run_id}` (attempt {snap.ci_run_attempt}){name_str} -> **{snap.ci_run_conclusion.upper() if snap.ci_run_conclusion else 'UNKNOWN'}**")
    else:
        lines.append("- **Triggering Run**: UNAVAILABLE (No CI run found for PR head)")

    if snap.tested_checkout_sha:
        rel = f" ({snap.tested_checkout_ref})" if snap.tested_checkout_ref else ""
        lines.append(f"- **Tested Checkout SHA**: `{snap.tested_checkout_sha}`{rel}")
    else:
        lines.append("- **Tested Checkout SHA**: UNAVAILABLE / UNPROVEN")

    if snap.trx_totals:
        lines.append(f"- **TRX Test Totals**: {snap.trx_totals.passed} passed, {snap.trx_totals.failed} failed, {snap.trx_totals.skipped} skipped (total {snap.trx_totals.total})")
    else:
        lines.append("- **TRX Test Totals**: UNAVAILABLE")

    if snap.preserved_reviews_count > 0:
        ids_str = ", ".join(f"`{rid}`" for rid in snap.preserved_review_ids)
        lines.append(f"- **Preserved Review Records**: {snap.preserved_reviews_count} record(s) ({ids_str})")
    else:
        lines.append("- **Preserved Review Records**: 0 recorded")

    lines.append("")

    # 3. Decision Prerequisites & Governance
    lines.append("## 3. Decision Prerequisites & Governance")
    if snap.decision_required:
        lines.append(f"- **Decision Required**: {sanitize_display_text(snap.decision_required, max_len=80)}")
        lines.append(f"- **Decision Status**: `{snap.decision_status or 'PENDING'}`")
        lines.append(f"- **Human Approval Source**: {sanitize_display_text(snap.decision_approval_source or 'None', max_len=80)}")
    else:
        lines.append("- **Decision Prerequisites**: None pending (standard workflow)")

    lines.append("")

    # 4. Next Role & Concrete Action
    lines.append("## 4. Next Ownership & Action")
    lines.append(f"- **Next Owner**: **{snap.next_owner}**")
    lines.append(f"- **Exact Action**: {snap.next_action}")
    if snap.uncertainties:
        lines.append(f"- **Remaining Uncertainties**: {'; '.join(snap.uncertainties)}")

    full_text = "\n".join(lines)

    # Word budget check
    words = count_words_excluding_urls(full_text)
    if words > max_words:
        # Enforce budget truncation cleanly
        text_lines = full_text.splitlines()
        truncated: List[str] = []
        cur_words = 0
        for l in text_lines:
            line_w = count_words_excluding_urls(l)
            if cur_words + line_w > (max_words - 15):
                truncated.append("")
                truncated.append(f"> ... [Handoff truncated to meet {max_words}-word budget; total was {words} words]")
                break
            truncated.append(l)
            cur_words += line_w
        return "\n".join(truncated)

    return full_text


def format_handoff_json(snap: HandoffSnapshot) -> str:
    """Formats the handoff snapshot as structured JSON."""
    data = {
        "capture_time_utc": snap.capture_time_utc,
        "repo": snap.repo,
        "pr_number": snap.pr_number,
        "pr_title": snap.pr_title,
        "pr_state": snap.pr_state,
        "pr_merged": snap.pr_merged,
        "live_pr_head_sha": snap.live_pr_head_sha,
        "live_pr_base_sha": snap.live_pr_base_sha,
        "live_default_branch_ref": snap.live_default_branch_ref,
        "live_default_branch_sha": snap.live_default_branch_sha,
        "base_has_moved": snap.base_has_moved,
        "primary_issue_number": snap.primary_issue_number,
        "primary_issue_title": snap.primary_issue_title,
        "primary_issue_state": snap.primary_issue_state,
        "decision_governance": {
            "required": snap.decision_required,
            "status": snap.decision_status,
            "approval_source": snap.decision_approval_source,
        },
        "local_workspace": {
            "branch": snap.local_workspace.branch if snap.local_workspace else None,
            "head_sha": snap.local_workspace.head_sha if snap.local_workspace else None,
            "status": snap.local_workspace.status_summary if snap.local_workspace else None,
            "is_git": snap.local_workspace.is_git if snap.local_workspace else False,
        },
        "ci_evidence": {
            "run_id": snap.ci_run_id,
            "run_attempt": snap.ci_run_attempt,
            "conclusion": snap.ci_run_conclusion,
            "head_sha": snap.ci_head_sha,
            "tested_checkout_sha": snap.tested_checkout_sha,
            "tested_checkout_ref": snap.tested_checkout_ref,
            "trx_totals": {
                "passed": snap.trx_totals.passed,
                "failed": snap.trx_totals.failed,
                "skipped": snap.trx_totals.skipped,
                "total": snap.trx_totals.total,
            } if snap.trx_totals else None,
        },
        "review_records": {
            "count": snap.preserved_reviews_count,
            "ids": snap.preserved_review_ids,
        },
        "next_ownership": {
            "owner": snap.next_owner,
            "action": snap.next_action,
        },
        "uncertainties": snap.uncertainties,
    }
    return json.dumps(data, indent=2)


def get_default_github_token() -> Optional[str]:
    """Retrieves GitHub token from environment variables or local gh CLI auth."""
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    if token:
        return token
    try:
        res = subprocess.run(["gh", "auth", "token"], capture_output=True, text=True, timeout=3)
        if res.returncode == 0 and res.stdout.strip():
            return res.stdout.strip()
    except Exception:
        pass
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate compact, evidence-bound handoff snapshot.")
    parser.add_argument("--pr", type=int, required=True, help="Target Pull Request number.")
    parser.add_argument("--issue", type=int, default=None, help="Optional primary task issue number.")
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "Tiflit/DXVK-Companion"), help="Repository owner/repo.")
    parser.add_argument("--token", default=get_default_github_token(), help="GitHub API token.")
    parser.add_argument("--worktree", type=Path, default=None, help="Optional path to local workspace worktree.")
    parser.add_argument("--output", type=Path, default=None, help="Output file path (defaults to stdout).")
    parser.add_argument("--json", action="store_true", help="Output JSON snapshot instead of Markdown.")
    parser.add_argument("--max-words", type=int, default=MAX_HANDOFF_WORDS, help=f"Word limit (default {MAX_HANDOFF_WORDS}).")
    args = parser.parse_args()

    if not is_valid_repo_name(args.repo):
        print(f"ERROR: Invalid repository name: '{args.repo}'", file=sys.stderr)
        return 1

    client = GitHubClient(token=args.token, repo=args.repo)

    try:
        snap = collect_handoff_snapshot(
            client=client,
            repo=args.repo,
            pr_number=args.pr,
            issue_number=args.issue,
            worktree_path=args.worktree,
        )
    except Exception as e:
        print(f"ERROR collecting handoff snapshot: {e}", file=sys.stderr)
        return 1

    if args.json:
        result = format_handoff_json(snap)
    else:
        result = format_handoff_markdown(snap, max_words=args.max_words)

    if args.output:
        try:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(result, encoding="utf-8")
            print(f"Handoff snapshot written to: {args.output}")
        except Exception as e:
            print(f"ERROR writing to output file: {e}", file=sys.stderr)
            return 1
    else:
        print(result)

    return 0


if __name__ == "__main__":
    sys.exit(main())
