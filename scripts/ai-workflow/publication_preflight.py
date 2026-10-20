"""Explicit, local, read-only inspection of a bounded publication candidate.

No publishing, identity discovery, remote freshness inference or reusable approval.
Only category/ordinal diagnostics leave the checker. See PRIVACY-HANDLING.md.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import sys
import threading
import time
import unicodedata

from privacy_guard import scan_for_privacy_violations

MAX_OBJECT = 1024 * 1024
MAX_TOTAL = 8 * MAX_OBJECT
MAX_GIT_OUTPUT = 2 * MAX_OBJECT
MAX_FILES = 128
MAX_COMMITS = 64
MAX_TERMS_BYTES = 32768
MAX_FINDINGS = 32
TOTAL_SECONDS = 30
GIT_SECONDS = 10
TEXT_SUFFIXES = frozenset((
    ".txt .md .rst .py .cs .json .xml .yml .yaml .toml .ini .cfg .conf .ps1 "
    ".sh .html .css .js .ts .tsx .jsx .csv .sql .h .hpp .c .cpp .cmake "
    ".sln .csproj .props .targets .svg .gitignore .gitattributes .editorconfig"
).split())
TEXT_NAMES = frozenset({"readme", "license", "licence", "notice", "dockerfile", "makefile",
                        ".gitignore", ".gitattributes", ".editorconfig"})
OID = re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z")
REF = re.compile(r"[A-Za-z0-9][A-Za-z0-9._/-]{0,127}\Z")
NOREPLY = re.compile(
    r"(?:[0-9]+\+)?([A-Za-z0-9][A-Za-z0-9-]{0,38}(?:\[bot\])?)@users\.noreply\.github\.com\Z",
    re.IGNORECASE,
)
IDENTITY = re.compile(r"([^<>\r\n]+) <([^<>\s]+)>\Z")
RAW_HEADER = re.compile(rb":([0-7]{6}) ([0-7]{6}) ([0-9a-f]{40,64}) ([0-9a-f]{40,64}) ([AMDT])\Z")


class Refusal(Exception):
    """Carries only a code authored here, never exception/argument/matched text."""


def canonical(path: Path) -> Path:
    if str(path).startswith(("\\\\", "//")) or "://" in str(path):
        raise Refusal("LocalPathRequired")
    return path.resolve(strict=True)


def inside(path: Path, root: Path) -> bool:
    return path == root or root in path.parents


def decode_text(data: bytes) -> str:
    if b"\x00" in data:
        raise Refusal("UnsupportedBinary")
    try:
        text = data.decode("utf-8-sig", errors="strict")
    except UnicodeError:
        raise Refusal("InvalidUtf8") from None
    if any(ord(c) < 32 and c not in "\r\n\t" for c in text):
        raise Refusal("UnsupportedControlCharacters")
    if text.startswith(("version https://git-lfs.github.com/spec/v1\n",
                        "version https://git-lfs.github.com/spec/v1\r\n")):
        raise Refusal("UnsupportedExternalContent")
    return text


def read_regular(path: Path, limit: int) -> bytes:
    """Bounded explicit file read; never follow a final symlink or special file."""
    if str(path).startswith(("\\\\", "//")) or "://" in str(path):
        raise Refusal("LocalPathRequired")
    before = path.lstat()
    if not stat.S_ISREG(before.st_mode):
        raise Refusal("UnsupportedFileType")
    if before.st_size > limit:
        raise Refusal("InputTooLarge")
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
    with os.fdopen(os.open(path, flags), "rb") as stream:
        opened = os.fstat(stream.fileno())
        if (before.st_dev, before.st_ino) != (opened.st_dev, opened.st_ino):
            raise Refusal("InputChanged")
        data = stream.read(limit + 1)
        after = os.fstat(stream.fileno())
    if len(data) > limit:
        raise Refusal("InputTooLarge")
    if (opened.st_size, opened.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise Refusal("InputChanged")
    return data


def declared_metadata(root: Path) -> tuple[Path, ...]:
    """Bound config reads before Git setup can follow a local include directive."""
    marker = root / ".git"
    mode = marker.lstat().st_mode
    if stat.S_ISDIR(mode):
        directory = canonical(marker)
    elif stat.S_ISREG(mode):
        line = decode_text(read_regular(marker, 4096)).strip()
        if not line.startswith("gitdir: ") or "\n" in line:
            raise Refusal("UnsupportedRepositoryLayout")
        value = Path(line[8:])
        directory = canonical(value if value.is_absolute() else root / value)
    else:
        raise Refusal("UnsupportedRepositoryLayout")
    common = directory
    shared = directory / "commondir"
    if shared.exists():
        value = Path(decode_text(read_regular(shared, 4096)).strip())
        common = canonical(value if value.is_absolute() else directory / value)
    for config in (common / "config", directory / "config.worktree"):
        if config.exists():
            text = decode_text(read_regular(config, 65536))
            if re.search(r"(?im)^\s*\[\s*include", text):
                raise Refusal("UnsupportedConfigInclude")
            if re.search(r"(?m)\\\s*$", text):
                raise Refusal("UnsupportedConfigContinuation")
    return directory, common


class GitReader:
    """Fixed plumbing reads; no shell, filters, hooks, optional locks or lazy fetch."""
    def __init__(self, root: Path):
        self.deadline = time.monotonic() + TOTAL_SECONDS
        self.root = canonical(root)
        self.declared_root = Path(os.path.abspath(root))
        self.metadata_roots = declared_metadata(self.root)
        executable = shutil.which("git")
        if not executable:
            raise Refusal("GitUnavailable")
        if Path(executable).suffix.lower() in (".cmd", ".bat", ".ps1"):
            raise Refusal("NativeGitRequired")
        self.executable = executable

    def run(self, *args: str, limit: int = MAX_GIT_OUTPUT) -> bytes:
        remaining = min(GIT_SECONDS, self.deadline - time.monotonic())
        if remaining <= 0:
            raise Refusal("ReadDeadlineExceeded")
        # Do not forward identity, credential, replacement-object or injected Git-config variables.
        env = {key: os.environ[key] for key in ("PATH", "SystemRoot", "WINDIR") if key in os.environ}
        env.update({
            "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_SYSTEM": os.devnull,
            "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_COUNT": "0",
            "GIT_NO_LAZY_FETCH": "1", "GIT_NO_REPLACE_OBJECTS": "1",
            "GIT_TERMINAL_PROMPT": "0", "GIT_OPTIONAL_LOCKS": "0", "LC_ALL": "C",
        })
        command = [self.executable, "--no-pager", "--no-optional-locks", "--no-replace-objects", "--no-lazy-fetch",
                   "-c", "core.fsmonitor=false", "-c", "core.hooksPath=",
                   "-c", "core.attributesFile=" + os.devnull, "-c", "diff.external=",
                   "-C", str(self.root), *args]
        proc = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                stderr=subprocess.DEVNULL, env=env, shell=False)
        result: list[bytes] = []
        errors: list[bool] = []

        def read():
            try:
                result.append(proc.stdout.read(limit + 1))
            except (OSError, ValueError):
                errors.append(True)

        command_deadline = time.monotonic() + remaining
        reader = threading.Thread(target=read, daemon=True)
        reader.start()
        try:
            reader.join(remaining)
            if reader.is_alive():
                raise Refusal("GitReadTimeout")
            if errors or not result:
                raise Refusal("GitReadFailed")
            if len(result[0]) > limit:
                raise Refusal("GitOutputTooLarge")
            wait = min(command_deadline, self.deadline) - time.monotonic()
            if wait <= 0:
                raise Refusal("ReadDeadlineExceeded")
            if proc.wait(timeout=wait) != 0:
                raise Refusal("GitReadFailed")
            return result[0]
        except subprocess.TimeoutExpired:
            raise Refusal("GitReadTimeout") from None
        finally:
            if proc.poll() is None:
                proc.kill()  # Only this checker-owned read process.
                proc.wait(timeout=2)
            reader.join(2)
            proc.stdout.close()

    def layout(self) -> tuple[Path, ...]:
        top = canonical(Path(decode_text(self.run("rev-parse", "--show-toplevel")).strip()))
        if top != self.root:
            raise Refusal("RepositoryRootRequired")
        roots = [top]
        for option in ("--absolute-git-dir", "--git-common-dir"):
            value = Path(decode_text(self.run("rev-parse", option)).strip())
            roots.append(canonical(value if value.is_absolute() else self.root / value))
        if tuple(roots[1:]) != self.metadata_roots:
            raise Refusal("RepositoryLayoutChanged")
        return (*roots, self.declared_root)

    def resolve(self, ref: str) -> str:
        if not REF.fullmatch(ref) or ".." in ref or "//" in ref or ref.endswith("/"):
            raise Refusal("InvalidRevision")
        oid = decode_text(self.run("rev-parse", "--verify", "--end-of-options", ref + "^{commit}")).strip()
        if not OID.fullmatch(oid):
            raise Refusal("InvalidObjectIdentity")
        return oid

    def object(self, kind: str, oid: str) -> bytes:
        if not OID.fullmatch(oid):
            raise Refusal("InvalidObjectIdentity")
        size_text = self.run("cat-file", "-s", oid, limit=32).strip()
        if not size_text.isdigit() or int(size_text) > MAX_OBJECT:
            raise Refusal("ObjectTooLarge")
        data = self.run("cat-file", kind, oid, limit=MAX_OBJECT)
        if len(data) != int(size_text):
            raise Refusal("IncompleteObjectRead")
        return data


class Inspection:
    def __init__(self, mode: str, terms: tuple[str, ...], approved: set[tuple[str, str]]):
        self.mode, self.terms, self.approved = mode, terms, approved
        self.digest = hashlib.sha256()
        self.findings: set[tuple[str, str, int, int]] = set()
        self.paths: set[str] = set()
        self.path_ordinals: dict[str, int] = {}
        self.blobs: set[str] = set()
        self.commits = 0
        self.total = 0
        self.base_oid: str | None = None
        self.tip_oid: str | None = None

    def feed(self, label: bytes, data: bytes):
        self.total += len(data)
        if self.total > MAX_TOTAL:
            raise Refusal("TotalInputTooLarge")
        for part in (label, data):
            self.digest.update(len(part).to_bytes(8, "big"))
            self.digest.update(part)

    def scan(self, text: str, item: int, source: str = "file"):
        for line, category in scan_for_privacy_violations(text):
            self.findings.add((category, source, item, line))
        folded = unicodedata.normalize("NFC", text).casefold()
        if any(term in folded for term in self.terms):
            self.findings.add(("PrivateTerm", source, item, 0))

    def filename(self, raw: bytes, key: str | None = None) -> int:
        path = decode_text(raw)
        if (not path or len(raw) > 4096 or any(ord(c) < 32 for c in path)
                or path.startswith("/") or any(part in ("", ".", "..") for part in path.split("/"))):
            raise Refusal("UnsupportedFilename")
        selected_key = path if key is None else key
        self.paths.add(selected_key)
        if selected_key not in self.path_ordinals:
            self.path_ordinals[selected_key] = len(self.paths)
        ordinal = self.path_ordinals[selected_key]
        if len(self.paths) > MAX_FILES:
            raise Refusal("TooManyFiles")
        self.scan(path, ordinal)
        name = PurePosixPath(path)
        if name.suffix.lower() not in TEXT_SUFFIXES and name.name.lower() not in TEXT_NAMES:
            raise Refusal("UnsupportedCandidateType")
        return ordinal

    def changes(self, git: GitReader, raw: bytes):
        if not raw:
            return
        if not raw.endswith(b"\0"):
            raise Refusal("MalformedGitOutput")
        fields = raw[:-1].split(b"\0")
        if len(fields) % 2:
            raise Refusal("MalformedGitOutput")
        for pos in range(0, len(fields), 2):
            match = RAW_HEADER.fullmatch(fields[pos])
            if not match:
                raise Refusal("UnsupportedGitChange")
            item = self.filename(fields[pos + 1])
            self.feed(b"change", fields[pos] + b"\0" + fields[pos + 1])
            old_mode, new_mode, old_oid, new_oid, _ = match.groups()
            for mode, oid_raw in ((old_mode, old_oid), (new_mode, new_oid)):
                if mode == b"000000" and set(oid_raw) == {48}:
                    continue
                if mode not in (b"100644", b"100755"):
                    raise Refusal("UnsupportedGitMode")
                oid = oid_raw.decode("ascii")
                if not OID.fullmatch(oid) or set(oid) == {"0"}:
                    raise Refusal("InvalidObjectIdentity")
                if oid not in self.blobs:
                    if len(self.blobs) >= 2 * MAX_FILES:
                        raise Refusal("TooManyObjects")
                    data = git.object("blob", oid)
                    text = decode_text(data)
                    self.feed(oid.encode("ascii"), data)
                    self.scan(text, item)
                    self.blobs.add(oid)

    def commit(self, git: GitReader, oid: str) -> list[str]:
        data = git.object("commit", oid)
        text = decode_text(data)
        self.commits += 1
        self.feed(oid.encode("ascii"), data)
        self.scan(text, self.commits, source="commit")
        if "\n\n" not in text:
            raise Refusal("MalformedCommitMetadata")
        headers, _ = text.split("\n\n", 1)
        parents, identities = [], []
        for line in headers.splitlines():
            if line.startswith(" "):
                continue  # Signature continuation already scanned as text above.
            key, sep, value = line.partition(" ")
            if not sep or key not in ("tree", "parent", "author", "committer", "encoding", "gpgsig"):
                raise Refusal("UnsupportedCommitMetadata")
            if key in ("tree", "parent"):
                if not OID.fullmatch(value):
                    raise Refusal("MalformedCommitMetadata")
                if key == "parent":
                    parents.append(value)
            elif key == "encoding" and value.lower() not in ("utf-8", "utf8"):
                raise Refusal("UnsupportedCommitEncoding")
            elif key in ("author", "committer"):
                parsed = re.fullmatch(r"(.+ <[^<>]+>) [0-9]+ [+-][0-9]{4}", value)
                if not parsed:
                    raise Refusal("MalformedCommitIdentity")
                identity = IDENTITY.fullmatch(parsed.group(1))
                if not identity:
                    raise Refusal("MalformedCommitIdentity")
                name, email = identity.groups()
                noreply = NOREPLY.fullmatch(email)
                if not ((noreply and name.casefold() == noreply.group(1).casefold())
                        or (name, email) in self.approved):
                    self.findings.add(("UnapprovedCommitIdentity", "commit", self.commits, 0))
                identities.append(key)
        if sorted(identities) != ["author", "committer"] or len(parents) > 8:
            raise Refusal("MalformedCommitMetadata")
        return parents

    def report(self, complete: bool, reason: str | None = None) -> dict:
        findings = sorted(self.findings)
        return {
            "mode": self.mode, "complete": complete,
            "outcome": "incomplete" if not complete else ("refused" if findings else "checked"),
            "candidate_sha256": self.digest.hexdigest() if complete else None,
            "base_oid": self.base_oid, "tip_oid": self.tip_oid,
            "files": len(self.paths), "blobs": len(self.blobs), "commits": self.commits,
            "inspected_bytes": self.total,
            "findings": [{"category": c, "source": source, "item": i, "line": line}
                         for c, source, i, line in findings[:MAX_FINDINGS]],
            "additional_findings": max(0, len(findings) - MAX_FINDINGS),
            "reason": reason,
            "limits": {"object_bytes": MAX_OBJECT, "total_bytes": MAX_TOTAL,
                       "files": MAX_FILES, "commits": MAX_COMMITS, "seconds": TOTAL_SECONDS},
            "coverage": "Only selected inputs; no authorization/scope/remote freshness or attachment assurance.",
            "commit_metadata": ("checked" if complete else "incomplete") if self.mode == "commits" else "not_in_this_candidate",
            "binding": "A later changed payload requires another check; this is not a publication approval.",
        }


def private_terms(path: str | None, roots: tuple[Path, ...]) -> tuple[str, ...]:
    if path is None:
        return ()
    source = Path(path)
    resolved = canonical(source)
    tool_root = Path(__file__).resolve().parents[2]
    lexical = Path(os.path.abspath(source))
    if any(inside(candidate, root) for root in (*roots, tool_root) for candidate in (resolved, lexical)):
        raise Refusal("TermsFileInsideRepository")
    text = decode_text(read_regular(source, MAX_TERMS_BYTES))
    terms = tuple(unicodedata.normalize("NFC", term.strip()).casefold()
                  for term in text.splitlines() if term.strip())
    if not terms or len(terms) > 256 or any(not 3 <= len(term) <= 128 for term in terms):
        raise Refusal("InvalidTermsFile")
    return terms


def approved_identities(values: list[str]) -> set[tuple[str, str]]:
    approved = set()
    if len(values) > 16:
        raise Refusal("TooManyPublicIdentities")
    for value in values:
        match = IDENTITY.fullmatch(value)
        if not match:
            raise Refusal("InvalidPublicIdentity")
        name, email = match.groups()
        if (not re.fullmatch(r"[A-Za-z][A-Za-z0-9 ._-]{0,63}", name)
                or not re.fullmatch(r"[A-Za-z0-9._-]+@[A-Za-z0-9.-]+\.invalid", email)):
            raise Refusal("InvalidPublicIdentity")
        approved.add((name, email))
    return approved


DIFF_OPTIONS = ("--raw", "--no-abbrev", "--no-renames", "--no-ext-diff", "--no-textconv",
                "--ignore-submodules=none", "-z")


def inspect(args) -> dict:
    mode = "text" if args.text_file else ("staged" if args.staged else "commits")
    check = Inspection(mode, (), set())
    try:
        git = GitReader(Path(args.repo))
        roots = git.layout()
        check.terms = private_terms(args.private_terms, roots)
        check.approved = approved_identities(args.public_identity)
        if mode == "text":
            if len(args.text_file) > MAX_FILES:
                raise Refusal("TooManyFiles")
            for ordinal, filename in enumerate(args.text_file, 1):
                path = Path(filename)
                name = path.name.encode("utf-8", errors="strict")
                check.filename(name, key="text-" + str(ordinal))
                data = read_regular(path, MAX_OBJECT)
                check.feed(name + ordinal.to_bytes(4, "big"), data)
                check.scan(decode_text(data), ordinal)
        elif mode == "staged":
            base = git.resolve("HEAD")  # Unborn/missing HEAD refuses rather than inventing a baseline.
            check.base_oid = base
            check.feed(b"base", base.encode("ascii"))
            raw = git.run("diff-index", "--cached", *DIFF_OPTIONS, base, "--")
            check.changes(git, raw)
            if git.resolve("HEAD") != base or git.run("diff-index", "--cached", *DIFF_OPTIONS, base, "--") != raw:
                raise Refusal("CandidateChanged")
        else:
            if git.run("rev-parse", "--is-shallow-repository").strip() != b"false":
                raise Refusal("UnsupportedShallowHistory")
            if any((root / "info" / "grafts").exists() for root in git.metadata_roots):
                raise Refusal("UnsupportedHistoryOverlay")
            if args.commits.count("..") != 1 or "..." in args.commits:
                raise Refusal("InvalidCommitRange")
            lower, upper = args.commits.split("..")
            base, tip = git.resolve(lower), git.resolve(upper)
            check.base_oid, check.tip_oid = base, tip
            check.feed(b"range", (base + ".." + tip).encode("ascii"))
            raw = git.run("rev-list", "--reverse", "--topo-order", base + ".." + tip)
            commits = raw.decode("ascii", errors="strict").splitlines()
            if len(commits) > MAX_COMMITS:
                raise Refusal("TooManyCommits")
            for oid in commits:
                parents = check.commit(git, oid)
                if parents:
                    for parent in parents:
                        check.changes(git, git.run("diff-tree", "-r", "--no-commit-id", *DIFF_OPTIONS, parent, oid, "--"))
                else:
                    check.changes(git, git.run("diff-tree", "--root", "-r", "--no-commit-id", *DIFF_OPTIONS, oid, "--"))
        if time.monotonic() > git.deadline:
            raise Refusal("ReadDeadlineExceeded")
        return check.report(True)
    except Refusal as exc:
        return check.report(False, str(exc))
    except (OSError, ValueError, UnicodeError, subprocess.SubprocessError):
        return check.report(False, "ReadFailed")


class SafeParser(argparse.ArgumentParser):
    def error(self, message):
        raise Refusal("InvalidArguments")


def main(argv: list[str] | None = None) -> int:
    parser = SafeParser(prog="publication_preflight.py", description=__doc__)
    parser.add_argument("--repo", required=True, help="Explicit local checkout root (also the private-terms boundary).")
    modes = parser.add_mutually_exclusive_group(required=True)
    modes.add_argument("--text-file", action="append", help="Explicit UTF-8 publication text file; repeatable.")
    modes.add_argument("--staged", action="store_true", help="Selected index changes only; requires an existing HEAD.")
    modes.add_argument("--commits", help="Explicit local BASE..TIP range; no fetch or remote-ref inference.")
    parser.add_argument("--private-terms", help="Optional external UTF-8 literal terms, never echoed or discovered.")
    parser.add_argument("--public-identity", action="append", default=[], help="Explicit approved Generic Name <agent@example.invalid> entry.")
    try:
        values = sys.argv[1:] if argv is None else argv
        if len(values) > 2 * MAX_FILES + 40 or any(len(value) > 4096 for value in values):
            raise Refusal("InvalidArguments")
        args = parser.parse_args(values)
        report = inspect(args)
    except Refusal:
        report = Inspection("invalid", (), set()).report(False, "InvalidArguments")
    print(json.dumps(report, sort_keys=True, ensure_ascii=True))
    return 2 if not report["complete"] else (1 if report["findings"] else 0)


if __name__ == "__main__":
    raise SystemExit(main())
