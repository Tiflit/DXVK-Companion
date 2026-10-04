"""Parses Visual Studio TRX test result files and extracts test counts safely."""

from __future__ import annotations

import io
import os
import sys
import xml.etree.ElementTree as ET
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import List, Optional


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


def parse_trx_content(xml_content: str, source_name: str = "test-results.trx") -> TrxTotals:
    """Parses TRX XML string content and returns structured test totals."""
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
    except ET.ParseError as e:
        return TrxTotals(
            status="unavailable",
            total=0,
            passed=0,
            failed=0,
            skipped=0,
            source_name=source_name,
            unavailable_reason=f"Malformed XML in TRX file: {e}",
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

        return TrxTotals(
            status=status,
            total=total,
            passed=passed,
            failed=effective_failed,
            skipped=skipped,
            source_name=source_name,
        )
    except (ValueError, TypeError) as e:
        return TrxTotals(
            status="unavailable",
            total=0,
            passed=0,
            failed=0,
            skipped=0,
            source_name=source_name,
            unavailable_reason=f"Failed to parse numeric counters: {e}",
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
            source_name=path.name,
            unavailable_reason=f"File not found: {path}",
        )

    try:
        content = path.read_text(encoding="utf-8")
        return parse_trx_content(content, source_name=path.name)
    except Exception as e:
        return TrxTotals(
            status="unavailable",
            total=0,
            passed=0,
            failed=0,
            skipped=0,
            source_name=path.name,
            unavailable_reason=f"Error reading TRX file: {e}",
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

