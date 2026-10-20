"""Cloud-only synthetic repositories and mocked read boundaries; no owner data."""
from __future__ import annotations

from contextlib import redirect_stderr, redirect_stdout
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts" / "ai-workflow"
if str(SCRIPTS) not in sys.path:
    sys.path.insert(0, str(SCRIPTS))
import publication_preflight as pf


class PublicationPreflightTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = self.root / "repository"
        self.repo.mkdir()
        self.env = dict(os.environ)
        for key in list(self.env):
            if key.startswith("GIT_"):
                del self.env[key]
        self.env.update({
            "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull,
            "GIT_AUTHOR_NAME": "FixtureAgent", "GIT_COMMITTER_NAME": "FixtureAgent",
            "GIT_AUTHOR_EMAIL": "123+FixtureAgent@users.noreply.github.com",
            "GIT_COMMITTER_EMAIL": "123+FixtureAgent@users.noreply.github.com",
        })
        self.git("init", "-b", "main")
        self.write("README.md", "Generic project and public third-party attribution: Example Contributor.\n")
        self.git("add", "--", "README.md")
        self.git("commit", "-m", "Synthetic baseline")
        self.base = self.git("rev-parse", "HEAD").strip().decode("ascii")

    def git(self, *args, input=None, env=None):
        result = subprocess.run(
            ["git", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=", "-C", str(self.repo), *args],
            input=input, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            env=env or self.env, timeout=10, check=False,
        )
        if result.returncode:
            self.fail("Synthetic repository setup failed (details intentionally suppressed)")
        return result.stdout

    def write(self, name, text):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(text, bytes):
            path.write_bytes(text)
        else:
            path.write_text(text, encoding="utf-8")
        return path

    def check(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = pf.main(["--repo", str(self.repo), *map(str, args)])
        self.assertEqual(err.getvalue(), "")
        data = out.getvalue()
        self.assertLess(len(data), 16384)
        self.assertNotIn(str(self.root), data)
        return code, json.loads(data), data

    def terms(self, text="PrivateSentinel"):
        path = self.root / "private-terms.txt"
        path.write_text(text, encoding="utf-8")
        return path

    def metadata_only_reader(self):
        original = pf.read_regular
        def read(path, limit):
            self.assertTrue((self.repo / ".git") in Path(path).parents)
            return original(path, limit)
        return read

    def commit_file(self, text="safe generic text", message="Synthetic update", env=None):
        self.write("candidate.txt", text)
        self.git("add", "--", "candidate.txt")
        self.git("commit", "-m", message, env=env)

    def test_explicit_text_is_read_only_and_reports_payload_identity(self):
        text = self.root / "publication.md"
        text.write_text("Public attribution: Example Contributor; D:/dev/Project", encoding="utf-8")
        before = text.read_bytes()
        code, report, _ = self.check("--text-file", text)
        self.assertEqual(code, 0)
        self.assertTrue(report["complete"])
        self.assertEqual(report["files"], 1)
        self.assertEqual(report["commit_metadata"], "not_in_this_candidate")
        self.assertRegex(report["candidate_sha256"], r"^[0-9a-f]{64}$")
        self.assertEqual(text.read_bytes(), before)
        self.assertEqual(self.check("--text-file", text)[1]["candidate_sha256"], report["candidate_sha256"])

    def test_private_terms_are_literal_casefolded_substrings_not_echoed(self):
        text = self.write("publication.md", "PrefixprivatesentinelSuffix")
        code, report, output = self.check("--text-file", text, "--private-terms", self.terms())
        self.assertEqual(code, 1)
        self.assertEqual(report["findings"][0]["category"], "PrivateTerm")
        self.assertNotIn("PrivateSentinel", output)
        self.assertNotIn("privatesentinel", output)

    def test_private_terms_inside_checkout_are_refused(self):
        path = self.write("private-terms.txt", "PrivateSentinel")
        text = self.write("publication.md", "safe")
        code, report, output = self.check("--text-file", text, "--private-terms", path)
        self.assertEqual(code, 2)
        self.assertEqual(report["reason"], "TermsFileInsideRepository")
        self.assertNotIn("PrivateSentinel", output)

    def test_terms_in_git_metadata_are_refused(self):
        terms = self.repo / ".git" / "private-terms.txt"
        terms.write_text("PrivateSentinel", encoding="utf-8")
        text = self.write("publication.md", "safe")
        self.assertEqual(self.check("--text-file", text, "--private-terms", terms)[1]["reason"],
                         "TermsFileInsideRepository")

    def test_invalid_utf8_binary_unsupported_and_oversize_are_incomplete(self):
        cases = [("bad.txt", b"\xff", "InvalidUtf8"),
                 ("binary.txt", b"a\0b", "UnsupportedBinary"),
                 ("attachment.pdf", b"%PDF-synthetic", "UnsupportedCandidateType"),
                 ("pointer.txt", b"version https://git-lfs.github.com/spec/v1\noid sha256:synthetic\n", "UnsupportedExternalContent"),
                 ("control.txt", b"text\x01", "UnsupportedControlCharacters")]
        for name, data, reason in cases:
            with self.subTest(reason=reason):
                code, report, _ = self.check("--text-file", self.write(name, data))
                self.assertEqual(code, 2)
                self.assertFalse(report["complete"])
                self.assertIsNone(report["candidate_sha256"])
                self.assertEqual(report["reason"], reason)
        with mock.patch.object(pf, "MAX_OBJECT", 32):
            code, report, _ = self.check("--text-file", self.write("large.txt", b"x" * 33))
        self.assertEqual(code, 2)
        self.assertEqual(report["reason"], "InputTooLarge")

    def test_total_input_cap_and_duplicate_basenames(self):
        first = self.write("a/publication.txt", "x" * 20)
        second = self.write("b/publication.txt", "y" * 20)
        code, report, _ = self.check("--text-file", first, "--text-file", second)
        self.assertEqual(code, 0)
        self.assertEqual(report["files"], 2)
        with mock.patch.object(pf, "MAX_TOTAL", 32):
            self.assertEqual(self.check("--text-file", first, "--text-file", second)[0], 2)

    def test_staged_scan_does_not_read_unstaged_untracked_or_ignored_files(self):
        self.write(".gitignore", "ignored.txt\n")
        self.git("add", "--", ".gitignore")
        self.git("commit", "-m", "Synthetic ignores")
        self.write("candidate.txt", "generic staged text")
        self.git("add", "--", "candidate.txt")
        self.write("candidate.txt", r"C:\Users\PrivateSentinel\unselected")
        self.write("untracked.txt", r"C:\Users\PrivateSentinel\unselected")
        self.write("ignored.txt", r"C:\Users\PrivateSentinel\unselected")
        index_before = (self.repo / ".git/index").read_bytes()
        with mock.patch.object(pf, "read_regular", side_effect=self.metadata_only_reader()):
            code, report, output = self.check("--staged")
        self.assertEqual(code, 0)
        self.assertEqual(report["files"], 1)
        self.assertEqual(report["commits"], 0)
        self.assertNotIn("PrivateSentinel", output)
        self.assertEqual((self.repo / ".git/index").read_bytes(), index_before)
        self.assertFalse((self.repo / ".git/index.lock").exists())

    def test_staged_deletion_checks_preimage(self):
        self.commit_file(r"C:\Users\PrivateSentinel\candidate.txt")
        self.git("rm", "--", "candidate.txt")
        code, report, output = self.check("--staged")
        self.assertEqual(code, 1)
        self.assertTrue(report["findings"])
        self.assertNotIn("PrivateSentinel", output)

    def test_git_symlinks_and_gitlinks_refuse_without_following(self):
        self.git("config", "diff.ignoreSubmodules", "all")
        for mode in ("120000", "160000"):
            with self.subTest(mode=mode):
                oid = self.base if mode == "160000" else self.git(
                    "hash-object", "-w", "--stdin", input=b"../outside-private.txt").strip().decode("ascii")
                self.git("update-index", "--add", "--cacheinfo", mode + "," + oid + ",link.txt")
                with mock.patch.object(pf, "read_regular", side_effect=self.metadata_only_reader()):
                    code, report, _ = self.check("--staged")
                self.assertEqual(code, 2)
                self.assertEqual(report["reason"], "UnsupportedGitMode")

    def test_shallow_history_and_unmerged_index_are_not_clean(self):
        self.commit_file()
        (self.repo / ".git/shallow").write_text(self.base + "\n", encoding="ascii")
        self.assertEqual(self.check("--commits", self.base + "..HEAD")[1]["reason"],
                         "UnsupportedShallowHistory")
        (self.repo / ".git/shallow").unlink()
        blob = self.git("hash-object", "-w", "--stdin", input=b"safe").strip().decode("ascii")
        stages = "".join("100644 " + blob + " " + str(stage) + "\tconflict.txt\n" for stage in (1, 2, 3))
        self.git("update-index", "--index-info", input=stages.encode("ascii"))
        self.assertEqual(self.check("--staged")[0], 2)

    def test_filename_terms_and_control_names_never_appear_in_output(self):
        self.write("--PrivateSentinel.txt", "safe")
        self.git("add", "--", "--PrivateSentinel.txt")
        code, _, output = self.check("--staged", "--private-terms", self.terms())
        self.assertEqual(code, 1)
        self.assertNotIn("PrivateSentinel", output)
        self.git("reset", "--", "--PrivateSentinel.txt")
        self.write("line\nPrivateSentinel.txt", "safe")
        self.git("add", "--", "line\nPrivateSentinel.txt")
        self.assertEqual(self.check("--staged")[1]["reason"], "UnsupportedFilename")

    def test_range_scans_intermediate_commits_not_just_tip(self):
        self.commit_file(r"C:\Users\PrivateSentinel\temporary.txt")
        self.git("rm", "--", "candidate.txt")
        self.git("commit", "-m", "Remove temporary candidate")
        code, report, output = self.check("--commits", self.base + "..HEAD")
        self.assertEqual(code, 1)
        self.assertEqual(report["commits"], 2)
        self.assertNotIn("PrivateSentinel", output)

    def test_commit_messages_and_both_identity_fields_are_checked(self):
        env = dict(self.env, GIT_COMMITTER_NAME="Synthetic Person",
                   GIT_COMMITTER_EMAIL="person@example.test")
        self.commit_file(message="PrivateSentinel in synthetic message", env=env)
        code, report, output = self.check("--commits", self.base + "..HEAD", "--private-terms", self.terms())
        self.assertEqual(code, 1)
        categories = {finding["category"] for finding in report["findings"]}
        self.assertIn("PrivateTerm", categories)
        self.assertIn("UnapprovedCommitIdentity", categories)
        for secret in ("PrivateSentinel", "Synthetic Person", "person@example.test"):
            self.assertNotIn(secret, output)

    def test_noreply_email_does_not_exempt_a_different_author_name(self):
        env = dict(self.env, GIT_AUTHOR_NAME="Synthetic Personal Name")
        self.commit_file(env=env)
        self.assertEqual(self.check("--commits", self.base + "..HEAD")[0], 1)

    def test_explicit_generic_reserved_identity(self):
        env = dict(self.env, GIT_AUTHOR_NAME="Generic Agent", GIT_COMMITTER_NAME="Generic Agent",
                   GIT_AUTHOR_EMAIL="agent@example.invalid", GIT_COMMITTER_EMAIL="agent@example.invalid")
        self.commit_file(env=env)
        self.assertEqual(self.check("--commits", self.base + "..HEAD")[0], 1)
        code, report, _ = self.check("--commits", self.base + "..HEAD",
                                     "--public-identity", "Generic Agent <agent@example.invalid>")
        self.assertEqual(code, 0)
        self.assertEqual(report["commits"], 1)
        self.assertEqual(report["commit_metadata"], "checked")

    def test_amended_author_metadata_is_inspected(self):
        self.commit_file()
        self.git("commit", "--amend", "--no-edit", "--author", "Synthetic Person <person@example.test>")
        self.assertEqual(self.check("--commits", self.base + "..HEAD")[0], 1)

    def test_cherry_picked_author_metadata_is_inspected(self):
        self.git("checkout", "-b", "source")
        env = dict(self.env, GIT_AUTHOR_NAME="Synthetic Person", GIT_AUTHOR_EMAIL="person@example.test")
        self.commit_file(env=env)
        picked = self.git("rev-parse", "HEAD").strip().decode("ascii")
        self.git("checkout", "main")
        self.git("cherry-pick", picked)
        self.assertEqual(self.check("--commits", self.base + "..HEAD")[0], 1)

    def test_missing_objects_ranges_and_argument_injection_fail_closed(self):
        for value in ("HEAD...HEAD", "PrivateSentinel..HEAD", "--evil..HEAD", "HEAD;echo PrivateSentinel..HEAD"):
            with self.subTest(value=value):
                code, report, output = self.check("--commits=" + value)
                self.assertEqual(code, 2)
                self.assertFalse(report["complete"])
                self.assertNotIn("PrivateSentinel", output)
        code, _, output = self.check("--unknown=PrivateSentinel")
        self.assertEqual(code, 2)
        self.assertNotIn("PrivateSentinel", output)

    def test_index_change_during_scan_refuses(self):
        self.write("candidate.txt", "safe")
        self.git("add", "--", "candidate.txt")
        original = pf.Inspection.changes

        def mutate(check, git, raw):
            original(check, git, raw)
            self.write("candidate.txt", "different safe content")
            self.git("add", "--", "candidate.txt")

        with mock.patch.object(pf.Inspection, "changes", mutate):
            code, report, _ = self.check("--staged")
        self.assertEqual(code, 2)
        self.assertEqual(report["reason"], "CandidateChanged")

    def test_raw_git_reads_do_not_execute_filters_hooks_fsmonitor_or_network(self):
        self.write("candidate.txt", "generic staged text")
        self.write(".gitattributes", "candidate.txt filter=trap diff=trap\n")
        self.git("add", "--", "candidate.txt", ".gitattributes")
        marker = self.root / "forbidden-execution"
        trap = self.root / "trap.py"
        trap.write_text("from pathlib import Path\nPath(" + repr(str(marker)) + ").write_text('executed')\n", encoding="utf-8")
        command = '"' + sys.executable + '" "' + str(trap) + '"'
        for key in ("filter.trap.clean", "filter.trap.smudge", "diff.trap.textconv", "diff.external", "core.fsmonitor"):
            self.git("config", key, command)
        self.git("config", "core.hooksPath", str(self.root / "hooks"))
        hooks = self.root / "hooks"
        hooks.mkdir()
        for hook in ("post-index-change", "pre-commit"):
            file = hooks / hook
            file.write_text("#!/bin/sh\n" + command + "\n", encoding="utf-8")
            file.chmod(0o755)
        import socket
        real_popen = pf.subprocess.Popen
        with mock.patch.object(socket, "socket", side_effect=AssertionError("No Python network")), \
                mock.patch.object(pf.subprocess, "Popen", wraps=real_popen) as popen:
            code, _, _ = self.check("--staged")
        self.assertEqual(code, 0)
        self.assertFalse(marker.exists())
        for call in popen.call_args_list:
            command_line = call.args[0]
            self.assertIn("--no-lazy-fetch", command_line)
            self.assertIn("--no-optional-locks", command_line)
            self.assertIn("core.fsmonitor=false", command_line)
            self.assertFalse(call.kwargs["shell"])
            self.assertEqual(call.kwargs["env"]["GIT_NO_LAZY_FETCH"], "1")
            self.assertNotIn("--filters", command_line)
            self.assertNotIn("--textconv", command_line)
            self.assertFalse({"fetch", "push", "checkout", "add", "commit"}.intersection(command_line))

    def test_bounded_git_output_kills_only_owned_reader(self):
        class Output:
            requested = None
            def read(self, count):
                self.requested = count
                return b"x" * count
            def close(self):
                pass
        class Process:
            stdout = Output()
            killed = False
            def poll(self):
                return -9 if self.killed else None
            def kill(self):
                self.killed = True
            def wait(self, timeout=None):
                return -9
        process = Process()
        reader = pf.GitReader(self.repo)
        with mock.patch.object(pf.subprocess, "Popen", return_value=process):
            with self.assertRaisesRegex(pf.Refusal, "GitOutputTooLarge"):
                reader.run("rev-parse", "HEAD", limit=8)
        self.assertEqual(process.stdout.requested, 9)
        self.assertTrue(process.killed)

    def test_missing_promisor_object_does_not_invoke_transport(self):
        marker = self.root / "forbidden-transport"
        trap = self.root / "transport.py"
        trap.write_text("from pathlib import Path\nPath(" + repr(str(marker)) + ").write_text('executed')\n", encoding="utf-8")
        self.git("config", "extensions.partialClone", "origin")
        self.git("config", "remote.origin.promisor", "true")
        self.git("config", "remote.origin.url", "ssh://transport.example.invalid/project")
        self.git("config", "core.sshCommand", '"' + sys.executable + '" "' + str(trap) + '"')
        missing = "f" * len(self.base)
        self.git("update-index", "--add", "--info-only", "--cacheinfo", "100644," + missing + ",missing.txt")
        code, report, _ = self.check("--staged")
        self.assertEqual(code, 2)
        self.assertFalse(report["complete"])
        self.assertFalse(marker.exists())

    def test_config_include_is_refused_before_git_or_include_target_read(self):
        secret = self.root / "unselected-config"
        secret.write_text("PrivateSentinel", encoding="utf-8")
        self.git("config", "include.path", str(secret))
        with mock.patch.object(pf.subprocess, "Popen", side_effect=AssertionError("No Git setup")):
            code, report, output = self.check("--staged")
        self.assertEqual(code, 2)
        self.assertEqual(report["reason"], "UnsupportedConfigInclude")
        self.assertNotIn("PrivateSentinel", output)

    def test_linked_worktree_terms_boundary_and_raw_commit_selection(self):
        linked = self.root / "linked"
        self.git("worktree", "add", "-b", "linked", str(linked))
        out = io.StringIO()
        terms = self.repo / ".git" / "terms.txt"
        terms.write_text("PrivateSentinel", encoding="utf-8")
        with redirect_stdout(out):
            code = pf.main(["--repo", str(linked), "--staged", "--private-terms", str(terms)])
        self.assertEqual(code, 2)
        self.assertEqual(json.loads(out.getvalue())["reason"], "TermsFileInsideRepository")

    def test_supplied_symlink_and_empty_or_short_terms_refuse(self):
        text = self.write("publication.md", "safe")
        link = self.root / "link.md"
        link.symlink_to(text)
        self.assertEqual(self.check("--text-file", link)[1]["reason"], "UnsupportedFileType")
        for terms in ("", "xy"):
            self.assertEqual(self.check("--text-file", text, "--private-terms", self.terms(terms))[1]["reason"],
                             "InvalidTermsFile")


if __name__ == "__main__":
    unittest.main()
