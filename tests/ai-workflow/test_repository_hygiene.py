"""Tests covering repository hygiene, targeted .gitignore exclusions, worktree boundaries, and privacy protection."""

from __future__ import annotations

import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import update_pr_body
from update_pr_body import (
    SecurityValidationError,
    anchor_and_resolve_backup_path,
    create_prewrite_backup,
    find_git_worktree_roots,
    is_inside_git_repository,
    is_path_inside_or_equal,
    validate_backup_dir,
)


class MockHttpResponse:
    """Mock HTTP response object."""

    def __init__(self, data, status: int = 200):
        self.status = status
        if isinstance(data, (dict, list)):
            self._bytes = json.dumps(data).encode("utf-8")
        elif isinstance(data, str):
            self._bytes = data.encode("utf-8")
        elif isinstance(data, bytes):
            self._bytes = data
        else:
            self._bytes = b""

    def read(self) -> bytes:
        return self._bytes

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        pass


class TestRepositoryHygiene(unittest.TestCase):
    """Tests covering .gitignore exclusions, synthetic worktrees, alias resolution, and backup isolation."""

    @classmethod
    def setUpClass(cls):
        cls.repo_gitignore = REPO_ROOT / ".gitignore"
        if not cls.repo_gitignore.is_file():
            raise unittest.SkipTest(".gitignore is not present in repository root")
        cls.gitignore_content = cls.repo_gitignore.read_text(encoding="utf-8")

    def _check_ignore_in_synthetic_repo(self, repo_dir: Path, target_path: str) -> bool:
        """
        Runs 'git check-ignore -q <target_path>' inside repo_dir.
        Returns True if the target path is ignored by Git, False otherwise.
        """
        proc = subprocess.run(
            ["git", "check-ignore", "-q", target_path],
            cwd=repo_dir,
            capture_output=True,
            text=True,
        )
        return proc.returncode == 0

    def test_gitignore_excludes_targeted_ai_review_backups_and_cache(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            repo_dir = Path(tmp_dir) / "repo"
            repo_dir.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_dir, check=True)
            (repo_dir / ".gitignore").write_text(self.gitignore_content, encoding="utf-8")

            # .ai-review-backups directory and files inside
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, ".ai-review-backups/"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, ".ai-review-backups/pr_31_body_backup.md"))

            # .ai-cache directory and files inside
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, ".ai-cache/"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, ".ai-cache/cache.bin"))

    def test_gitignore_excludes_build_artifacts_and_test_results(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            repo_dir = Path(tmp_dir) / "repo"
            repo_dir.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_dir, check=True)
            (repo_dir / ".gitignore").write_text(self.gitignore_content, encoding="utf-8")

            # Root-level build artifacts
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "bin/"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "obj/"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "TestResults/"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "artifacts/"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "artifacts/provenance/build-provenance.json"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "artifacts/publish/DXVKCompanion.exe"))

            # Subdirectory build artifacts (.NET style)
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "src/Project/bin/Debug/net8.0/app.dll"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "src/Project/obj/Release/net8.0/app.pdb"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "tests/TestResults/test_run.trx"))

    def test_gitignore_excludes_python_bytecode_and_cache(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            repo_dir = Path(tmp_dir) / "repo"
            repo_dir.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_dir, check=True)
            (repo_dir / ".gitignore").write_text(self.gitignore_content, encoding="utf-8")

            # Python bytecode and __pycache__
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "__pycache__/"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "scripts/__pycache__/helper.cpython-311.pyc"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "module.pyc"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "module.pyo"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, "module.pyd"))

    def test_gitignore_excludes_local_env_files_but_allows_env_example(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            repo_dir = Path(tmp_dir) / "repo"
            repo_dir.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_dir, check=True)
            (repo_dir / ".gitignore").write_text(self.gitignore_content, encoding="utf-8")

            # Local env secret files are ignored
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, ".env"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, ".env.local"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, ".env.development"))
            self.assertTrue(self._check_ignore_in_synthetic_repo(repo_dir, ".env.production"))

            # Whitelisted .env.example remains tracked and versionable
            self.assertFalse(self._check_ignore_in_synthetic_repo(repo_dir, ".env.example"))

    def test_gitignore_does_not_blanket_ignore_source_or_fixtures(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            repo_dir = Path(tmp_dir) / "repo"
            repo_dir.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_dir, check=True)
            (repo_dir / ".gitignore").write_text(self.gitignore_content, encoding="utf-8")

            # Valid source code
            self.assertFalse(self._check_ignore_in_synthetic_repo(repo_dir, "src/Program.cs"))
            self.assertFalse(self._check_ignore_in_synthetic_repo(repo_dir, "scripts/ai-workflow/update_pr_body.py"))

            # JSON, ZIP, DLL, PNG test fixtures and docs are NOT blanket-ignored
            self.assertFalse(self._check_ignore_in_synthetic_repo(repo_dir, "tests/fixtures/sample_config.json"))
            self.assertFalse(self._check_ignore_in_synthetic_repo(repo_dir, "tests/fixtures/bundle_archive.zip"))
            self.assertFalse(self._check_ignore_in_synthetic_repo(repo_dir, "tests/fixtures/native_mock.dll"))
            self.assertFalse(self._check_ignore_in_synthetic_repo(repo_dir, "docs/images/architecture_flow.png"))

    def test_synthetic_linked_worktree_boundary_discovery_and_rejection(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            base_dir = Path(tmp_dir)
            main_repo = base_dir / "main_repo"
            main_repo.mkdir()

            # Initialize main repo with git
            subprocess.run(["git", "init", "-q"], cwd=main_repo, check=True)
            subprocess.run(["git", "config", "user.email", "test@example.com"], cwd=main_repo, check=True)
            subprocess.run(["git", "config", "user.name", "Test User"], cwd=main_repo, check=True)
            (main_repo / "README.md").write_text("# Main\n", encoding="utf-8")
            subprocess.run(["git", "add", "README.md"], cwd=main_repo, check=True)
            subprocess.run(["git", "commit", "-q", "-m", "Initial commit"], cwd=main_repo, check=True)

            # Create linked worktree
            linked_wt = base_dir / "linked_worktree"
            subprocess.run(["git", "worktree", "add", "-q", str(linked_wt), "-b", "feature-test"], cwd=main_repo, check=True)

            # Discover worktree roots from both main and linked worktree
            main_roots = find_git_worktree_roots(cwd=main_repo)
            linked_roots = find_git_worktree_roots(cwd=linked_wt)

            self.assertGreaterEqual(len(main_roots), 2)
            self.assertGreaterEqual(len(linked_roots), 2)

            # Verify backup destination inside main worktree is rejected from both
            with self.assertRaises(SecurityValidationError) as cm1:
                validate_backup_dir(main_repo / "backup_dir", cwd=main_repo)
            self.assertIn("strictly outside all Git checkouts and worktrees", str(cm1.exception))

            with self.assertRaises(SecurityValidationError) as cm2:
                validate_backup_dir(main_repo / "backup_dir", cwd=linked_wt)
            self.assertIn("strictly outside all Git checkouts and worktrees", str(cm2.exception))

            # Verify backup destination inside linked worktree is rejected from both
            with self.assertRaises(SecurityValidationError) as cm3:
                validate_backup_dir(linked_wt / "backup_dir", cwd=main_repo)
            self.assertIn("strictly outside all Git checkouts and worktrees", str(cm3.exception))

            with self.assertRaises(SecurityValidationError) as cm4:
                validate_backup_dir(linked_wt / "backup_dir", cwd=linked_wt)
            self.assertIn("strictly outside all Git checkouts and worktrees", str(cm4.exception))

            # Verify external directory outside both worktrees succeeds
            safe_ext = base_dir / "safe_external_backups"
            validated = validate_backup_dir(safe_ext, cwd=main_repo)
            self.assertEqual(validated, base_dir.resolve() / "safe_external_backups")

            validated_linked = validate_backup_dir(safe_ext, cwd=linked_wt)
            self.assertEqual(validated_linked, base_dir.resolve() / "safe_external_backups")

    def test_alias_resolution_detects_relative_and_case_variants(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            repo_dir = Path(tmp_dir) / "test_repo"
            repo_dir.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_dir, check=True)

            # Relative path inside repo
            rel_path = Path("backups")
            with self.assertRaises(SecurityValidationError):
                validate_backup_dir(rel_path, cwd=repo_dir)

            # Parent-relative alias navigating inside repo
            sub_dir = repo_dir / "subdir"
            sub_dir.mkdir()
            dotdot_path = sub_dir / ".." / "external_name_actually_in_repo"
            with self.assertRaises(SecurityValidationError):
                validate_backup_dir(dotdot_path, cwd=repo_dir)

    def test_two_independent_synthetic_repos_rejects_cross_repo_destinations(self):
        """
        R1: Refuses destinations inside any locally establishable Git repository,
        including independent repositories, nested directories, and non-existent destination suffixes.
        """
        with tempfile.TemporaryDirectory() as tmp_dir:
            base_dir = Path(tmp_dir)
            repo_a = base_dir / "repo_a"
            repo_b = base_dir / "repo_b"
            safe_ext = base_dir / "safe_external_backups"

            repo_a.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_a, check=True)

            repo_b.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_b, check=True)
            nested_b = repo_b / "nested" / "existing_dir"
            nested_b.mkdir(parents=True)

            # From repo_a, attempt backup inside repo_b root
            with self.assertRaises(SecurityValidationError) as cm1:
                validate_backup_dir(repo_b, cwd=repo_a)
            self.assertIn("strictly outside all Git checkouts and worktrees", str(cm1.exception))

            # From repo_a, attempt backup inside repo_b nested existing dir
            with self.assertRaises(SecurityValidationError) as cm2:
                validate_backup_dir(nested_b, cwd=repo_a)
            self.assertIn("strictly outside all Git checkouts and worktrees", str(cm2.exception))

            # From repo_a, attempt backup inside repo_b nested non-existent suffix
            non_existent_suffix = nested_b / "non_existent_child"
            with self.assertRaises(SecurityValidationError) as cm3:
                validate_backup_dir(non_existent_suffix, cwd=repo_a)
            self.assertIn("strictly outside all Git checkouts and worktrees", str(cm3.exception))

            # From repo_a, attempt backup inside repo_b .git directory
            with self.assertRaises(SecurityValidationError) as cm4:
                validate_backup_dir(repo_b / ".git", cwd=repo_a)
            self.assertIn("strictly outside all Git checkouts and worktrees", str(cm4.exception))

            # From repo_b, attempt backup inside repo_a
            with self.assertRaises(SecurityValidationError) as cm5:
                validate_backup_dir(repo_a, cwd=repo_b)
            self.assertIn("strictly outside all Git checkouts and worktrees", str(cm5.exception))

            # External destination strictly outside both repos succeeds
            validated = validate_backup_dir(safe_ext, cwd=repo_a)
            self.assertEqual(validated, base_dir.resolve() / "safe_external_backups")

            validated_b = validate_backup_dir(safe_ext, cwd=repo_b)
            self.assertEqual(validated_b, base_dir.resolve() / "safe_external_backups")

    def test_relative_backup_dir_anchored_against_cwd_parameter_not_process_cwd(self):
        """
        R2: Explicitly anchors relative paths against cwd parameter, returning
        the validated absolute Path for downstream operations.
        """
        with tempfile.TemporaryDirectory() as tmp_dir:
            base_dir = Path(tmp_dir)
            repo_a = base_dir / "repo_a"
            repo_a.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_a, check=True)

            safe_external = base_dir / "safe_backups"

            # Relative destination '../safe_backups' relative to repo_a
            rel_ext = Path("../safe_backups")

            # Validate with cwd=repo_a
            validated = validate_backup_dir(rel_ext, cwd=repo_a)
            expected_abs = base_dir.resolve() / "safe_backups"
            self.assertEqual(validated, expected_abs)

            # Prewrite backup creation uses anchored path
            backup_file = create_prewrite_backup("pr", 31, "test body", backup_dir=rel_ext, cwd=repo_a)
            self.assertTrue(backup_file.is_file())
            self.assertEqual(backup_file.parent, expected_abs)

    def test_symlink_loop_and_resolution_failure_fails_closed(self):
        """
        R2: Ancestor resolution fails closed on symlink loops or broken resolutions.
        """
        if not hasattr(os, "symlink"):
            raise unittest.SkipTest("Symlinks not supported on this platform")

        with tempfile.TemporaryDirectory() as tmp_dir:
            base_dir = Path(tmp_dir)
            repo_a = base_dir / "repo_a"
            repo_a.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_a, check=True)

            link_a = base_dir / "loop_a"
            link_b = base_dir / "loop_b"
            try:
                os.symlink(str(link_b), str(link_a))
                os.symlink(str(link_a), str(link_b))
            except (OSError, NotImplementedError):
                raise unittest.SkipTest("Symlink creation requires elevated privileges or is unsupported")

            with self.assertRaises(SecurityValidationError) as cm:
                validate_backup_dir(link_a, cwd=repo_a)
            self.assertIn("Backup destination path resolution failed.", str(cm.exception))

            with self.assertRaises(SecurityValidationError) as cm2:
                validate_backup_dir(link_a / "nested_suffix", cwd=repo_a)
            self.assertIn("Backup destination path resolution failed.", str(cm2.exception))

    def test_git_worktree_porcelain_parsing_preserves_spaces_losslessly(self):
        """
        R2: Preserves path characters losslessly when parsing 'git worktree list --porcelain'.
        """
        wt_with_spaces = Path("/repo/path with spaces/worktree ")
        mock_output = f"worktree {wt_with_spaces}\nHEAD 1234\nbranch refs/heads/main\n"

        with patch("subprocess.run") as mock_run:
            mock_proc = MagicMock()
            mock_proc.returncode = 0
            mock_proc.stdout = mock_output
            mock_run.return_value = mock_proc

            roots = find_git_worktree_roots(cwd=Path.cwd())
            self.assertIn(wt_with_spaces, roots)

    def test_is_inside_git_repository_distinguishes_absence_from_query_failure(self):
        """
        R1: Distinguishes established non-repository absence (no markers in ancestry + 128 + 'not a git repository')
        from broken repository markers or query failures/timeouts which must fail closed.
        """
        with tempfile.TemporaryDirectory() as tmp_dir:
            clean_external = Path(tmp_dir) / "clean_external"
            clean_external.mkdir()

            # Absent repository: no markers in ancestry, code 128 + 'not a git repository' -> returns False
            with patch("subprocess.run") as mock_run:
                mock_proc = MagicMock()
                mock_proc.returncode = 128
                mock_proc.stdout = ""
                mock_proc.stderr = "fatal: not a git repository (or any of the parent directories): .git"
                mock_run.return_value = mock_proc

                result = is_inside_git_repository(clean_external)
                self.assertFalse(result)

            # Broken repository marker: .git file exists, but git exits 128 -> fails closed
            broken_git = clean_external / ".git"
            broken_git.write_text("gitdir: /nonexistent/gitdir\n", encoding="utf-8")
            with patch("subprocess.run") as mock_run:
                mock_proc = MagicMock()
                mock_proc.returncode = 128
                mock_proc.stdout = ""
                mock_proc.stderr = "fatal: not a git repository: /nonexistent/gitdir"
                mock_run.return_value = mock_proc

                with self.assertRaises(SecurityValidationError) as cm:
                    is_inside_git_repository(clean_external)
                self.assertIn("Git worktree boundary could not be established.", str(cm.exception))

            # Query failure / permission: fails closed with category-only error
            with patch("subprocess.run") as mock_run:
                mock_proc = MagicMock()
                mock_proc.returncode = 1
                mock_proc.stdout = ""
                mock_proc.stderr = "fatal: error reading directory: permission denied"
                mock_run.return_value = mock_proc

                with self.assertRaises(SecurityValidationError) as cm:
                    is_inside_git_repository(clean_external)
                self.assertIn("Git worktree boundary could not be established.", str(cm.exception))

            # Query exception: fails closed with category-only error
            with patch("subprocess.run", side_effect=TimeoutError("git timed out")):
                with self.assertRaises(SecurityValidationError) as cm:
                    is_inside_git_repository(clean_external)
                self.assertIn("Git worktree boundary could not be established.", str(cm.exception))

        # Inside repository: code 0 + 'true' -> returns True
        with tempfile.TemporaryDirectory() as tmp_dir:
            repo_dir = Path(tmp_dir) / "valid_repo"
            repo_dir.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_dir, check=True)
            result = is_inside_git_repository(repo_dir)
            self.assertTrue(result)

    @patch("urllib.request.urlopen")
    def test_synthetic_broken_gitfile_pointer_fixture_refuses_backups_without_creation_or_patch(self, mock_urlopen):
        """
        R1: An owned synthetic checkout with a broken .git pointer (pointing to missing gitdir storage)
        is never mistaken for safe non-repository absence. Both PR and Issue CLI routes refuse with
        category-only errors, create zero backup directories/files, execute zero PATCH requests,
        and preserve synthetic metadata intact.
        """
        sample_pr_body = (
            "## Primary Issue\nFixes #31\n\n"
            "## Summary\nValid summary.\n\n"
            "## Scope\nscripts/ai-workflow/**\n\n"
            "## Verification\n92 passed.\n\n"
            "## Documentation\nUpdated.\n"
        )
        sample_issue_body = (
            "## Objective\nValid issue objective.\n\n"
            "## Assignment\nNext owner: Gemini.\n"
        )
        sample_append = (
            "<!-- AI-POST-MERGE-RECORD: gemini-20261019-issue42-test -->\n"
            "## Post-Merge Verification & Activity Record — Issue #42\n"
            "Verified on main. Result: PASS.\n"
            "<!-- AI-POST-MERGE-RECORD-END -->"
        )

        with tempfile.TemporaryDirectory() as tmp_dir:
            base_dir = Path(tmp_dir)
            repo_a = base_dir / "repo_a"
            repo_a.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_a, check=True)
            subprocess.run(["git", "config", "user.email", "test@example.com"], cwd=repo_a, check=True)
            subprocess.run(["git", "config", "user.name", "Test User"], cwd=repo_a, check=True)
            (repo_a / "README.md").write_text("# Repo A\n", encoding="utf-8")
            subprocess.run(["git", "add", "README.md"], cwd=repo_a, check=True)
            subprocess.run(["git", "commit", "-q", "-m", "Initial commit"], cwd=repo_a, check=True)

            # Synthetic repo_b with broken .git pointer file
            repo_b = base_dir / "repo_b"
            repo_b.mkdir()
            broken_pointer_content = "gitdir: /nonexistent/storage/gitdir\n"
            git_file = repo_b / ".git"
            git_file.write_text(broken_pointer_content, encoding="utf-8")

            # Candidate destination within repo_b
            target_backup_dir = repo_b / "sub" / "future_backups"

            # Unit-level assertions
            with self.assertRaises(SecurityValidationError) as cm_unit1:
                validate_backup_dir(repo_b, cwd=repo_a)
            self.assertIn("Git worktree boundary could not be established.", str(cm_unit1.exception))

            with self.assertRaises(SecurityValidationError) as cm_unit2:
                validate_backup_dir(target_backup_dir, cwd=repo_a)
            self.assertIn("Git worktree boundary could not be established.", str(cm_unit2.exception))

            # CLI-level route 1: PR mode
            pr_hash = update_pr_body.compute_body_sha256(sample_pr_body)
            mock_urlopen.return_value = MockHttpResponse({"number": 31, "body": sample_pr_body})

            pr_args = [
                "update_pr_body.py",
                "--pr", "31",
                "--body", sample_pr_body,
                "--expected-base-hash", pr_hash,
                "--backup-dir", str(target_backup_dir),
                "--write",
                "--token", "tok",
            ]
            with patch.object(sys, "argv", pr_args), \
                 patch("sys.stdout", new_callable=io.StringIO) as mock_out, \
                 patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 1)
                err = mock_err.getvalue()
                self.assertIn("Git worktree boundary could not be established.", err)
                self.assertNotIn("Traceback", err)

            # Assert no backup directory or files created
            self.assertFalse((repo_b / "sub").exists())
            self.assertEqual(list(repo_b.glob("**/*.md")), [])

            # Assert zero PATCH requests
            for call in mock_urlopen.call_args_list:
                req = call[0][0]
                method = req.get_method() if hasattr(req, "get_method") else "GET"
                self.assertNotEqual(method, "PATCH")

            # Assert synthetic metadata preserved intact
            self.assertEqual(git_file.read_text(encoding="utf-8"), broken_pointer_content)

            # CLI-level route 2: Issue mode
            mock_urlopen.reset_mock()
            issue_hash = update_pr_body.compute_body_sha256(sample_issue_body)
            mock_urlopen.return_value = MockHttpResponse({"number": 42, "body": sample_issue_body})

            append_file = base_dir / "append.md"
            append_file.write_text(sample_append, encoding="utf-8")

            issue_args = [
                "update_pr_body.py",
                "--issue", "42",
                "--append-file", str(append_file),
                "--expected-base-hash", issue_hash,
                "--backup-dir", str(target_backup_dir),
                "--write",
                "--token", "tok",
            ]
            with patch.object(sys, "argv", issue_args), \
                 patch("sys.stdout", new_callable=io.StringIO) as mock_out, \
                 patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 1)
                err = mock_err.getvalue()
                self.assertIn("Git worktree boundary could not be established.", err)
                self.assertNotIn("Traceback", err)

            # Assert no backup directory or files created
            self.assertFalse((repo_b / "sub").exists())
            self.assertEqual(list(repo_b.glob("**/*.md")), [])

            # Assert zero PATCH requests
            for call in mock_urlopen.call_args_list:
                req = call[0][0]
                method = req.get_method() if hasattr(req, "get_method") else "GET"
                self.assertNotEqual(method, "PATCH")

            # Assert synthetic metadata preserved intact
            self.assertEqual(git_file.read_text(encoding="utf-8"), broken_pointer_content)

    @patch("urllib.request.urlopen")
    def test_synthetic_corrupted_git_directory_fixture_refuses_backups_without_creation_or_patch(self, mock_urlopen):
        """
        R1: An owned synthetic checkout with a corrupted .git directory (e.g. invalid HEAD / metadata)
        is never mistaken for safe non-repository absence. Both PR and Issue CLI routes refuse with
        category-only errors, create zero backup directories/files, execute zero PATCH requests,
        and preserve synthetic metadata intact.
        """
        sample_pr_body = (
            "## Primary Issue\nFixes #31\n\n"
            "## Summary\nValid summary.\n\n"
            "## Scope\nscripts/ai-workflow/**\n\n"
            "## Verification\n92 passed.\n\n"
            "## Documentation\nUpdated.\n"
        )
        sample_issue_body = (
            "## Objective\nValid issue objective.\n\n"
            "## Assignment\nNext owner: Gemini.\n"
        )
        sample_append = (
            "<!-- AI-POST-MERGE-RECORD: gemini-20261019-issue42-test2 -->\n"
            "## Post-Merge Verification & Activity Record — Issue #42\n"
            "Verified on main. Result: PASS.\n"
            "<!-- AI-POST-MERGE-RECORD-END -->"
        )

        with tempfile.TemporaryDirectory() as tmp_dir:
            base_dir = Path(tmp_dir)
            repo_a = base_dir / "repo_a"
            repo_a.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_a, check=True)
            subprocess.run(["git", "config", "user.email", "test@example.com"], cwd=repo_a, check=True)
            subprocess.run(["git", "config", "user.name", "Test User"], cwd=repo_a, check=True)
            (repo_a / "README.md").write_text("# Repo A\n", encoding="utf-8")
            subprocess.run(["git", "add", "README.md"], cwd=repo_a, check=True)
            subprocess.run(["git", "commit", "-q", "-m", "Initial commit"], cwd=repo_a, check=True)

            # Synthetic repo_c with corrupted .git directory
            repo_c = base_dir / "repo_c"
            repo_c.mkdir()
            corrupt_git_dir = repo_c / ".git"
            corrupt_git_dir.mkdir()
            head_content = "ref: refs/heads/nonexistent_branch_with_corrupt_objects\n"
            (corrupt_git_dir / "HEAD").write_text(head_content, encoding="utf-8")

            # Candidate destination within repo_c
            target_backup_dir = repo_c / "nested" / "future_backups"

            # Unit-level assertions
            with self.assertRaises(SecurityValidationError) as cm_unit1:
                validate_backup_dir(repo_c, cwd=repo_a)
            self.assertIn("Git worktree boundary could not be established.", str(cm_unit1.exception))

            with self.assertRaises(SecurityValidationError) as cm_unit2:
                validate_backup_dir(target_backup_dir, cwd=repo_a)
            self.assertIn("Git worktree boundary could not be established.", str(cm_unit2.exception))

            with self.assertRaises(SecurityValidationError) as cm_unit3:
                validate_backup_dir(corrupt_git_dir, cwd=repo_a)
            self.assertIn("Git worktree boundary could not be established.", str(cm_unit3.exception))

            # CLI-level route 1: PR mode
            pr_hash = update_pr_body.compute_body_sha256(sample_pr_body)
            mock_urlopen.return_value = MockHttpResponse({"number": 31, "body": sample_pr_body})

            pr_args = [
                "update_pr_body.py",
                "--pr", "31",
                "--body", sample_pr_body,
                "--expected-base-hash", pr_hash,
                "--backup-dir", str(target_backup_dir),
                "--write",
                "--token", "tok",
            ]
            with patch.object(sys, "argv", pr_args), \
                 patch("sys.stdout", new_callable=io.StringIO) as mock_out, \
                 patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 1)
                err = mock_err.getvalue()
                self.assertIn("Git worktree boundary could not be established.", err)
                self.assertNotIn("Traceback", err)

            # Assert no backup directory or files created
            self.assertFalse((repo_c / "nested").exists())
            self.assertEqual(list(repo_c.glob("**/*.md")), [])

            # Assert zero PATCH requests
            for call in mock_urlopen.call_args_list:
                req = call[0][0]
                method = req.get_method() if hasattr(req, "get_method") else "GET"
                self.assertNotEqual(method, "PATCH")

            # Assert synthetic metadata preserved intact
            self.assertEqual((corrupt_git_dir / "HEAD").read_text(encoding="utf-8"), head_content)

            # CLI-level route 2: Issue mode
            mock_urlopen.reset_mock()
            issue_hash = update_pr_body.compute_body_sha256(sample_issue_body)
            mock_urlopen.return_value = MockHttpResponse({"number": 42, "body": sample_issue_body})

            append_file = base_dir / "append.md"
            append_file.write_text(sample_append, encoding="utf-8")

            issue_args = [
                "update_pr_body.py",
                "--issue", "42",
                "--append-file", str(append_file),
                "--expected-base-hash", issue_hash,
                "--backup-dir", str(target_backup_dir),
                "--write",
                "--token", "tok",
            ]
            with patch.object(sys, "argv", issue_args), \
                 patch("sys.stdout", new_callable=io.StringIO) as mock_out, \
                 patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 1)
                err = mock_err.getvalue()
                self.assertIn("Git worktree boundary could not be established.", err)
                self.assertNotIn("Traceback", err)

            # Assert no backup directory or files created
            self.assertFalse((repo_c / "nested").exists())
            self.assertEqual(list(repo_c.glob("**/*.md")), [])

            # Assert zero PATCH requests
            for call in mock_urlopen.call_args_list:
                req = call[0][0]
                method = req.get_method() if hasattr(req, "get_method") else "GET"
                self.assertNotEqual(method, "PATCH")

            # Assert synthetic metadata preserved intact
            self.assertEqual((corrupt_git_dir / "HEAD").read_text(encoding="utf-8"), head_content)

    def test_genuinely_external_directory_without_repository_markers_succeeds(self):
        """
        R1: An external directory with no repository markers in any ancestor
        is verified as safe absence, accepted by validate_backup_dir, and successfully stores backups.
        """
        with tempfile.TemporaryDirectory() as tmp_dir:
            base_dir = Path(tmp_dir)
            repo_a = base_dir / "repo_a"
            repo_a.mkdir()
            subprocess.run(["git", "init", "-q"], cwd=repo_a, check=True)

            safe_external = base_dir / "safe_external_backups" / "nested"
            validated = validate_backup_dir(safe_external, cwd=repo_a)
            self.assertEqual(validated, base_dir.resolve() / "safe_external_backups" / "nested")

            backup_file = create_prewrite_backup("issue", 42, "test issue body", backup_dir=safe_external, cwd=repo_a)
            self.assertTrue(backup_file.is_file())
            self.assertEqual(backup_file.parent, validated)
            self.assertEqual(backup_file.read_text(encoding="utf-8"), "test issue body")


if __name__ == "__main__":
    unittest.main()
