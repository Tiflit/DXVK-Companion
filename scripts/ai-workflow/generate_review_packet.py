"""Generates an immutable, revision-accurate AI Review Packet."""

from __future__ import annotations

import argparse
import datetime
import io
import json
import os
import re
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
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


def validate_identifier(val: str, name: str, pattern: str) -> None:
    """Validates that an identifier matches expected format to prevent injection."""
    if not re.match(pattern, str(val)):
        raise ValueError(f"Invalid {name} format: '{val}'. Expected pattern: {pattern}")


class GitHubClient:
    """HTTP client for GitHub REST API with host-aware authorization and security bounds."""

    def __init__(self, token: Optional[str] = None, repo: Optional[str] = None):
        self.token = token
        self.repo = repo

    def fetch_json(self, url: str, timeout: int = 30) -> Any:
        """Fetches and parses JSON from GitHub API."""
        headers = {
            "Accept": "application/vnd.github.v3+json",
            "User-Agent": "DXVK-Companion-AI-Workflow",
        }
        if self.token:
            headers["Authorization"] = f"Bearer {self.token}"
        req = urllib.request.Request(url, headers=headers)
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def download_bytes(self, url: str, max_bytes: int = 50 * 1024 * 1024, timeout: int = 30) -> bytes:
        """
        Safely downloads bytes with host-aware authorization handling (F6),
        explicit timeout, and bounded maximum download size.
        """
        init_parsed = urllib.parse.urlparse(url)
        headers = {
            "User-Agent": "DXVK-Companion-AI-Workflow",
        }
        if self.token and init_parsed.netloc.endswith("github.com"):
            headers["Authorization"] = f"Bearer {self.token}"

        class SafeRedirectHandler(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, hdrs, newurl):
                new_parsed = urllib.parse.urlparse(newurl)
                # When redirecting away from github.com (e.g. to Azure blob storage), strip Authorization header
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
                    raise SecurityValidationError(
                        f"Download exceeded maximum allowed size ({max_bytes} bytes)."
                    )
                chunks.append(chunk)
            return b"".join(chunks)

    def get_pr(self, pr_number: int) -> Dict[str, Any]:
        return self.fetch_json(f"https://api.github.com/repos/{self.repo}/pulls/{pr_number}")

    def get_issue(self, issue_number: int) -> Dict[str, Any]:
        return self.fetch_json(f"https://api.github.com/repos/{self.repo}/issues/{issue_number}")

    def get_workflow_run_jobs(self, run_id: str, attempt: Optional[str] = None) -> List[Dict[str, Any]]:
        """Fetches jobs for the workflow run, preferring attempt-specific API when attempt is provided (F5)."""
        if attempt:
            try:
                data = self.fetch_json(f"https://api.github.com/repos/{self.repo}/actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100")
                return data.get("jobs", [])
            except urllib.error.HTTPError as e:
                if e.code != 404:
                    raise
        # Fallback to run-level jobs
        data = self.fetch_json(f"https://api.github.com/repos/{self.repo}/actions/runs/{run_id}/jobs?per_page=100")
        jobs = data.get("jobs", [])
        if attempt:
            filtered = [j for j in jobs if str(j.get("run_attempt", "")) == str(attempt)]
            if filtered:
                return filtered
        return jobs

    def get_run_artifacts(self, run_id: str) -> List[Dict[str, Any]]:
        data = self.fetch_json(f"https://api.github.com/repos/{self.repo}/actions/runs/{run_id}/artifacts")
        return data.get("artifacts", [])

    def compare_commits(self, base_sha: str, head_sha: str) -> Dict[str, Any]:
        return self.fetch_json(f"https://api.github.com/repos/{self.repo}/compare/{base_sha}...{head_sha}")


def build_packet_content(
    repo: str,
    pr_number: int,
    pr_title: str,
    pr_url: str,
    reviewed_head_sha: str,
    base_sha: str,
    live_pr_head_sha: str,
    run_id: str,
    run_attempt: str,
    run_head_sha: str,
    build_provenance: Optional[Dict[str, Any]],
    task_contract: Optional[Dict[str, Any]],
    ci_jobs: List[Dict[str, Any]],
    trx_totals: Optional[Dict[str, Any]],
    manifest: List[Dict[str, Any]],
    capture_time: str,
    pr_verification: str = "",
    max_diff_lines_per_section: int = 500,
    live_base_sha: Optional[str] = None,
    provenance_diagnostic: str = "",
) -> str:
    """Builds the formatted markdown content of the AI Review Packet in strict section order."""
    lines: List[str] = []

    tested_base_sha = base_sha
    actual_live_base = live_base_sha if live_base_sha is not None else base_sha

    # Title & Introduction
    lines.append("# AI Review Packet")
    lines.append("")
    lines.append("Generated from GitHub metadata after the deterministic Build and Test workflow completed.")
    lines.append("")
    lines.append("> This packet is evidence for independent review. It does not determine whether the change is correct.")
    lines.append("")

    # Section 1: Revision and Evidence Identity
    lines.append("## 1. Revision and Evidence Identity")
    lines.append("")
    lines.append(f"- Repository: `{repo}`")
    lines.append(f"- PR: #{pr_number} — {pr_title} ({pr_url})")
    lines.append(f"- Triggering run ID: `{run_id}` (attempt {run_attempt})")
    lines.append(f"- Run head SHA: `{run_head_sha}`")
    lines.append(f"- Reviewed head SHA: `{reviewed_head_sha}`")

    # Tested checkout identity
    if build_provenance and build_provenance.get("head_sha"):
        tested_sha = build_provenance["head_sha"]
        tested_ref = build_provenance.get("ref", "unknown")
        lines.append(f"- Tested checkout SHA: `{tested_sha}`")
        if "merge" in tested_ref:
            lines.append(f"- Checkout relationship: `synthetic merge ref {tested_ref}`")
        elif tested_sha == run_head_sha:
            lines.append("- Checkout relationship: `direct head checkout`")
        else:
            lines.append(f"- Checkout relationship: `ref {tested_ref}`")
    else:
        diag_str = f" ({provenance_diagnostic})" if provenance_diagnostic else " (build-provenance.json was unavailable; checkout identity is not assumed)"
        lines.append(f"- Tested checkout SHA: `incomplete`{diag_str}")

    lines.append(f"- Tested Base SHA: `{tested_base_sha}`")
    if actual_live_base and actual_live_base != tested_base_sha:
        lines.append(f"- Live Base SHA: `{actual_live_base}`")
        lines.append("")
        lines.append("> [!WARNING]")
        lines.append("> BASE-BRANCH MOVEMENT: BASE MOVED")
        lines.append(f"> Triggering run was based on `{tested_base_sha}`, but live base branch is now `{actual_live_base}`.")
        lines.append("> Integration CI evidence reflects the tested base; re-run CI if updated base integration is required.")
    else:
        lines.append(f"- Live Base SHA: `{actual_live_base}` (matches tested base)")

    lines.append(f"- Metadata capture time: `{capture_time}`")

    # Check for current-head staleness
    if live_pr_head_sha and run_head_sha and live_pr_head_sha != run_head_sha:
        lines.append("")
        lines.append("> [!WARNING]")
        lines.append("> CURRENT-HEAD STALENESS: STALE")
        lines.append(f"> Live PR head is `{live_pr_head_sha}`, but this review packet represents triggering CI run for commit `{run_head_sha}`.")
        lines.append("> Re-run CI on the latest PR head to obtain updated evidence.")

    lines.append("")

    # Section 2: Task Contract
    lines.append("## 2. Task Contract")
    lines.append("")
    if task_contract:
        issue_num = task_contract.get("issue_number")
        issue_title = task_contract.get("title", "")
        issue_body = task_contract.get("body", "")
        lines.append(f"Tracked issue: #{issue_num} — {issue_title}")
        lines.append("")
        if len(issue_body) > 40000:
            lines.append(issue_body[:40000])
            lines.append("")
            lines.append("[Task contract truncated at 40,000 characters; see Issue directly.]")
        else:
            lines.append(issue_body)
    else:
        lines.append("No primary task Issue reference was detected or resolved.")

    lines.append("")
    lines.append("### PR Verification Statement")
    lines.append("")
    if pr_verification:
        lines.append(pr_verification)
    else:
        lines.append("No explicit verification statement extracted from PR description.")
    lines.append("")

    # Section 3: Deterministic CI and Test Totals
    lines.append("## 3. Deterministic CI and Test Totals")
    lines.append("")

    # Structured TRX Totals Breakdown
    lines.append("### Structured Test Totals (TRX)")
    lines.append("")
    lines.append("> Definitions: Total = tests discovered in TRX; Passed = passed tests; Failed = failed + error + timeout + aborted; Skipped = notExecuted + notRunnable + inconclusive.")
    lines.append("")
    if trx_totals and trx_totals.get("status") in ("passed", "failed"):
        lines.append(f"- Status: `{trx_totals.get('status').upper()}`")
        lines.append(f"- Total tests: {trx_totals.get('total')}")
        lines.append(f"- Passed: {trx_totals.get('passed')}")
        lines.append(f"- Failed: {trx_totals.get('failed')}")
        lines.append(f"- Skipped: {trx_totals.get('skipped')}")
        lines.append(f"- Provenance source: `{trx_totals.get('source', 'TRX artifact')}`")
    else:
        reason = trx_totals.get("reason", "No test artifact available") if trx_totals else "No test artifact"
        lines.append(f"- Status: `UNAVAILABLE` ({reason})")
    lines.append("")

    lines.append("### CI Job and Step Results")
    lines.append("")
    if ci_jobs:
        for job in ci_jobs:
            job_name = job.get("name", "Job")
            conclusion = job.get("conclusion") or job.get("status", "unknown")
            lines.append(f"- Job: {job_name} — {conclusion}")
            for step in job.get("steps", []):
                step_name = step.get("name", "")
                if re.search(r"Restore|Build|Test|Publish|Upload", step_name, re.IGNORECASE):
                    step_conc = step.get("conclusion") or step.get("status", "unknown")
                    lines.append(f"  - {step_name}: {step_conc}")
    else:
        lines.append("No CI job telemetry available.")
    lines.append("")

    # Section 4: Complete Changed Files Manifest
    total_manifest_files = len(manifest)
    files_with_patches = sum(1 for f in manifest if f.get("patch"))
    lines.append("## 4. Complete Changed Files Manifest")
    lines.append("")
    if manifest:
        if files_with_patches < total_manifest_files:
            lines.append(f"> Manifest reports {total_manifest_files} changed files; {files_with_patches} file patches available via API ({total_manifest_files - files_with_patches} omitted or binary).")
            lines.append("")
        for item in manifest:
            filename = item.get("filename", "")
            additions = item.get("additions", 0)
            deletions = item.get("deletions", 0)
            status = item.get("status", "modified")
            has_patch = bool(item.get("patch"))
            patch_note = "" if has_patch else " [patch omitted/binary]"
            lines.append(f"- `{filename}` (+{additions}/-{deletions}) — {status}{patch_note}")
    else:
        lines.append("No changed files in manifest.")
    lines.append("")

    # Split files into categories for budgeted diffs (F12)
    test_files = []
    prod_files = []
    workflow_doc_files = []

    for item in manifest:
        fn = item.get("filename", "")
        if fn.startswith("tests/") or fn.endswith("Tests.cs") or "/tests/" in fn:
            test_files.append(item)
        elif fn.startswith("src/"):
            prod_files.append(item)
        else:
            workflow_doc_files.append(item)

    def render_diff_section(files: List[Dict[str, Any]], title: str, limit: int) -> Tuple[List[str], bool]:
        sec_lines: List[str] = [f"## {title}", ""]
        current_lines = 0
        truncated = False

        if not files:
            sec_lines.append(f"No changes in this category.")
            sec_lines.append("")
            return sec_lines, False

        for f in files:
            fname = f.get("filename", "")
            patch = f.get("patch", "")
            additions = f.get("additions", 0)
            deletions = f.get("deletions", 0)

            sec_lines.append(f"### `{fname}` (+{additions}/-{deletions})")
            sec_lines.append("")
            if not patch:
                sec_lines.append("[Patch unavailable or binary; inspect diff artifact]")
                sec_lines.append("")
                continue

            patch_lines = patch.splitlines()
            remaining_budget = limit - current_lines
            if remaining_budget <= 0:
                truncated = True
                break

            if len(patch_lines) > remaining_budget:
                sec_lines.append("```diff")
                sec_lines.extend(patch_lines[:remaining_budget])
                sec_lines.append("```")
                sec_lines.append("")
                current_lines += remaining_budget
                truncated = True
                break
            else:
                sec_lines.append("```diff")
                sec_lines.extend(patch_lines)
                sec_lines.append("```")
                sec_lines.append("")
                current_lines += len(patch_lines)

        if truncated:
            sec_lines.append(f"[Section diff truncated at {limit} lines; inspect complete diff artifact]")
            sec_lines.append("")

        return sec_lines, truncated

    # Section 5: Changed Tests
    test_sec, _ = render_diff_section(test_files, "5. Changed Tests", max_diff_lines_per_section)
    lines.extend(test_sec)

    # Section 6: Production Changes
    prod_sec, _ = render_diff_section(prod_files, "6. Production Changes", max_diff_lines_per_section)
    lines.extend(prod_sec)

    # Section 7: Workflow, Documentation, and Review Focus
    work_sec, _ = render_diff_section(workflow_doc_files, "7. Workflow, Documentation, and Review Focus", max_diff_lines_per_section)
    lines.extend(work_sec)

    lines.append("---")
    lines.append("### Independent-Review Checklist")
    lines.append("")
    lines.append("1. Compare implementation against the task contract.")
    lines.append("2. Audit tests before trusting green CI.")
    lines.append("3. Ask whether the required invariant can still be bypassed through another entry point.")
    lines.append("4. Trace the real production orchestration, not only mocks.")
    lines.append("5. Check regressions and unrelated scope.")
    lines.append("")
    lines.append("The selected diff is intentionally compact. The full diff artifact preserves complete context.")
    lines.append("")

    return "\n".join(lines)


def generate_packet(
    event: Dict[str, Any],
    client: GitHubClient,
    output_dir: Path,
    override_pr_number: Optional[int] = None,
    step_summary_path: Optional[Path] = None,
    github_output_path: Optional[Path] = None,
) -> int:
    """Orchestrates end-to-end review packet acquisition and generation."""
    workflow_run = event.get("workflow_run", {})
    if not workflow_run:
        print("ERROR: Event does not contain workflow_run.", file=sys.stderr)
        return 1

    pull_requests = workflow_run.get("pull_requests", [])
    if not pull_requests:
        print("INFO: Completed workflow_run is not associated with any pull request; nothing to package.")
        return 0

    # Handle multiple PR associations explicitly (F3)
    target_pr = None
    if len(pull_requests) == 1:
        target_pr = pull_requests[0]
    else:
        pr_numbers = [p.get("number") for p in pull_requests]
        if override_pr_number is not None:
            matching = [p for p in pull_requests if p.get("number") == override_pr_number]
            if matching:
                target_pr = matching[0]
                print(f"INFO: Disambiguated multiple PR associations {pr_numbers} to PR #{override_pr_number} via explicit argument.")
            else:
                print(f"ERROR: Specified --pr-number {override_pr_number} is not in associated PRs: {pr_numbers}.", file=sys.stderr)
                return 1
        else:
            print(f"ERROR: workflow_run is associated with multiple pull requests: {pr_numbers}. Ambiguous association declined; specify --pr-number to disambiguate.", file=sys.stderr)
            return 1

    pr_number = target_pr.get("number")
    run_id = str(workflow_run.get("id"))
    run_attempt = str(workflow_run.get("run_attempt", "1"))
    run_head_sha = str(workflow_run.get("head_sha", ""))

    # Validate identifiers (F12)
    validate_identifier(client.repo, "repository", r"^[\w.-]+/[\w.-]+$")
    validate_identifier(pr_number, "pr_number", r"^\d+$")
    validate_identifier(run_id, "run_id", r"^\d+$")
    validate_identifier(run_attempt, "run_attempt", r"^\d+$")
    validate_identifier(run_head_sha, "run_head_sha", r"^[0-9a-fA-F]{40}$")

    # Fetch live PR metadata
    pr_data = client.get_pr(pr_number)
    live_pr_head_sha = pr_data.get("head", {}).get("sha", "")
    live_base_sha = pr_data.get("base", {}).get("sha", "")
    pr_title = pr_data.get("title", "")
    pr_url = pr_data.get("html_url", "")
    pr_body = pr_data.get("body", "")

    # Bind tested base SHA from triggering run event payload if available (F4)
    event_base_sha = target_pr.get("base", {}).get("sha", "")
    tested_base_sha = event_base_sha if event_base_sha else live_base_sha
    if tested_base_sha:
        validate_identifier(tested_base_sha, "tested_base_sha", r"^[0-9a-fA-F]{40}$")

    # Resolve task contract issue
    task_contract = None
    try:
        primary_issue_num = extract_primary_issue(pr_body)
        issue_data = client.get_issue(primary_issue_num)
        task_contract = {
            "issue_number": primary_issue_num,
            "title": issue_data.get("title", ""),
            "body": issue_data.get("body", ""),
        }
    except Exception as e:
        print(f"INFO: Could not resolve primary task contract: {e}")

    # Extract verification statement from PR body
    verification_match = re.search(
        r"^##\s+Verification\s*$(.*?)(?=^##\s+|\Z)",
        pr_body,
        re.MULTILINE | re.IGNORECASE | re.DOTALL,
    )
    pr_verification = verification_match.group(1).strip() if verification_match else ""

    # Fetch attempt-specific jobs (F5)
    ci_jobs = client.get_workflow_run_jobs(run_id, attempt=run_attempt)

    # Fetch changed files comparison between tested base and run_head_sha (F4)
    compare_data = client.compare_commits(tested_base_sha, run_head_sha)
    manifest = compare_data.get("files", [])

    # Process TRX results and provenance from run artifacts (F1, F5, F6)
    artifacts = client.get_run_artifacts(run_id)

    trx_totals = {"status": "unavailable", "reason": "No TRX artifact found"}
    build_provenance = None
    provenance_diagnostic = ""

    output_dir.mkdir(parents=True, exist_ok=True)

    for art in artifacts:
        art_name = art.get("name", "")
        art_download_url = art.get("archive_download_url", "")
        if not art_download_url:
            continue

        if art_name == "phase-a-test-results":
            try:
                zip_data = client.download_bytes(art_download_url, max_bytes=50 * 1024 * 1024, timeout=30)
                with tempfile.TemporaryDirectory() as tmp_dir:
                    extracted = safe_extract_zip(zip_data, Path(tmp_dir), max_uncompressed_bytes=50 * 1024 * 1024)
                    for trx_p in extracted:
                        totals = parse_trx_file(trx_p)
                        if totals.status != "unavailable":
                            trx_totals = {
                                "status": totals.status,
                                "total": totals.total,
                                "passed": totals.passed,
                                "failed": totals.failed,
                                "skipped": totals.skipped,
                                "source": totals.source_name,
                            }
                            break
            except Exception as e:
                trx_totals = {"status": "unavailable", "reason": f"Failed to download/parse TRX: {e}"}

        elif art_name == "build-provenance":
            try:
                zip_data = client.download_bytes(art_download_url, max_bytes=10 * 1024 * 1024, timeout=30)
                parsed_prov = safe_extract_json_from_zip(zip_data, "build-provenance.json", max_uncompressed_bytes=10 * 1024 * 1024)
                if parsed_prov:
                    prov_run_id = str(parsed_prov.get("run_id", ""))
                    prov_attempt = str(parsed_prov.get("run_attempt", ""))
                    # Verify attempt and run identity (F5)
                    if prov_run_id and prov_run_id != run_id:
                        provenance_diagnostic = f"Run ID mismatch: provenance recorded {prov_run_id} vs triggering run {run_id}"
                    elif prov_attempt and prov_attempt != run_attempt:
                        provenance_diagnostic = f"Attempt mismatch: provenance recorded attempt {prov_attempt} vs triggering attempt {run_attempt}"
                    else:
                        build_provenance = parsed_prov
                else:
                    provenance_diagnostic = "build-provenance.json not found inside artifact zip"
            except Exception as e:
                provenance_diagnostic = f"Failed to download/parse build-provenance: {e}"

    capture_time = datetime.datetime.now(datetime.timezone.utc).isoformat()

    packet_md = build_packet_content(
        repo=client.repo,
        pr_number=pr_number,
        pr_title=pr_title,
        pr_url=pr_url,
        reviewed_head_sha=run_head_sha,
        base_sha=tested_base_sha,
        live_pr_head_sha=live_pr_head_sha,
        run_id=run_id,
        run_attempt=run_attempt,
        run_head_sha=run_head_sha,
        build_provenance=build_provenance,
        task_contract=task_contract,
        ci_jobs=ci_jobs,
        trx_totals=trx_totals,
        manifest=manifest,
        capture_time=capture_time,
        pr_verification=pr_verification,
        live_base_sha=live_base_sha,
        provenance_diagnostic=provenance_diagnostic,
    )

    packet_file = output_dir / "review_packet.md"
    packet_file.write_text(packet_md, encoding="utf-8")
    print(f"Generated review packet at {packet_file}")

    # Generate full diff artifact with explicit omission and status reporting (F12)
    total_manifest_files = len(manifest)
    files_with_patches = sum(1 for f in manifest if f.get("patch"))
    manifest_status = "complete" if files_with_patches == total_manifest_files else f"partial ({files_with_patches}/{total_manifest_files} patches present)"

    full_diff_file = output_dir / f"full-diff-pr-{pr_number}.diff"
    full_diff_content = [
        f"# AI Review Packet Diff for PR #{pr_number} — {pr_title}",
        f"# Triggering run: {run_id} (attempt {run_attempt})",
        f"# Tested Base SHA: {tested_base_sha} -> Run Head SHA: {run_head_sha}",
        f"# Manifest status: {manifest_status}",
        "",
    ]
    for f in manifest:
        patch = f.get("patch")
        fname = f.get("filename", "")
        full_diff_content.append(f"diff --git a/{fname} b/{fname}")
        if patch:
            full_diff_content.append(patch)
        else:
            full_diff_content.append(f"# [Patch omitted by GitHub API for {fname}; inspect repository diff directly]")
        full_diff_content.append("")
    full_diff_file.write_text("\n".join(full_diff_content), encoding="utf-8")
    print(f"Generated full diff artifact at {full_diff_file}")

    # Step Summary
    if step_summary_path and str(step_summary_path):
        with open(step_summary_path, "a", encoding="utf-8") as f:
            f.write(f"### AI Review Packet for PR #{pr_number}\n\n")
            f.write(f"- Reviewed head: `{run_head_sha}`\n")
            f.write(f"- Tested base: `{tested_base_sha}`\n")
            if live_base_sha != tested_base_sha:
                f.write(f"- **WARNING:** Live base branch moved to `{live_base_sha}`\n")
            f.write(f"- Triggering run ID: `{run_id}` (attempt {run_attempt})\n")
            if live_pr_head_sha != run_head_sha:
                f.write(f"- **WARNING:** Live PR head has moved to `{live_pr_head_sha}` (STALE)\n")
            f.write("\nReview packet generated and attached as an artifact.\n")

    # GitHub Output
    if github_output_path and str(github_output_path):
        with open(github_output_path, "a", encoding="utf-8") as f:
            f.write(f"pr_number={pr_number}\n")
            f.write("has_packet=true\n")

    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate immutable AI review packet.")
    parser.add_argument("--event-path", type=Path, default=Path(os.environ.get("GITHUB_EVENT_PATH", "")))
    parser.add_argument("--output-dir", type=Path, default=Path("packet"))
    parser.add_argument("--pr-number", type=int, default=int(os.environ.get("PR_NUMBER", 0)) if os.environ.get("PR_NUMBER") else None)
    args = parser.parse_args()

    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    repo = os.environ.get("GH_REPO") or os.environ.get("GITHUB_REPOSITORY")

    if not token or not repo:
        print("ERROR: GH_TOKEN and GH_REPO must be set.", file=sys.stderr)
        return 1

    if not args.event_path.exists():
        print(f"ERROR: Event file {args.event_path} does not exist.", file=sys.stderr)
        return 1

    with open(args.event_path, "r", encoding="utf-8-sig") as f:
        event = json.load(f)

    client = GitHubClient(token=token, repo=repo)
    summary_path = Path(os.environ["GITHUB_STEP_SUMMARY"]) if os.environ.get("GITHUB_STEP_SUMMARY") else None
    output_path = Path(os.environ["GITHUB_OUTPUT"]) if os.environ.get("GITHUB_OUTPUT") else None

    return generate_packet(
        event=event,
        client=client,
        output_dir=args.output_dir,
        override_pr_number=args.pr_number,
        step_summary_path=summary_path,
        github_output_path=output_path,
    )


if __name__ == "__main__":
    sys.exit(main())
