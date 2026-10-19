"""Tests covering repository hygiene, targeted .gitignore exclusions, worktree boundaries, and privacy protection."""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import update_pr_body
from update_pr_body import (
    SecurityValidationError,
    create_prewrite_backup,
    find_git_worktree_roots,
    is_path_inside_or_equal,
    validate_backup_dir,
)


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
            self.assertEqual(validated, safe_ext)

            validated_linked = validate_backup_dir(safe_ext, cwd=linked_wt)
            self.assertEqual(validated_linked, safe_ext)

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


if __name__ == "__main__":
    unittest.main()
