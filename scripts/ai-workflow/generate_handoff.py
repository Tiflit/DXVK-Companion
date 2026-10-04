"""Generates a compact, evidence-bound, read-only handoff snapshot for an AI development task."""

from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

try:
    from .generate_review_packet import (
        parse_iso8601_utc,
        resolve_trx_artifact_for_attempt,
    )
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
    from generate_review_packet import (
        parse_iso8601_utc,
        resolve_trx_artifact_for_attempt,
    )
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
    """HTTP client for GitHub API with PR/Issue/Jobs helpers and safe artifact download."""

    def get_pr(self, pr_number: int) -> Dict[str, Any]:
        return self._request("GET", f"repos/{self.repo}/pulls/{pr_number}")

    def get_issue(self, issue_number: int) -> Dict[str, Any]:
        return self._request("GET", f"repos/{self.repo}/issues/{issue_number}")

    def get_repo(self) -> Dict[str, Any]:
        return self._request("GET", f"repos/{self.repo}")

    def get_workflow_run_jobs(self, run_id: int, attempt: Optional[str] = None) -> List[Dict[str, Any]]:
        """Fetches jobs for the workflow run, preferring attempt-specific API when attempt is provided."""
        if attempt:
            try:
                data = self._request("GET", f"repos/{self.repo}/actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100")
                if isinstance(data, dict):
                    return data.get("jobs", [])
            except Exception:
                pass
        data = self._request("GET", f"repos/{self.repo}/actions/runs/{run_id}/jobs?per_page=100")
        jobs = data.get("jobs", []) if isinstance(data, dict) else []
        if attempt:
            return [j for j in jobs if str(j.get("run_attempt", "")) == str(attempt)]
        return jobs

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
class AttributedReview:
    """Attributed review record extracted from a PR description."""
    record_id: str
    reviewer: str
    result: str  # PASS, CHANGES REQUIRED, INCOMPLETE, UNKNOWN
    reviewed_head_sha: Optional[str] = None
    reviewed_base_sha: Optional[str] = None
    raw_content: str = ""


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
    pr_base_ref: str
    repo_default_branch: str
    live_default_branch_sha: str
    base_sync_status: str
    base_has_moved: bool
    primary_issue_number: Optional[int] = None
    primary_issue_title: str = ""
    primary_issue_state: str = ""
    decision_required: Optional[str] = None
    decision_proposed_option: Optional[str] = None
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
    reviews: List[AttributedReview] = field(default_factory=list)
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


def extract_decision_block(issue_body: str) -> Tuple[Optional[str], Optional[str], Optional[str], Optional[str]]:
    """
    Extracts Decision Governance Block from an Issue description.
    Returns: (decision_required, proposed_option, status, approval_source)
    """
    if not issue_body:
        return None, None, None, None

    req_match = re.search(r"\*\*Decision required\*\*:\s*([^\r\n]+)", issue_body, re.IGNORECASE)
    opt_match = re.search(r"\*\*Proposed option\*\*:\s*([^\r\n]+)", issue_body, re.IGNORECASE)
    status_match = re.search(r"\*\*Status\*\*:\s*([^\r\n]+)", issue_body, re.IGNORECASE)
    source_match = re.search(r"\*\*Source of explicit human approval\*\*:\s*([^\r\n]+)", issue_body, re.IGNORECASE)

    decision_req = req_match.group(1).strip() if req_match else None
    proposed_opt = opt_match.group(1).strip() if opt_match else None
    status = status_match.group(1).strip() if status_match else None
    source = source_match.group(1).strip() if source_match else None

    if status:
        status = re.sub(r"[`*]", "", status).strip().upper()

    return decision_req, proposed_opt, status, source


def mask_code_spans(text: str) -> str:
    """Masks inline code spans and fenced code blocks with whitespace of equal length."""
    def mask_match(m):
        return " " * len(m.group(0))

    masked = re.sub(r"```[\s\S]*?```", mask_match, text)
    masked = re.sub(r"`[^`\r\n]*`", mask_match, masked)
    return masked


def parse_review_records(pr_body: str) -> List[AttributedReview]:
    """
    Extracts and parses all protected review records from a PR description outside code blocks.
    Identifies reviewer, outcome (PASS, CHANGES REQUIRED, etc.), and reviewed head SHA.
    """
    if not pr_body:
        return []

    masked = mask_code_spans(pr_body)
    pattern = r"<!--\s*AI-REVIEW-RECORD:\s*([A-Za-z0-9_.:-]+)\s*-->([\s\S]*?)<!--\s*AI-REVIEW-RECORD-END\s*-->"
    matches = list(re.finditer(pattern, masked))
    records: List[AttributedReview] = []

    for m in matches:
        record_id = m.group(1).strip()
        s_offset = m.start(2)
        e_offset = m.end(2)
        block_text = pr_body[s_offset:e_offset].strip()

        reviewer = "Unknown"
        rev_match = re.search(r"##\s+([A-Za-z0-9]+)\s+.*(?:verification|review|audit)", block_text, re.I)
        if rev_match:
            reviewer = rev_match.group(1).strip()
        elif "-" in record_id:
            reviewer = record_id.split("-")[0].capitalize()

        res_match = re.search(r"\*\*Result(?:\*\*)?:?\s*([^*.\r\n]+)", block_text, re.I)
        raw_res = res_match.group(1).strip() if res_match else ""
        if "CHANGES REQUIRED" in raw_res.upper() or "CHANGES_REQUIRED" in raw_res.upper():
            result = "CHANGES REQUIRED"
        elif "PASS" in raw_res.upper() or "APPROVE" in raw_res.upper() or "APPROVED" in raw_res.upper():
            result = "PASS"
        elif "INCOMPLETE" in raw_res.upper():
            result = "INCOMPLETE"
        elif re.search(r"\bCHANGES REQUIRED\b", block_text, re.I):
            result = "CHANGES REQUIRED"
        elif re.search(r"\bPASS\b", block_text):
            result = "PASS"
        else:
            result = "UNKNOWN"

        head_match = re.search(r"Reviewed head\s+[`]?([0-9a-fA-F]{7,40})[`]?", block_text, re.I)
        reviewed_head = head_match.group(1).lower() if head_match else None

        base_match = re.search(r"against base\s+[`]?([0-9a-fA-F]{7,40})[`]?", block_text, re.I)
        reviewed_base = base_match.group(1).lower() if base_match else None

        records.append(AttributedReview(
            record_id=record_id,
            reviewer=reviewer,
            result=result,
            reviewed_head_sha=reviewed_head,
            reviewed_base_sha=reviewed_base,
            raw_content=block_text,
        ))

    return records


def extract_review_record_ids(pr_body: str) -> List[str]:
    """Extracts all protected review record IDs from a PR description outside code blocks."""
    return [r.record_id for r in parse_review_records(pr_body)]


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

    pr_base_ref = base_data.get("ref", "main")

    # 2. Query repository default branch separately and fetch its head commit
    try:
        repo_data = client.get_repo()
        repo_default_branch = repo_data.get("default_branch", "main") if isinstance(repo_data, dict) else "main"
    except Exception:
        repo_default_branch = "unknown"

    if repo_default_branch != "unknown":
        try:
            ref_data = client.get_ref(f"heads/{repo_default_branch}")
            live_default_sha = ref_data.get("object", {}).get("sha", "unknown")
        except Exception:
            live_default_sha = "unknown"
    else:
        live_default_sha = "unknown"

    if live_default_sha != "unknown" and not is_valid_sha(live_default_sha):
        raise ValueError(f"GitHub returned invalid default branch SHA: '{live_default_sha}'")

    # Evaluate base movement and sync status conservatively
    if live_default_sha == "unknown":
        base_sync_status = "UNKNOWN (default branch ref unavailable)"
        base_has_moved = False
    elif pr_base_ref != repo_default_branch:
        try:
            target_ref_data = client.get_ref(f"heads/{pr_base_ref}")
            target_sha = target_ref_data.get("object", {}).get("sha", "")
            if target_sha and target_sha != live_pr_base_sha:
                base_has_moved = True
                base_sync_status = f"NON-DEFAULT PR TARGET ({pr_base_ref} != {repo_default_branch}; BASE MOVED: target is {target_sha[:7]})"
            else:
                base_has_moved = False
                base_sync_status = f"NON-DEFAULT PR TARGET ({pr_base_ref} != {repo_default_branch}; synced to {pr_base_ref})"
        except Exception:
            base_has_moved = False
            base_sync_status = f"NON-DEFAULT PR TARGET ({pr_base_ref} != {repo_default_branch}; target ref unavailable)"
    else:
        if live_default_sha == live_pr_base_sha:
            base_has_moved = False
            base_sync_status = "synced"
        else:
            base_has_moved = True
            base_sync_status = "BASE MOVED"

    # 3. Resolve primary task issue and Decision Governance Block
    resolved_issue_number = issue_number
    if resolved_issue_number is None:
        try:
            resolved_issue_number = extract_primary_issue(pr_body)
        except ContractParseError:
            resolved_issue_number = None

    primary_issue_title = ""
    primary_issue_state = ""
    decision_required = None
    decision_proposed_option = None
    decision_status = None
    decision_approval_source = None

    if resolved_issue_number:
        try:
            issue_data = client.get_issue(resolved_issue_number)
            primary_issue_title = issue_data.get("title", "")
            primary_issue_state = issue_data.get("state", "")
            issue_body = issue_data.get("body", "") or ""
            decision_required, decision_proposed_option, decision_status, decision_approval_source = extract_decision_block(issue_body)
            if not decision_status:
                has_decision_prose = bool(re.search(
                    r"(?:human\s+(?:design\s+)?decision\s+required\s+first|architectural\s+(?:choice|decision)\s+required|(?:explicit\s+)?human\s+approval\s+required\s+before\s+implementation|obtain\s+the\s+human'?s?\s+decision)",
                    issue_body,
                    re.IGNORECASE,
                ))
                if has_decision_prose:
                    decision_status = "UNMIGRATED_PROSE_DECISION"
                    decision_required = "Unmigrated decision requirement in issue prose; explicit human approval required first"
                    decision_approval_source = "None"
                else:
                    decision_status = "NO_DECISION_BLOCK"
                    decision_required = "None recorded in issue description (Decision Governance Block absent)"
                    decision_approval_source = "None"
        except Exception as e:
            primary_issue_title = "unavailable"
            primary_issue_state = "unknown"
            decision_status = "UNAVAILABLE"
            decision_required = f"Issue #{resolved_issue_number} query failed: {sanitize_diagnostic(str(e))}"
            decision_approval_source = "None"
    else:
        decision_status = "NO_ISSUE_LINK"
        decision_required = "No primary task issue linked in PR description"
        decision_approval_source = "None"

    # 4. Review preservation check & attributed review records in PR body
    reviews = parse_review_records(pr_body)

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
        build_run = next((r for r in runs if r.get("name") == "Build and Test"), None)

        if not build_run:
            uncertainties.append(f"No 'Build and Test' workflow run found matching head SHA {live_pr_head_sha[:7]}")
        else:
            ci_run_id = str(build_run.get("id"))
            ci_run_name = build_run.get("name")
            ci_run_attempt = str(build_run.get("run_attempt", 1))
            ci_run_conclusion = build_run.get("conclusion") or build_run.get("status", "unknown")
            ci_head_sha = build_run.get("head_sha")

            # Check artifacts and jobs for this attempt
            artifacts = client.get_run_artifacts(int(ci_run_id)) if ci_run_id.isdigit() else []
            ci_jobs = client.get_workflow_run_jobs(int(ci_run_id), attempt=ci_run_attempt) if ci_run_id.isdigit() else []

            # 1. Process build-provenance
            prov_art = next((a for a in artifacts if a.get("name") == "build-provenance"), None)
            if prov_art and hasattr(client, "download_bytes"):
                try:
                    zip_bytes = client.download_bytes(prov_art["archive_download_url"])
                    prov_json = safe_extract_json_from_zip(zip_bytes, "build-provenance.json")
                    if not prov_json:
                        uncertainties.append("build-provenance.json not found inside artifact zip")
                    else:
                        prov_run_id = str(prov_json.get("run_id", ""))
                        prov_attempt = str(prov_json.get("run_attempt", ""))
                        prov_head_sha = str(prov_json.get("head_sha", ""))
                        prov_base_sha = str(prov_json.get("base_sha", ""))
                        prov_ref = str(prov_json.get("ref", ""))

                        if not prov_run_id or not prov_attempt or not prov_head_sha:
                            uncertainties.append("Provenance incomplete: missing run_id, run_attempt, or head_sha")
                        elif prov_run_id != ci_run_id:
                            uncertainties.append(f"Provenance run ID mismatch: recorded {prov_run_id} vs triggering run {ci_run_id}")
                        elif prov_attempt != ci_run_attempt:
                            uncertainties.append(f"Provenance attempt mismatch: recorded attempt {prov_attempt} vs triggering attempt {ci_run_attempt}")
                        else:
                            if "merge" in prov_ref:
                                tested_checkout_sha = prov_head_sha
                                tested_checkout_ref = f"synthetic merge ref {prov_ref}"
                            elif prov_head_sha == live_pr_head_sha or live_pr_head_sha.startswith(prov_head_sha):
                                tested_checkout_sha = prov_head_sha
                                tested_checkout_ref = "direct head checkout"
                            else:
                                uncertainties.append(f"Provenance checkout SHA mismatch: recorded {prov_head_sha[:7]} vs PR head {live_pr_head_sha[:7]}")

                            if prov_base_sha and live_pr_base_sha and prov_base_sha != live_pr_base_sha:
                                uncertainties.append(f"Base SHA disagreement: provenance recorded {prov_base_sha[:7]} vs PR base {live_pr_base_sha[:7]}")
                except Exception as e:
                    uncertainties.append(f"Provenance artifact parsing failed: {sanitize_diagnostic(str(e))}")
            else:
                uncertainties.append("No 'build-provenance' artifact found for triggering run")

            # 2. Process TRX test results using attempt attribution helper
            selected_trx, trx_unavail_reason = resolve_trx_artifact_for_attempt(
                artifacts=artifacts,
                ci_jobs=ci_jobs,
                run_attempt=str(ci_run_attempt),
            )
            if selected_trx and hasattr(client, "download_bytes"):
                try:
                    trx_zip = client.download_bytes(selected_trx["archive_download_url"])
                    with tempfile.TemporaryDirectory() as tmp_dir:
                        extracted = safe_extract_zip(trx_zip, Path(tmp_dir), max_uncompressed_bytes=50 * 1024 * 1024)
                        for trx_p in extracted:
                            if str(trx_p).endswith(".trx"):
                                totals = parse_trx_file(trx_p)
                                if totals.status != "unavailable":
                                    trx_totals = totals
                                    break
                except Exception as e:
                    uncertainties.append(f"TRX artifact parsing failed: {sanitize_diagnostic(str(e))}")
            elif trx_unavail_reason:
                uncertainties.append(f"TRX results unavailable: {trx_unavail_reason}")
    except Exception as e:
        uncertainties.append(f"CI query failed: {sanitize_diagnostic(str(e))}")

    # Determine next owner & concrete action
    current_head_reviews = [
        r for r in reviews
        if r.reviewed_head_sha and (
            r.reviewed_head_sha == live_pr_head_sha or
            live_pr_head_sha.startswith(r.reviewed_head_sha) or
            r.reviewed_head_sha.startswith(live_pr_head_sha[:7])
        )
    ]

    if ci_run_conclusion in ("failure", "timed_out", "cancelled"):
        next_owner = "Gemini"
        next_action = "Bounded repair for failing CI check"
    elif ci_run_conclusion in ("in_progress", "queued", "waiting", "pending"):
        next_owner = "CI"
        next_action = "Await workflow completion"
    elif current_head_reviews and current_head_reviews[-1].result == "CHANGES REQUIRED":
        latest_review = current_head_reviews[-1]
        next_owner = "Gemini"
        next_action = f"Focused revision addressing reviewer findings ({latest_review.record_id})"
    elif decision_status in ("PENDING", "UNMIGRATED_PROSE_DECISION"):
        next_owner = "Human"
        next_action = f"Explicit decision required on Issue #{resolved_issue_number}"
    elif pr_state == "open":
        if not reviews:
            next_owner = "ChatGPT"
            next_action = "Independent review and verification"
        elif not current_head_reviews:
            old_ids = ", ".join(f"`{r.record_id}`" for r in reviews)
            next_owner = "ChatGPT"
            next_action = f"Independent review of current head `{live_pr_head_sha[:7]}` (prior reviews on older heads: {old_ids})"
        else:
            latest_review = current_head_reviews[-1]
            if latest_review.result == "PASS":
                if ci_run_conclusion == "success":
                    next_owner = "Human"
                    next_action = "Final review and merge decision"
                else:
                    next_owner = "Human"
                    next_action = "Merge decision pending CI verification"
            else:
                next_owner = "ChatGPT"
                next_action = f"Clarify review status on current head ({latest_review.record_id} reported {latest_review.result})"
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
        pr_base_ref=pr_base_ref,
        repo_default_branch=repo_default_branch,
        live_default_branch_sha=live_default_sha,
        base_sync_status=base_sync_status,
        base_has_moved=base_has_moved,
        primary_issue_number=resolved_issue_number,
        primary_issue_title=primary_issue_title,
        primary_issue_state=primary_issue_state,
        decision_required=decision_required,
        decision_proposed_option=decision_proposed_option,
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
        reviews=reviews,
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
    lines.append(f"- **Live PR Base Branch (`{snap.pr_base_ref}`)**: `{snap.live_pr_base_sha}`")
    lines.append(f"- **Live Default Branch (`{snap.repo_default_branch}`)**: `{snap.live_default_branch_sha}` ({snap.base_sync_status})")

    if snap.local_workspace and snap.local_workspace.is_git:
        lines.append(f"- **Local Workspace (Separate Identity)**: branch `{snap.local_workspace.branch}` @ `{snap.local_workspace.head_sha}` ({snap.local_workspace.status_summary})")

    lines.append("")

    # 2. Acquired Facts: CI Evidence & Tests
    lines.append("## 2. CI Verification & Evidence Provenance")
    if snap.ci_run_id:
        name_str = f" ({snap.ci_run_name})" if snap.ci_run_name else ""
        lines.append(f"- **Triggering Run**: ID `{snap.ci_run_id}` (attempt {snap.ci_run_attempt}){name_str} -> **{snap.ci_run_conclusion.upper() if snap.ci_run_conclusion else 'UNKNOWN'}**")
    else:
        lines.append("- **Triggering Run**: UNAVAILABLE (No 'Build and Test' workflow run found for PR head)")

    if snap.tested_checkout_sha:
        rel = f" ({snap.tested_checkout_ref})" if snap.tested_checkout_ref else ""
        lines.append(f"- **Tested Checkout SHA**: `{snap.tested_checkout_sha}`{rel}")
    else:
        lines.append("- **Tested Checkout SHA**: UNAVAILABLE / UNPROVEN")

    if snap.trx_totals:
        lines.append(f"- **TRX Test Totals**: {snap.trx_totals.passed} passed, {snap.trx_totals.failed} failed, {snap.trx_totals.skipped} skipped (total {snap.trx_totals.total})")
    else:
        lines.append("- **TRX Test Totals**: UNAVAILABLE")

    if snap.reviews:
        review_summaries = []
        for r in snap.reviews:
            head_str = f" on head `{r.reviewed_head_sha[:7]}`" if r.reviewed_head_sha else ""
            review_summaries.append(f"`{r.record_id}`: **{r.result}**{head_str}")
        lines.append(f"- **Attributed Review Records**: {len(snap.reviews)} record(s) ({', '.join(review_summaries)})")
    else:
        lines.append("- **Attributed Review Records**: 0 recorded")

    lines.append("")

    # 3. Decision Prerequisites & Governance
    lines.append("## 3. Decision Prerequisites & Governance")
    if snap.decision_status == "PENDING":
        lines.append(f"- **Decision Required**: {sanitize_display_text(snap.decision_required, max_len=80)}")
        lines.append(f"- **Decision Status**: `PENDING` (BLOCKED: explicit human approval pending)")
        lines.append(f"- **Human Approval Source**: {sanitize_display_text(snap.decision_approval_source or 'None', max_len=80)}")
    elif snap.decision_status == "DECIDED":
        lines.append(f"- **Decision Required**: {sanitize_display_text(snap.decision_required, max_len=80)}")
        lines.append(f"- **Decision Status**: `DECIDED`")
        lines.append(f"- **Human Approval Source**: {sanitize_display_text(snap.decision_approval_source or 'None', max_len=80)}")
    elif snap.decision_status == "UNMIGRATED_PROSE_DECISION":
        lines.append(f"- **Decision Governance**: UNMIGRATED PROSE DECISION (Preflight verification required)")
        lines.append(f"- **Details**: {sanitize_display_text(snap.decision_required, max_len=80)}")
    elif snap.decision_status == "NO_DECISION_BLOCK":
        lines.append("- **Decision Governance**: UNRECORDED / NO DECISION BLOCK (Standard workflow if no architectural policy applies)")
    elif snap.decision_status == "UNAVAILABLE":
        lines.append(f"- **Decision Governance**: UNAVAILABLE ({sanitize_display_text(snap.decision_required, max_len=80)})")
    else:
        lines.append(f"- **Decision Governance**: `{snap.decision_status or 'UNSPECIFIED'}`")

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
        "pr_base_ref": snap.pr_base_ref,
        "repo_default_branch": snap.repo_default_branch,
        "live_default_branch_sha": snap.live_default_branch_sha,
        "base_sync_status": snap.base_sync_status,
        "base_has_moved": snap.base_has_moved,
        "primary_issue_number": snap.primary_issue_number,
        "primary_issue_title": snap.primary_issue_title,
        "primary_issue_state": snap.primary_issue_state,
        "decision_governance": {
            "required": snap.decision_required,
            "proposed_option": snap.decision_proposed_option,
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
        "review_records": [
            {
                "record_id": r.record_id,
                "reviewer": r.reviewer,
                "result": r.result,
                "reviewed_head_sha": r.reviewed_head_sha,
                "reviewed_base_sha": r.reviewed_base_sha,
            }
            for r in snap.reviews
        ],
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


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Generate compact, evidence-bound handoff snapshot.")
    parser.add_argument("--pr", type=int, required=True, help="Target Pull Request number.")
    parser.add_argument("--issue", type=int, default=None, help="Optional primary task issue number.")
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "Tiflit/DXVK-Companion"), help="Repository owner/repo.")
    parser.add_argument("--token", default=get_default_github_token(), help="GitHub API token.")
    parser.add_argument("--worktree", type=Path, default=None, help="Optional path to local workspace worktree.")
    parser.add_argument("--output", type=Path, default=None, help="Output file path (defaults to stdout).")
    parser.add_argument("--json", action="store_true", help="Output JSON snapshot instead of Markdown.")
    parser.add_argument("--max-words", type=int, default=MAX_HANDOFF_WORDS, help=f"Word limit (default {MAX_HANDOFF_WORDS}).")
    args = parser.parse_args(argv)

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
