"""Parses Visual Studio TRX test result files and extracts test counts safely."""

from __future__ import annotations

import io
import os
import re
import sys
import unicodedata
import xml.etree.ElementTree as ET
import zipfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import List, Optional

try:
    from .privacy_guard import redact_privacy_text, sanitize_display_text
except ImportError:
    from privacy_guard import redact_privacy_text, sanitize_display_text


MAX_FAILURE_DETAILS = 5
MAX_FAILURE_NAME = 120
MAX_FAILURE_MESSAGE = 240


def _safe_detail_text(value: object, limit: int, fallback: str) -> tuple[str, bool]:
    """Redact entire bounded fields before first-line selection or display truncation."""
    if not isinstance(value, str) or not value.strip():
        return fallback, False
    if len(value) > 65536:
        return "Field omitted: size limit exceeded.", True
    # Identity assignments/email addresses are omitted conservatively in this derived projection.
    # Pattern redaction is not general personal-information recognition.
    if re.search(r"\b(?:host(?:name)?|computer(?:name)?|machine(?:name)?|user(?:name)?)\s*[:=]", value, re.I) or re.search(r"[\w.+-]+@[\w.-]+\.[\w-]+", value):
        return "[REDACTED_IDENTITY_FIELD]", True
    clean = redact_privacy_text(value)
    clean = re.sub(r"\b(?:https?|file)://\S+", "[URL omitted]", clean, flags=re.I)
    logical_lines = [line.strip() for line in clean.splitlines() if line.strip()]
    first = logical_lines[0] if logical_lines else ""
    if first.startswith("at ") or first.startswith("--- End of stack trace"):
        return "Stack trace omitted.", True
    first = "".join(c for c in first if unicodedata.category(c) not in ("Cc", "Cf"))
    formatted = sanitize_display_text(first, max_len=65536)
    truncated = len(logical_lines) > 1 or len(formatted) > limit
    return sanitize_display_text(formatted, max_len=limit) or fallback, truncated


@dataclass
class TrxFailureDetail:
    name: str
    message: str
    name_truncated: bool = False
    message_truncated: bool = False


def _failure_records(root: ET.Element) -> tuple[List[TrxFailureDetail], int]:
    details = []
    observed = 0
    for result in root.iter():
        if result.tag.rsplit("}", 1)[-1] != "UnitTestResult" or result.get("outcome", "").lower() not in ("failed", "error", "timeout", "aborted"):
            continue
        observed += 1
        if len(details) == MAX_FAILURE_DETAILS:
            continue
        message = None
        current = result
        for component in ("Output", "ErrorInfo", "Message"):
            current = next((child for child in current if child.tag.rsplit("}", 1)[-1] == component), None)
            if current is None:
                break
        if current is not None:
            message = current.text
        name, name_cut = _safe_detail_text(result.get("testName"), MAX_FAILURE_NAME, "Unnamed failed test")
        text, text_cut = _safe_detail_text(message, MAX_FAILURE_MESSAGE, "Message unavailable.")
        details.append(TrxFailureDetail(name, text, name_cut, text_cut))
    return details, observed


def _positive_identifier(value: object) -> Optional[str]:
    text = str(value)
    return text if len(text) <= 20 and text.isascii() and text.isdecimal() and int(text) > 0 else None


def sanitize_trx_source_name(value: object) -> str:
    return _safe_detail_text(value, 80, "TRX artifact")[0]


def sanitize_trx_reason(value: object) -> str:
    return _safe_detail_text(value, 160, "TRX results unavailable.")[0]


def build_failure_summary(totals: "TrxTotals", run_id: object, attempt: object, artifact_id: object) -> Optional[dict]:
    """Small optional projection. Counters and attribution selection are never inferred here."""
    if totals.failed == 0 and totals.failure_details_observed == 0:
        return None
    ids = [_positive_identifier(value) for value in (run_id, attempt, artifact_id)]
    attributed = all(ids) and totals.status != "unavailable"
    entries = []
    if attributed:
        for detail in totals.failure_details[:MAX_FAILURE_DETAILS]:
            name, name_cut = _safe_detail_text(detail.name, MAX_FAILURE_NAME, "Unnamed failed test")
            message, message_cut = _safe_detail_text(detail.message, MAX_FAILURE_MESSAGE, "Message unavailable.")
            entries.append({"name": name, "message": message,
                            "name_truncated": detail.name_truncated or name_cut,
                            "message_truncated": detail.message_truncated or message_cut})
    source, _ = _safe_detail_text(totals.source_name, 80, "TRX artifact")
    return {"status": "available" if entries else "unavailable", "run_id": ids[0], "run_attempt": ids[1],
            "artifact_id": ids[2], "source_name": source, "entries": entries,
            "observed_failure_records": totals.failure_details_observed,
            "entries_truncated": totals.failure_details_observed > MAX_FAILURE_DETAILS,
            "reason": None if entries else ("no_failure_records" if attributed else "unattributed")}


def sanitize_failure_summary(summary: Optional[dict], run_id: object, attempt: object) -> Optional[dict]:
    """Revalidate optional serialized projection on output, including its enclosing run binding."""
    if summary is None:
        return None
    expected = [_positive_identifier(run_id), _positive_identifier(attempt)]
    fallback = {"status": "unavailable", "run_id": expected[0], "run_attempt": expected[1],
                "artifact_id": None, "source_name": "TRX artifact", "entries": [],
                "observed_failure_records": 0, "entries_truncated": False, "reason": "attribution_mismatch"}
    if not isinstance(summary, dict):
        return fallback
    ids = [_positive_identifier(summary.get(key)) for key in ("run_id", "run_attempt", "artifact_id")]
    if not all(expected) or not all(ids) or ids[:2] != expected:
        return fallback
    entries = summary.get("entries", [])
    if not isinstance(entries, list):
        return fallback
    observed = summary.get("observed_failure_records", 0)
    if type(observed) is not int or not 0 <= observed <= 10**9:
        return fallback
    safe_entries = []
    if summary.get("status") == "available":
        for entry in entries[:MAX_FAILURE_DETAILS]:
            if not isinstance(entry, dict):
                return fallback
            name, name_cut = _safe_detail_text(entry.get("name"), MAX_FAILURE_NAME, "Unnamed failed test")
            message, message_cut = _safe_detail_text(entry.get("message"), MAX_FAILURE_MESSAGE, "Message unavailable.")
            safe_entries.append({"name": name, "message": message,
                                 "name_truncated": bool(entry.get("name_truncated")) or name_cut,
                                 "message_truncated": bool(entry.get("message_truncated")) or message_cut})
    return {"status": "available" if safe_entries else "unavailable", "run_id": ids[0], "run_attempt": ids[1],
            "artifact_id": ids[2], "source_name": sanitize_trx_source_name(summary.get("source_name")),
            "entries": safe_entries, "observed_failure_records": max(observed, len(safe_entries)),
            "entries_truncated": bool(summary.get("entries_truncated")) or len(entries) > MAX_FAILURE_DETAILS or observed > len(safe_entries),
            "reason": None if safe_entries else "no_failure_records"}


def render_failure_summary(summary: Optional[dict], run_id: object, attempt: object) -> List[str]:
    """Render only the bounded projection, never XML, stack traces or arbitrary exceptions."""
    summary = sanitize_failure_summary(summary, run_id, attempt)
    if summary is None:
        return []
    ids = [_positive_identifier(summary.get(key)) for key in ("run_id", "run_attempt", "artifact_id")]
    if not all(ids):
        return ["- Failure details: UNAVAILABLE (attribution mismatch or incomplete run/attempt/artifact)."]
    source, _ = _safe_detail_text(summary.get("source_name"), 80, "TRX artifact")
    lines = [f"- Failure detail source: run {ids[0]}, attempt {ids[1]}, artifact {ids[2]} ({source})."]
    entries = summary.get("entries")
    if summary.get("status") != "available" or not isinstance(entries, list) or not entries:
        return lines + ["- Failure details: UNAVAILABLE (no failure records in the selected TRX)."]
    shown = [entry for entry in entries[:MAX_FAILURE_DETAILS] if isinstance(entry, dict)]
    observed = summary.get("observed_failure_records")
    observed = max(len(shown), observed) if type(observed) is int and observed >= 0 else len(shown)
    lines.append(f"- Failure details: showing {len(shown)} of {observed} observed failure records; aggregate totals unchanged.")
    for entry in shown:
        name, name_cut = _safe_detail_text(entry.get("name"), MAX_FAILURE_NAME, "Unnamed failed test")
        message, message_cut = _safe_detail_text(entry.get("message"), MAX_FAILURE_MESSAGE, "Message unavailable.")
        suffix = " [field content omitted]" if name_cut or message_cut or entry.get("name_truncated") or entry.get("message_truncated") else ""
        lines.append(f"  - {name}: {message}{suffix}")
    if observed > len(shown) or summary.get("entries_truncated"):
        lines.append("- Additional failure records omitted (display limit or word budget); see original bound artifact for full evidence.")
    return lines


class SecurityValidationError(Exception):
    """Raised when an artifact violates security invariants (Zip Slip, size limit, etc.)."""
    pass


@dataclass
class TrxTotals:
    status: str  # "passed", "failed", "unavailable"
    total: int
    passed: int
    failed: int
    skipped: int
    source_name: str
    unavailable_reason: str = ""
    failure_details: List[TrxFailureDetail] = field(default_factory=list)
    failure_details_observed: int = 0


def parse_trx_content(xml_content: str, source_name: str = "test-results.trx") -> TrxTotals:
    """Parses TRX XML string content and returns structured test totals."""
    source_name, _ = _safe_detail_text(source_name, 80, "TRX artifact")
    if not xml_content or not xml_content.strip():
        return TrxTotals(
            status="unavailable",
            total=0,
            passed=0,
            failed=0,
            skipped=0,
            source_name=source_name,
            unavailable_reason="TRX content is empty.",
        )

    try:
        root = ET.fromstring(xml_content)
    except ET.ParseError:
        return TrxTotals(
            status="unavailable",
            total=0,
            passed=0,
            failed=0,
            skipped=0,
            source_name=source_name,
            unavailable_reason="Malformed XML in TRX file.",
        )

    # Search for Counters element across all namespaces
    counters_el = None
    for el in root.iter():
        if el.tag.endswith("Counters"):
            counters_el = el
            break

    if counters_el is None:
        return TrxTotals(
            status="unavailable",
            total=0,
            passed=0,
            failed=0,
            skipped=0,
            source_name=source_name,
            unavailable_reason="No <Counters> element found in TRX file.",
        )

    try:
        total = int(counters_el.get("total", "0"))
        passed = int(counters_el.get("passed", "0"))
        failed = int(counters_el.get("failed", "0"))
        error = int(counters_el.get("error", "0"))
        timeout = int(counters_el.get("timeout", "0"))
        aborted = int(counters_el.get("aborted", "0"))
        not_executed = int(counters_el.get("notExecuted", "0"))
        not_runnable = int(counters_el.get("notRunnable", "0"))
        inconclusive = int(counters_el.get("inconclusive", "0"))

        effective_failed = failed + error + timeout + aborted
        skipped = not_executed + not_runnable + inconclusive

        # Determine status
        status = "passed"
        if effective_failed > 0:
            status = "failed"
        elif total == 0:
            status = "unavailable"

        failure_details, observed = _failure_records(root)
        return TrxTotals(
            status=status,
            total=total,
            passed=passed,
            failed=effective_failed,
            skipped=skipped,
            source_name=source_name,
            failure_details=failure_details,
            failure_details_observed=observed,
        )
    except (ValueError, TypeError):
        return TrxTotals(
            status="unavailable",
            total=0,
            passed=0,
            failed=0,
            skipped=0,
            source_name=source_name,
            unavailable_reason="Failed to parse numeric counters.",
        )


def parse_trx_file(file_path: Optional[Path | str]) -> TrxTotals:
    """Reads a TRX file from disk and parses its totals."""
    if file_path is None:
        return TrxTotals(
            status="unavailable",
            total=0,
            passed=0,
            failed=0,
            skipped=0,
            source_name="",
            unavailable_reason="TRX file path not found or specified.",
        )

    path = Path(file_path)
    if not path.exists() or not path.is_file():
        return TrxTotals(
            status="unavailable",
            total=0,
            passed=0,
            failed=0,
            skipped=0,
            source_name=_safe_detail_text(path.name, 80, "TRX artifact")[0],
            unavailable_reason="TRX file not found.",
        )

    try:
        content = path.read_text(encoding="utf-8")
        return parse_trx_content(content, source_name=path.name)
    except Exception:
        return TrxTotals(
            status="unavailable",
            total=0,
            passed=0,
            failed=0,
            skipped=0,
            source_name=_safe_detail_text(path.name, 80, "TRX artifact")[0],
            unavailable_reason="Error reading TRX file.",
        )


def safe_extract_zip(
    zip_bytes: bytes,
    destination_dir: Path,
    max_uncompressed_bytes: int = 50 * 1024 * 1024,
) -> List[Path]:
    """
    Safely extracts a zip archive in memory to destination_dir with Zip-Slip protection
    and uncompressed size bounding. Only extracts .trx files.
    """
    dest_resolved = destination_dir.resolve()
    dest_resolved.mkdir(parents=True, exist_ok=True)

    extracted_files: List[Path] = []
    total_uncompressed = 0

    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as zf:
        # Pre-check total uncompressed size
        for info in zf.infolist():
            total_uncompressed += info.file_size
            if total_uncompressed > max_uncompressed_bytes:
                raise SecurityValidationError(
                    f"Archive uncompressed size ({total_uncompressed} bytes) exceeds size limit ({max_uncompressed_bytes} bytes)."
                )

        # Extract safely
        for info in zf.infolist():
            # Security: Zip Slip defense
            target_path = (dest_resolved / info.filename).resolve()
            try:
                target_path.relative_to(dest_resolved)
            except ValueError:
                raise SecurityValidationError(
                    f"Path traversal detected in archive entry: {info.filename}"
                )

            if info.is_dir():
                target_path.mkdir(parents=True, exist_ok=True)
                continue

            # Only extract .trx files
            if target_path.suffix.lower() == ".trx":
                target_path.parent.mkdir(parents=True, exist_ok=True)
                with zf.open(info) as source, open(target_path, "wb") as target:
                    target.write(source.read())
                extracted_files.append(target_path)

    return extracted_files



def safe_extract_json_from_zip(
    zip_bytes: bytes,
    target_filename: str = "build-provenance.json",
    max_uncompressed_bytes: int = 10 * 1024 * 1024,
) -> Optional[dict]:
    """
    Safely extracts and parses a specific JSON file from a zip archive in memory.
    Enforces Zip-Slip protection, size bounding, and JSON validation.
    """
    import json
    total_uncompressed = 0
    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as zf:
        for info in zf.infolist():
            total_uncompressed += info.file_size
            if total_uncompressed > max_uncompressed_bytes:
                raise SecurityValidationError(
                    f"Archive uncompressed size ({total_uncompressed} bytes) exceeds limit ({max_uncompressed_bytes} bytes)."
                )

        for info in zf.infolist():
            if info.is_dir():
                continue
            normalized_name = os.path.normpath(info.filename).replace("\\", "/")
            if normalized_name.startswith("../") or "/../" in normalized_name or normalized_name.startswith("/"):
                raise SecurityValidationError(f"Path traversal detected in archive entry: {info.filename}")

            if Path(info.filename).name == target_filename:
                content = zf.read(info).decode("utf-8")
                return json.loads(content)
    return None

