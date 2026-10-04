"""Generates an immutable, revision-accurate AI Review Packet."""

from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import sys
import tempfile
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

try:
    from .parse_contract import ContractParseError, extract_primary_issue
    from .parse_trx import TrxTotals, parse_trx_content, parse_trx_file, safe_extract_zip
except ImportError:
    from parse_contract import ContractParseError, extract_primary_issue
    from parse_trx import TrxTotals, parse_trx_content, parse_trx_file, safe_extract_zip


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
) -> str:
    """Builds the formatted markdown content of the AI Review Packet in strict section order."""
    lines: List[str] = []

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
        lines.append("- Tested checkout SHA: `incomplete` (build-provenance.json was unavailable; checkout identity is not assumed)")

    lines.append(f"- Base SHA: `{base_sha}`")
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

    # Test Totals Breakdown
    if trx_totals and trx_totals.get("status") in ("passed", "failed"):
        lines.append("### Structured Test Totals (TRX)")
        lines.append(f"- Status: `{trx_totals.get('status').upper()}`")
        lines.append(f"- Total tests: {trx_totals.get('total')}")
        lines.append(f"- Passed: {trx_totals.get('passed')}")
        lines.append(f"- Failed: {trx_totals.get('failed')}")
        lines.append(f"- Skipped: {trx_totals.get('skipped')}")
        lines.append(f"- Provenance source: `{trx_totals.get('source', 'TRX artifact')}`")
    else:
        reason = trx_totals.get("reason", "No test artifact available") if trx_totals else "No test artifact"
        lines.append("### Structured Test Totals (TRX)")
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
    lines.append("## 4. Complete Changed Files Manifest")
    lines.append("")
    if manifest:
        for item in manifest:
            filename = item.get("filename", "")
            additions = item.get("additions", 0)
            deletions = item.get("deletions", 0)
            status = item.get("status", "modified")
            lines.append(f"- `{filename}` (+{additions}/-{deletions}) — {status}")
    else:
        lines.append("No changed files in manifest.")
    lines.append("")

    # Split files into categories for budgeted diffs
    test_files = []
    prod_files = []
    workflow_doc_files = []

    for item in manifest:
        fn = item.get("filename", "")
        if "test" in fn.lower() or fn.startswith("tests/"):
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


def fetch_github_api(url: str, token: str) -> Any:
    """Helper to fetch GitHub API with authorization header and error handling."""
    req = urllib.request.Request(
        url,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github.v3+json",
            "User-Agent": "DXVK-Companion-AI-Workflow",
        },
    )
    with urllib.request.urlopen(req) as resp:
        return json.loads(resp.read().decode("utf-8"))


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate immutable AI review packet.")
    parser.add_argument("--event-path", type=Path, default=Path(os.environ.get("GITHUB_EVENT_PATH", "")))
    parser.add_argument("--output-dir", type=Path, default=Path("packet"))
    args = parser.parse_args()

    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    repo = os.environ.get("GH_REPO") or os.environ.get("GITHUB_REPOSITORY")

    if not token or not repo:
        print("ERROR: GH_TOKEN and GH_REPO must be set.", file=sys.stderr)
        return 1

    if not args.event_path.exists():
        print(f"ERROR: Event file {args.event_path} does not exist.", file=sys.stderr)
        return 1

    with open(args.event_path, "r", encoding="utf-8") as f:
        event = json.load(f)

    workflow_run = event.get("workflow_run", {})
    if not workflow_run:
        print("ERROR: Event does not contain workflow_run.", file=sys.stderr)
        return 1

    pull_requests = workflow_run.get("pull_requests", [])
    if not pull_requests:
        print("INFO: Completed workflow_run is not associated with any pull request; nothing to package.")
        return 0

    if len(pull_requests) > 1:
        print(f"WARNING: workflow_run is associated with multiple pull requests: {[p.get('number') for p in pull_requests]}")

    pr_summary = pull_requests[0]
    pr_number = pr_summary.get("number")
    run_id = str(workflow_run.get("id"))
    run_attempt = str(workflow_run.get("run_attempt", "1"))
    run_head_sha = workflow_run.get("head_sha", "")

    # Fetch full PR metadata
    pr_data = fetch_github_api(f"https://api.github.com/repos/{repo}/pulls/{pr_number}", token)
    live_pr_head_sha = pr_data.get("head", {}).get("sha", "")
    base_sha = pr_data.get("base", {}).get("sha", "")
    pr_title = pr_data.get("title", "")
    pr_url = pr_data.get("html_url", "")
    pr_body = pr_data.get("body", "")

    # Resolve task contract issue
    task_contract = None
    try:
        primary_issue_num = extract_primary_issue(pr_body)
        issue_data = fetch_github_api(f"https://api.github.com/repos/{repo}/issues/{primary_issue_num}", token)
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

    # Fetch jobs
    jobs_data = fetch_github_api(f"https://api.github.com/repos/{repo}/actions/runs/{run_id}/jobs?per_page=100", token)
    ci_jobs = jobs_data.get("jobs", [])

    # Fetch changed files comparison between base and run_head_sha
    compare_data = fetch_github_api(f"https://api.github.com/repos/{repo}/compare/{base_sha}...{run_head_sha}", token)
    manifest = compare_data.get("files", [])

    # Process TRX results and provenance from run artifacts
    artifacts_data = fetch_github_api(f"https://api.github.com/repos/{repo}/actions/runs/{run_id}/artifacts", token)
    artifacts = artifacts_data.get("artifacts", [])

    trx_totals = {"status": "unavailable", "reason": "No TRX artifact found"}
    build_provenance = None

    args.output_dir.mkdir(parents=True, exist_ok=True)

    for art in artifacts:
        art_name = art.get("name", "")
        art_download_url = art.get("archive_download_url", "")
        if not art_download_url:
            continue

        if art_name == "phase-a-test-results":
            try:
                req = urllib.request.Request(
                    art_download_url,
                    headers={"Authorization": f"Bearer {token}", "User-Agent": "DXVK-Companion-AI-Workflow"},
                )
                with urllib.request.urlopen(req) as resp:
                    zip_data = resp.read()
                with tempfile.TemporaryDirectory() as tmp_dir:
                    extracted = safe_extract_zip(zip_data, Path(tmp_dir))
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
                req = urllib.request.Request(
                    art_download_url,
                    headers={"Authorization": f"Bearer {token}", "User-Agent": "DXVK-Companion-AI-Workflow"},
                )
                with urllib.request.urlopen(req) as resp:
                    zip_data = resp.read()
                with tempfile.TemporaryDirectory() as tmp_dir:
                    import zipfile
                    with zipfile.ZipFile(io.BytesIO(zip_data)) as zf:
                        for zinfo in zf.infolist():
                            if zinfo.filename == "build-provenance.json":
                                build_provenance = json.loads(zf.read(zinfo).decode("utf-8"))
                                break
            except Exception as e:
                print(f"WARNING: Could not parse build-provenance artifact: {e}")

    capture_time = datetime.datetime.now(datetime.timezone.utc).isoformat()

    packet_md = build_packet_content(
        repo=repo,
        pr_number=pr_number,
        pr_title=pr_title,
        pr_url=pr_url,
        reviewed_head_sha=run_head_sha,
        base_sha=base_sha,
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
    )

    packet_file = args.output_dir / "review_packet.md"
    packet_file.write_text(packet_md, encoding="utf-8")
    print(f"Generated review packet at {packet_file}")

    # Generate full diff artifact
    full_diff_file = args.output_dir / f"full-diff-pr-{pr_number}.diff"
    full_diff_content = []
    for f in manifest:
        patch = f.get("patch")
        if patch:
            full_diff_content.append(f"diff --git a/{f.get('filename')} b/{f.get('filename')}")
            full_diff_content.append(patch)
            full_diff_content.append("")
    full_diff_file.write_text("\n".join(full_diff_content), encoding="utf-8")
    print(f"Generated full diff artifact at {full_diff_file}")

    # Write step summary
    summary_file = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_file:
        with open(summary_file, "a", encoding="utf-8") as f:
            f.write(f"### AI Review Packet for PR #{pr_number}\n\n")
            f.write(f"- Reviewed head: `{run_head_sha}`\n")
            f.write(f"- Base: `{base_sha}`\n")
            f.write(f"- Triggering run ID: `{run_id}` (attempt {run_attempt})\n")
            if live_pr_head_sha != run_head_sha:
                f.write(f"- **WARNING:** Live PR head has moved to `{live_pr_head_sha}` (STALE)\n")
            f.write("\nReview packet generated and attached as an artifact.\n")

    github_output_file = os.environ.get("GITHUB_OUTPUT")
    if github_output_file:
        with open(github_output_file, "a", encoding="utf-8") as f:
            f.write(f"pr_number={pr_number}\n")
            f.write("has_packet=true\n")

    return 0


if __name__ == "__main__":
    sys.exit(main())
