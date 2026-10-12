import builtins
import hashlib
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import preview_role_handoff


def compute_dir_hashes(root_dir: Path) -> dict[str, str]:
    """Computes SHA-256 hashes for all regular files in a directory tree."""
    hashes = {}
    for p in sorted(root_dir.rglob("*")):
        if p.is_file():
            rel = str(p.relative_to(root_dir)).replace("\\", "/")
            hashes[rel] = hashlib.sha256(p.read_bytes()).hexdigest()
    return hashes


def make_valid_snapshot() -> dict:
    """Returns a full valid snapshot matching generate_handoff.py schema."""
    return {
        "capture_time_utc": "2026-10-12T19:00:00Z",
        "repo": "Tiflit/DXVK-Companion",
        "pr_number": 86,
        "pr_title": "[AI] Add read-only operator-invoked scope and PR hygiene preflight",
        "pr_state": "OPEN",
        "pr_merged": False,
        "live_pr_head_sha": "c700fcd16091e946246694565d1a5f55fdd3ad1e",
        "live_pr_base_sha": "3c231c1ab67c2b94b7aef76c536e8ca83a5810f3",
        "pr_base_ref": "main",
        "repo_default_branch": "main",
        "live_default_branch_sha": "91d72bc015559b2268abb7b9b3a033c9f63bad05",
        "base_sync_status": "synced",
        "base_has_moved": False,
        "primary_issue_number": 85,
        "primary_issue_title": "[AI] Add read-only operator-invoked scope and PR hygiene preflight",
        "primary_issue_state": "OPEN",
        "decision_governance": {
            "required": None,
            "proposed_option": None,
            "status": "NOT_REQUIRED",
            "approval_source": None,
        },
        "local_workspace": {
            "branch": "feat/read-only-workflow-preflight",
            "head_sha": "c700fcd16091e946246694565d1a5f55fdd3ad1e",
            "status": "clean",
            "is_git": True,
        },
        "ci_evidence": {
            "run_id": "38293235222",
            "run_attempt": "1",
            "conclusion": "SUCCESS",
            "head_sha": "c700fcd16091e946246694565d1a5f55fdd3ad1e",
            "tested_checkout_sha": "c4d849a549c307fe727c193fa348e8e51973045c",
            "tested_checkout_ref": "refs/pull/86/merge",
            "unverified_checkout_details": None,
            "trx_totals": {
                "passed": 378,
                "failed": 0,
                "skipped": 0,
                "total": 378,
            },
        },
        "review_records": [],
        "next_ownership": {
            "owner": "ChatGPT",
            "action": "Coordinator verification of current head",
        },
        "uncertainties": [],
    }


class TestRoleHandoffPreview(unittest.TestCase):
    """Test suite for compact read-only role handoff preview CLI."""

    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.dir_path = Path(self.temp_dir.name)

    def tearDown(self):
        self.temp_dir.cleanup()

    def _write_file(self, name: str, content: str, encoding: str = "utf-8") -> Path:
        file_path = self.dir_path / name
        file_path.write_text(content, encoding=encoding)
        return file_path

    def _write_bytes(self, name: str, data: bytes) -> Path:
        file_path = self.dir_path / name
        file_path.write_bytes(data)
        return file_path

    def test_valid_snapshot_all_roles_and_word_budget(self):
        snapshot = make_valid_snapshot()
        f_path = self._write_file("snapshot.json", json.dumps(snapshot))

        roles = [
            ("implementer", "Implementer", "Implement scoped changes strictly within assigned allowed paths"),
            ("coordinator", "Coordinator", "Check revision-bound evidence, checkout provenance"),
            ("reviewer", "Reviewer", "Inspect revision-bound code diff and verified CI evidence"),
        ]

        for role_arg, expected_title, expected_step_snippet in roles:
            stdout = io.StringIO()
            with patch("sys.stdout", stdout):
                ret = preview_role_handoff.main(["--snapshot-file", str(f_path), "--role", role_arg])
            self.assertEqual(ret, 0)
            output = stdout.getvalue()

            # Word count budget
            words = output.split()
            self.assertLessEqual(len(words), 180, f"Role {role_arg} exceeded 180-word budget ({len(words)} words)")

            # Required headers and pointers
            self.assertIn("Role Handoff Preview (Historical snapshot, unverified-current):", output)
            self.assertIn("https://github.com/Tiflit/DXVK-Companion/issues/85", output)
            self.assertIn("https://github.com/Tiflit/DXVK-Companion/pull/86", output)
            self.assertIn(f"Role: {expected_title}", output)
            self.assertIn("c700fcd16091e946246694565d1a5f55fdd3ad1e", output)
            self.assertIn("3c231c1ab67c2b94b7aef76c536e8ca83a5810f3", output)
            self.assertIn("91d72bc015559b2268abb7b9b3a033c9f63bad05", output)
            self.assertIn("Prerequisites:", output)
            self.assertIn("Authority: This preview grants no push, execution, publication, approval, or merge authority.", output)
            self.assertIn(expected_step_snippet, output)
            self.assertIn("Stop Boundary:", output)

    def test_case_insensitive_roles(self):
        snapshot = make_valid_snapshot()
        f_path = self._write_file("snapshot.json", json.dumps(snapshot))

        for role_arg in ["IMPLEMENTER", "Coordinator", "REVIEWER"]:
            stdout = io.StringIO()
            with patch("sys.stdout", stdout):
                ret = preview_role_handoff.main(["--snapshot-file", str(f_path), "--role", role_arg])
            self.assertEqual(ret, 0)
            self.assertIn("Role Handoff Preview", stdout.getvalue())

    def test_utf8_bom_compatibility(self):
        snapshot = make_valid_snapshot()
        bom_bytes = b"\xef\xbb\xbf" + json.dumps(snapshot).encode("utf-8")
        f_path = self._write_bytes("snapshot_bom.json", bom_bytes)

        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            ret = preview_role_handoff.main(["--snapshot-file", str(f_path), "--role", "implementer"])
        self.assertEqual(ret, 0)
        self.assertIn("Role Handoff Preview", stdout.getvalue())

    def test_closed_and_merged_task_behavior(self):
        # 1. PR merged
        s_merged = make_valid_snapshot()
        s_merged["pr_merged"] = True
        f_merged = self._write_file("snapshot_merged.json", json.dumps(s_merged))

        stdout1 = io.StringIO()
        with patch("sys.stdout", stdout1):
            ret1 = preview_role_handoff.main(["--snapshot-file", str(f_merged), "--role", "implementer"])
        self.assertEqual(ret1, 0)
        out1 = stdout1.getvalue()
        self.assertIn("(MERGED)", out1)
        self.assertIn("Task is closed or merged (inactive).", out1)
        self.assertIn("never restart implementation", out1)
        self.assertLessEqual(len(out1.split()), 180)

        # 2. PR closed
        s_closed_pr = make_valid_snapshot()
        s_closed_pr["pr_state"] = "CLOSED"
        f_closed_pr = self._write_file("snapshot_closed_pr.json", json.dumps(s_closed_pr))

        stdout2 = io.StringIO()
        with patch("sys.stdout", stdout2):
            ret2 = preview_role_handoff.main(["--snapshot-file", str(f_closed_pr), "--role", "coordinator"])
        self.assertEqual(ret2, 0)
        out2 = stdout2.getvalue()
        self.assertIn("Task is closed or merged (inactive).", out2)
        self.assertIn("never restart implementation", out2)
        self.assertLessEqual(len(out2.split()), 180)

        # 3. Issue closed
        s_closed_issue = make_valid_snapshot()
        s_closed_issue["primary_issue_state"] = "CLOSED"
        f_closed_issue = self._write_file("snapshot_closed_issue.json", json.dumps(s_closed_issue))

        stdout3 = io.StringIO()
        with patch("sys.stdout", stdout3):
            ret3 = preview_role_handoff.main(["--snapshot-file", str(f_closed_issue), "--role", "reviewer"])
        self.assertEqual(ret3, 0)
        out3 = stdout3.getvalue()
        self.assertIn("Task is closed or merged (inactive).", out3)
        self.assertIn("never restart implementation", out3)
        self.assertLessEqual(len(out3.split()), 180)

    def test_missing_and_wrong_type_fields(self):
        base = make_valid_snapshot()

        invalid_cases = [
            ("root not dict", json.dumps(["not", "a", "dict"])),
            ("missing repo", json.dumps({k: v for k, v in base.items() if k != "repo"})),
            ("repo not str", json.dumps({**base, "repo": 123})),
            ("missing pr_number", json.dumps({k: v for k, v in base.items() if k != "pr_number"})),
            ("pr_number string", json.dumps({**base, "pr_number": "86"})),
            ("pr_number bool", json.dumps({**base, "pr_number": True})),
            ("pr_number zero", json.dumps({**base, "pr_number": 0})),
            ("pr_number negative", json.dumps({**base, "pr_number": -5})),
            ("missing issue_number", json.dumps({k: v for k, v in base.items() if k != "primary_issue_number"})),
            ("issue_number string", json.dumps({**base, "primary_issue_number": "85"})),
            ("issue_number zero", json.dumps({**base, "primary_issue_number": 0})),
            ("missing head_sha", json.dumps({k: v for k, v in base.items() if k != "live_pr_head_sha"})),
            ("head_sha short", json.dumps({**base, "live_pr_head_sha": "c700fcd"})),
            ("head_sha non-hex", json.dumps({**base, "live_pr_head_sha": "c700fcd16091e946246694565d1a5f55fdd3ad1z"})),
            ("missing base_sha", json.dumps({k: v for k, v in base.items() if k != "live_pr_base_sha"})),
            ("missing default_sha", json.dumps({k: v for k, v in base.items() if k != "live_default_branch_sha"})),
            ("missing pr_state", json.dumps({k: v for k, v in base.items() if k != "pr_state"})),
            ("empty pr_state", json.dumps({**base, "pr_state": "   "})),
            ("missing pr_merged", json.dumps({k: v for k, v in base.items() if k != "pr_merged"})),
            ("pr_merged not bool", json.dumps({**base, "pr_merged": "false"})),
            ("missing issue_state", json.dumps({k: v for k, v in base.items() if k != "primary_issue_state"})),
            ("empty issue_state", json.dumps({**base, "primary_issue_state": ""})),
        ]

        for desc, content in invalid_cases:
            f_path = self._write_file(f"bad_{desc.replace(' ', '_')}.json", content)
            stderr = io.StringIO()
            with patch("sys.stderr", stderr):
                ret = preview_role_handoff.main(["--snapshot-file", str(f_path), "--role", "implementer"])
            self.assertEqual(ret, 1, f"Case '{desc}' should have exited with code 1")
            self.assertIn("ERROR: Snapshot schema validation failed", stderr.getvalue())

    def test_invalid_repo_and_sha_formats(self):
        base = make_valid_snapshot()

        bad_repos = [
            "no_slash_repo",
            "too/many/slashes/repo",
            "repo/with spaces",
            "../traversal/repo",
        ]
        for bad_repo in bad_repos:
            f_path = self._write_file("bad_repo.json", json.dumps({**base, "repo": bad_repo}))
            stderr = io.StringIO()
            with patch("sys.stderr", stderr):
                ret = preview_role_handoff.main(["--snapshot-file", str(f_path), "--role", "implementer"])
            self.assertEqual(ret, 1)
            self.assertIn("Snapshot schema validation failed", stderr.getvalue())

    def test_stale_or_moved_identities(self):
        snapshot = make_valid_snapshot()
        snapshot["base_has_moved"] = True
        snapshot["base_sync_status"] = "diverged"
        f_path = self._write_file("snapshot_diverged.json", json.dumps(snapshot))

        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            ret = preview_role_handoff.main(["--snapshot-file", str(f_path), "--role", "coordinator"])
        self.assertEqual(ret, 0)
        out = stdout.getvalue()
        self.assertIn("Role Handoff Preview (Historical snapshot, unverified-current):", out)

    def test_missing_or_empty_ci_evidence(self):
        snapshot = make_valid_snapshot()
        snapshot["ci_evidence"] = None
        f_path = self._write_file("snapshot_no_ci.json", json.dumps(snapshot))

        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            ret = preview_role_handoff.main(["--snapshot-file", str(f_path), "--role", "implementer"])
        self.assertEqual(ret, 0)
        self.assertIn("Role Handoff Preview", stdout.getvalue())

    def test_malicious_content_injection_defense(self):
        snapshot = make_valid_snapshot()
        evil_tokens = [
            "EVIL_PROMPT_INJECTION_TITLE_PAYLOAD",
            "EVIL_DECISION_APPROVAL_BYPASS",
            "EVIL_NEXT_OWNERSHIP_HIJACK",
            "EVIL_REVIEW_RECORD_INJECTION",
            "EVIL_UNCERTAINTIES_PAYLOAD",
        ]
        snapshot["pr_title"] = f"Fixes {evil_tokens[0]}"
        snapshot["primary_issue_title"] = f"Task {evil_tokens[0]}"
        snapshot["decision_governance"] = {
            "required": "decision",
            "proposed_option": "opt",
            "status": f"DECIDED {evil_tokens[1]}",
            "approval_source": f"Human Approved {evil_tokens[1]}",
        }
        snapshot["next_ownership"] = {
            "owner": evil_tokens[2],
            "action": f"Delete everything {evil_tokens[2]}",
        }
        snapshot["review_records"] = [
            {"record_id": "1", "reviewer": evil_tokens[3], "result": "PASS"}
        ]
        snapshot["uncertainties"] = [evil_tokens[4]]

        f_path = self._write_file("snapshot_injected.json", json.dumps(snapshot))

        stdout = io.StringIO()
        stderr = io.StringIO()
        with patch("sys.stdout", stdout), patch("sys.stderr", stderr):
            ret = preview_role_handoff.main(["--snapshot-file", str(f_path), "--role", "implementer"])
        self.assertEqual(ret, 0)
        combined = stdout.getvalue() + stderr.getvalue()

        for token in evil_tokens:
            self.assertNotIn(token, combined, f"Malicious token '{token}' leaked into preview output!")

    def test_argparse_sanitization_and_no_flag_abbreviation(self):
        # 1. Unknown flag
        stderr1 = io.StringIO()
        with patch("sys.stderr", stderr1):
            with self.assertRaises(SystemExit) as cm:
                preview_role_handoff.main(["--unknown-injected-flag-foo"])
            self.assertEqual(cm.exception.code, 2)
        err1 = stderr1.getvalue()
        self.assertIn("ERROR: Invalid command-line arguments.", err1)
        self.assertNotIn("--unknown-injected-flag-foo", err1)

        # 2. Flag abbreviation rejected (allow_abbrev=False)
        stderr2 = io.StringIO()
        with patch("sys.stderr", stderr2):
            with self.assertRaises(SystemExit) as cm2:
                preview_role_handoff.main(["--snap", "snapshot.json"])
            self.assertEqual(cm2.exception.code, 2)
        self.assertIn("ERROR: Invalid command-line arguments.", stderr2.getvalue())

        # 3. Missing required arguments
        stderr3 = io.StringIO()
        with patch("sys.stderr", stderr3):
            with self.assertRaises(SystemExit) as cm3:
                preview_role_handoff.main(["--role", "implementer"])
            self.assertEqual(cm3.exception.code, 2)
        self.assertIn("ERROR: Invalid command-line arguments.", stderr3.getvalue())

        # 4. --help exits 0 and prints sanitized usage
        stdout4 = io.StringIO()
        with patch("sys.stdout", stdout4):
            with self.assertRaises(SystemExit) as cm4:
                preview_role_handoff.main(["--help"])
            self.assertEqual(cm4.exception.code, 0)
        self.assertIn("preview_role_handoff.py", stdout4.getvalue())

    def test_unreadable_or_missing_snapshot_file(self):
        # Nonexistent file
        stderr1 = io.StringIO()
        with patch("sys.stderr", stderr1):
            ret1 = preview_role_handoff.main(["--snapshot-file", str(self.dir_path / "nonexistent.json"), "--role", "implementer"])
        self.assertEqual(ret1, 1)
        self.assertIn("Input --snapshot-file does not exist", stderr1.getvalue())

        # Directory passed as file
        stderr2 = io.StringIO()
        with patch("sys.stderr", stderr2):
            ret2 = preview_role_handoff.main(["--snapshot-file", str(self.dir_path), "--role", "implementer"])
        self.assertEqual(ret2, 1)
        self.assertIn("is not a regular file", stderr2.getvalue())

        # Malformed JSON syntax
        bad_json_path = self._write_file("bad_syntax.json", "NOT JSON {{{")
        stderr3 = io.StringIO()
        with patch("sys.stderr", stderr3):
            ret3 = preview_role_handoff.main(["--snapshot-file", str(bad_json_path), "--role", "implementer"])
        self.assertEqual(ret3, 1)
        self.assertIn("does not contain valid JSON syntax", stderr3.getvalue())

    def test_in_process_guards_against_writes_network_and_subprocesses(self):
        snapshot = make_valid_snapshot()
        f_path = self._write_file("snapshot.json", json.dumps(snapshot))

        def guarded_open(file, mode="r", *args, **kwargs):
            if any(m in mode for m in ("w", "a", "+", "x")):
                raise PermissionError("Write operation forbidden in read-only preview.")
            return original_open(file, mode, *args, **kwargs)

        original_open = builtins.open

        with patch("builtins.open", side_effect=guarded_open), \
             patch("pathlib.Path.write_text", side_effect=PermissionError("write_text forbidden")), \
             patch("pathlib.Path.write_bytes", side_effect=PermissionError("write_bytes forbidden")), \
             patch("os.remove", side_effect=PermissionError("os.remove forbidden")), \
             patch("os.unlink", side_effect=PermissionError("os.unlink forbidden")), \
             patch("urllib.request.urlopen") as mock_urlopen, \
             patch("subprocess.run") as mock_subproc_run, \
             patch("subprocess.Popen") as mock_subproc_popen:

            stdout = io.StringIO()
            with patch("sys.stdout", stdout):
                ret = preview_role_handoff.main(["--snapshot-file", str(f_path), "--role", "implementer"])
            self.assertEqual(ret, 0)
            mock_urlopen.assert_not_called()
            mock_subproc_run.assert_not_called()
            mock_subproc_popen.assert_not_called()

    def test_cloud_subprocess_startup_bytecode_and_hash_invariance(self):
        """Cloud-only subprocess test verifying CLI execution in a clean temp tree with zero mutations."""
        with tempfile.TemporaryDirectory() as clean_worktree:
            clean_path = Path(clean_worktree)
            scripts_dir = clean_path / "scripts" / "ai-workflow"
            scripts_dir.mkdir(parents=True, exist_ok=True)

            src_script = REPO_ROOT / "scripts" / "ai-workflow" / "preview_role_handoff.py"
            dest_script = scripts_dir / "preview_role_handoff.py"
            dest_script.write_text(src_script.read_text(encoding="utf-8"), encoding="utf-8")

            # Create all fixtures BEFORE establishing baseline
            snapshot = make_valid_snapshot()
            snap_file = clean_path / "snapshot.json"
            snap_file.write_text(json.dumps(snapshot), encoding="utf-8")

            bad_snap = {**snapshot, "pr_number": -1}
            bad_snap_file = clean_path / "bad_snapshot.json"
            bad_snap_file.write_text(json.dumps(bad_snap), encoding="utf-8")

            # Compute complete recursive directory hash baseline with all fixtures present
            baseline_hashes = compute_dir_hashes(clean_path)

            env = os.environ.copy()
            # Do NOT set PYTHONDONTWRITEBYTECODE externally; test the script's internal sys.dont_write_bytecode
            env.pop("PYTHONDONTWRITEBYTECODE", None)

            # 1. Successful execution
            cmd_pass = [
                sys.executable,
                str(dest_script),
                "--snapshot-file", str(snap_file),
                "--role", "implementer",
            ]
            res_pass = subprocess.run(cmd_pass, capture_output=True, text=True, env=env)
            self.assertEqual(res_pass.returncode, 0)
            self.assertIn("Role Handoff Preview", res_pass.stdout)
            self.assertEqual(
                compute_dir_hashes(clean_path),
                baseline_hashes,
                "Tree state modified during successful preview execution.",
            )

            # 2. Schema validation failure execution
            cmd_fail = [
                sys.executable,
                str(dest_script),
                "--snapshot-file", str(bad_snap_file),
                "--role", "implementer",
            ]
            res_fail = subprocess.run(cmd_fail, capture_output=True, text=True, env=env)
            self.assertEqual(res_fail.returncode, 1)
            self.assertIn("Snapshot schema validation failed", res_fail.stderr)
            self.assertEqual(
                compute_dir_hashes(clean_path),
                baseline_hashes,
                "Tree state modified during validation failure execution.",
            )

            # 3. Argument error execution
            cmd_arg_err = [
                sys.executable,
                str(dest_script),
                "--invalid-flag-attempt",
            ]
            res_arg = subprocess.run(cmd_arg_err, capture_output=True, text=True, env=env)
            self.assertEqual(res_arg.returncode, 2)
            self.assertIn("ERROR: Invalid command-line arguments.", res_arg.stderr)
            self.assertEqual(
                compute_dir_hashes(clean_path),
                baseline_hashes,
                "Tree state modified during argument error execution.",
            )

            # Assert NO __pycache__ directory was ever created
            pycache_dirs = list(clean_path.rglob("__pycache__"))
            self.assertEqual(pycache_dirs, [], "Bytecode cache directory __pycache__ must not be created.")

            # Assert NO .pyc files exist
            pyc_files = list(clean_path.rglob("*.pyc"))
            self.assertEqual(pyc_files, [], "Compiled bytecode files (*.pyc) must not be created.")


if __name__ == "__main__":
    unittest.main()
