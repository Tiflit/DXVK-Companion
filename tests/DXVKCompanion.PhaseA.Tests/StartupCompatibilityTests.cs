using System;
using System.IO;
using System.Linq;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Startup;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class StartupCompatibilityTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void RecoveryPreflight_MissingOrEmptyNamespace_PreservesNormalArgumentHandling(int layout)
        {
            using var root = new SyntheticTestDirectory();
            string library = root.CreateFile("Profiles/game-library.json", "{\"SchemaVersion\":1,\"Installations\":[]}");
            string backups = root.GetPath("Profiles/Backups");
            if (layout == 1) Directory.CreateDirectory(backups);
            if (layout == 2) Directory.CreateDirectory(Path.Combine(backups, ".transactions"));
            var before = Snapshot(root.RootPath);

            var options = Program.PrepareStartup(new[] { "--no-auto-discovery" }, library, out var preflight, backups);
            Assert.NotNull(options);
            Assert.False(options!.AutomaticDiscoveryEnabled);
            Assert.False(preflight.ShouldRefuse);
            Assert.Null(Program.PrepareStartup(new[] { "--unsupported" }, library, out preflight, backups));
            Assert.False(preflight.ShouldRefuse);
            AssertSnapshot(before, root.RootPath);
        }

        [Theory]
        [InlineData("--no-auto-discovery", "operation/plan.json", "{malformed")]
        [InlineData("--unsupported", "operation/undo/d3d11.dll", "retained-original")]
        [InlineData("--no-auto-discovery", "unknown-entry", "unknown")]
        [InlineData("--unsupported", "operation/plan.json", "{\"State\":\"Prepared\"}")]
        public void ProgramPreflight_RetainedEvidence_RefusesBeforeInitializationAndPreservesEverything(
            string argument, string entry, string contents)
        {
            using var root = new SyntheticTestDirectory();
            string library = root.CreateFile("Profiles/game-library.json", "{\"SchemaVersion\":1,\"Installations\":[]}");
            string backups = root.GetPath("Profiles/Backups");
            root.CreateFile("Profiles/Backups/.transactions/" + entry, contents);
            root.CreateFile("Profiles/Backups/original.dll", "native-backup");
            var before = Snapshot(root.RootPath);

            var options = Program.PrepareStartup(new[] { argument }, library, out var result, backups);

            Assert.Null(options);
            Assert.Equal(StartupAction.RefuseRecoveryEvidence, result.Action);
            Assert.True(result.ShouldRefuse);
            Assert.False(Directory.Exists(root.GetPath("Logs")));
            Assert.False(Directory.Exists(root.GetPath("Cache")));
            Assert.False(Directory.Exists(root.GetPath("DXVK")));
            AssertSnapshot(before, root.RootPath);
        }

        [Fact]
        public void RecoveryPreflight_PlanlessDirectoryAndNamespaceFile_BothRefuse()
        {
            using var root = new SyntheticTestDirectory();
            string library = root.GetPath("game-library.json");
            string first = root.GetPath("first");
            Directory.CreateDirectory(Path.Combine(first, ".transactions", "planless"));
            string second = root.GetPath("second");
            root.CreateFile("second/.transactions", "not-a-directory");
            var before = Snapshot(root.RootPath);

            Assert.True(StartupCompatibility.Evaluate(library, first).ShouldRefuse);
            Assert.True(StartupCompatibility.Evaluate(library, second).ShouldRefuse);
            AssertSnapshot(before, root.RootPath);
        }

        [Fact]
        public void RecoveryPreflight_NewerSchemaHasPriorityWithoutInspectingTransactions()
        {
            using var root = new SyntheticTestDirectory();
            string library = root.CreateFile("game-library.json", "{\"SchemaVersion\":999,\"Installations\":[]}");
            var probe = new RetainedTransactionProbe(_ => throw new InvalidOperationException("Must not inspect"));

            var result = StartupCompatibility.Evaluate(library, root.GetPath("Backups"), probe);

            Assert.Equal(StartupAction.RefuseIncompatibleSchema, result.Action);
        }

        [Fact]
        public void RecoveryPreflight_CustomLibraryUsesItsSiblingBackupsWithoutExplicitOverride()
        {
            using var root = new SyntheticTestDirectory();
            string library = root.GetPath("Profiles/game-library.json");
            root.CreateFile("Profiles/Backups/.transactions/retained", "evidence");
            Assert.Equal(StartupAction.RefuseRecoveryEvidence, StartupCompatibility.Evaluate(library).Action);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RecoveryPreflight_UnreadableAttributesOrEnumeration_RefusesWithoutLeakingDetails(bool enumeration)
        {
            using var root = new SyntheticTestDirectory();
            string library = root.GetPath("game-library.json");
            string backups = root.GetPath("Backups");
            Directory.CreateDirectory(Path.Combine(backups, ".transactions"));
            var before = Snapshot(root.RootPath);
            var probe = enumeration
                ? new RetainedTransactionProbe(hasEntries: _ => throw new IOException("private-enumeration-detail"))
                : new RetainedTransactionProbe(_ => throw new UnauthorizedAccessException("private-attribute-detail"));

            var result = StartupCompatibility.Evaluate(library, backups, probe);

            Assert.Equal(StartupAction.RefuseRecoveryEvidence, result.Action);
            Assert.DoesNotContain("private-", result.Message!);
            Assert.DoesNotContain(root.RootPath, result.Message!);
            AssertSnapshot(before, root.RootPath);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RecoveryPreflight_ReparseNamespaceOrAncestor_RefusesBeforeFollowingIt(bool ancestor)
        {
            using var root = new SyntheticTestDirectory();
            string backups = root.GetPath("Backups");
            string reparse = ancestor ? backups : Path.Combine(backups, ".transactions");
            bool reached = false;
            bool followed = false;
            var probe = new RetainedTransactionProbe(path =>
            {
                if (reached) followed = true;
                if (string.Equals(path, reparse, StringComparison.OrdinalIgnoreCase))
                {
                    reached = true;
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return FileAttributes.Directory;
            }, _ => throw new InvalidOperationException("Must not enumerate through reparse point"));

            var result = StartupCompatibility.Evaluate(root.GetPath("game-library.json"), backups, probe);

            Assert.True(reached);
            Assert.False(followed);
            Assert.Equal(StartupAction.RefuseRecoveryEvidence, result.Action);
        }

        [Fact]
        public void RecoveryPreflight_EntryCheckDoesNotReadArtifactContentsOrEnumerateChildren()
        {
            using var root = new SyntheticTestDirectory();
            string backups = root.GetPath("Backups");
            string expected = Path.Combine(backups, ".transactions");
            int calls = 0;
            var probe = new RetainedTransactionProbe(_ => FileAttributes.Directory, path =>
            {
                Assert.Equal(expected, path);
                calls++;
                return true;
            });

            Assert.True(StartupCompatibility.Evaluate(root.GetPath("game-library.json"), backups, probe).ShouldRefuse);
            Assert.Equal(1, calls);
            Assert.Empty(Directory.GetFileSystemEntries(root.RootPath));
        }

        [Fact]
        public void RecoveryRefusal_UsesRecoveryTitleAndSafeActionableMessage()
        {
            using var root = new SyntheticTestDirectory();
            root.CreateFile("Backups/.transactions/private-entry", "private-content");
            var result = StartupCompatibility.Evaluate(root.GetPath("game-library.json"), root.GetPath("Backups"));
            string? title = null;
            string? message = null;
            StartupCompatibility.MessagePresenter = (t, m) => { title = t; message = m; };
            try { StartupCompatibility.PresentRefusal(result); }
            finally { StartupCompatibility.MessagePresenter = null; }

            Assert.Equal("DXVK Companion - Recovery Attention Required", title);
            Assert.Equal(result.Message, message);
            Assert.Contains("preserve", message!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No game management has started", message!);
            Assert.DoesNotContain(root.RootPath, message!);
            Assert.DoesNotContain("private-", message!);
            Assert.DoesNotContain("delete", message!, StringComparison.OrdinalIgnoreCase);
        }

        private static System.Collections.Generic.Dictionary<string, string> Snapshot(string root) =>
            System.IO.Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(root, path),
                    path => Directory.Exists(path) ? "directory" : "file:" + Convert.ToBase64String(File.ReadAllBytes(path)));

        private static void AssertSnapshot(System.Collections.Generic.Dictionary<string, string> before, string root)
        {
            var after = Snapshot(root);
            Assert.Equal(before.Keys.OrderBy(p => p), after.Keys.OrderBy(p => p));
            foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
        }

        [Theory]
        [InlineData("--no-auto-discovery")]
        [InlineData("--unsupported")]
        public void ProgramPreflight_NewerSchemaKeepsPriorityAndPreservesLibrary(string argument)
        {
            using var root = new SyntheticTestDirectory();
            string path = Path.Combine(root.RootPath, "game-library.json");
            File.WriteAllText(path, "{\"SchemaVersion\":999,\"Installations\":[]}");
            byte[] before = File.ReadAllBytes(path);
            var options = Program.PrepareStartup(new[] { argument }, path, out var preflight);
            Assert.Null(options);
            Assert.True(preflight.ShouldRefuse);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(root.RootPath, "*", SearchOption.AllDirectories));
        }

        [Fact]
        public void ProgramPreflight_InvalidArgumentsDoNotInitializeMissingRuntimeDirectories()
        {
            using var root = new SyntheticTestDirectory();
            var options = Program.PrepareStartup(new[] { "--unsupported" },
                Path.Combine(root.RootPath, "uncreated", "game-library.json"), out var preflight);
            Assert.Null(options);
            Assert.False(preflight.ShouldRefuse);
            Assert.Empty(Directory.GetFileSystemEntries(root.RootPath));
        }

        [Fact]
        public void Evaluate_MissingLibraryFile_ReturnsContinue()
        {
            using var testDir = new SyntheticTestDirectory();
            string missingPath = Path.Combine(testDir.RootPath, "game-library.json");

            var result = StartupCompatibility.Evaluate(missingPath);

            Assert.Equal(StartupAction.Continue, result.Action);
            Assert.False(result.ShouldRefuse);
            Assert.Null(result.Message);
        }

        [Fact]
        public void Evaluate_SupportedSchemaLibraryFile_ReturnsContinue()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            File.WriteAllText(libraryPath, @"{ ""SchemaVersion"": 1, ""Installations"": [] }");

            var result = StartupCompatibility.Evaluate(libraryPath);

            Assert.Equal(StartupAction.Continue, result.Action);
            Assert.False(result.ShouldRefuse);
            Assert.Null(result.Message);
        }

        [Fact]
        public void Evaluate_MalformedLibraryFile_ReturnsContinue_ToAllowStoreRecovery()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            File.WriteAllText(libraryPath, "{ broken json content !!!");

            var result = StartupCompatibility.Evaluate(libraryPath);

            Assert.Equal(StartupAction.Continue, result.Action);
            Assert.False(result.ShouldRefuse);
            Assert.Null(result.Message);
        }

        [Theory]
        [InlineData("2")]
        [InlineData("999")]
        [InlineData("100000000000000000000000000000000000000000000000000")]
        public void Evaluate_ExplicitNewerSchema_ReturnsRefuseWithSafeMessage(string newerVersionText)
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            File.WriteAllText(libraryPath, $@"{{ ""SchemaVersion"": {newerVersionText}, ""Installations"": [] }}");

            var result = StartupCompatibility.Evaluate(libraryPath);

            Assert.Equal(StartupAction.RefuseIncompatibleSchema, result.Action);
            Assert.True(result.ShouldRefuse);
            Assert.Equal(newerVersionText, result.FoundSchemaVersionText);
            Assert.Equal(GameLibrary.CurrentSchemaVersion, result.SupportedSchemaVersion);

            Assert.NotNull(result.Message);

            // Must contain found numeric schema and supported schema
            Assert.Contains(newerVersionText, result.Message);
            Assert.Contains(GameLibrary.CurrentSchemaVersion.ToString(), result.Message);

            // Must explain compatible Companion build is required
            Assert.Contains("compatible", result.Message, StringComparison.OrdinalIgnoreCase);

            // Must NOT contain raw paths, file contents, unsupported recovery, or data relocation options
            Assert.DoesNotContain(libraryPath, result.Message);
            Assert.DoesNotContain(testDir.RootPath, result.Message);
            Assert.DoesNotContain("game-library.json", result.Message);
            Assert.DoesNotContain("{", result.Message);
            Assert.DoesNotContain("Installations", result.Message);
            Assert.DoesNotContain("recovery", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("relocat", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PresentRefusal_WhenRefused_InvokesMessagePresenterHook()
        {
            string? capturedTitle = null;
            string? capturedMessage = null;

            StartupCompatibility.MessagePresenter = (title, msg) =>
            {
                capturedTitle = title;
                capturedMessage = msg;
            };

            try
            {
                var refusal = StartupPreflightResult.Refuse("5", 1, "Test refusal message");
                StartupCompatibility.PresentRefusal(refusal);

                Assert.Equal("DXVK Companion - Incompatible Library", capturedTitle);
                Assert.Equal("Test refusal message", capturedMessage);
            }
            finally
            {
                StartupCompatibility.MessagePresenter = null;
            }
        }

        [Fact]
        public void PresentRefusal_WhenContinue_DoesNotInvokeMessagePresenterHook()
        {
            bool invoked = false;
            StartupCompatibility.MessagePresenter = (_, _) => invoked = true;

            try
            {
                var continueResult = StartupPreflightResult.Continue();
                StartupCompatibility.PresentRefusal(continueResult);

                Assert.False(invoked);
            }
            finally
            {
                StartupCompatibility.MessagePresenter = null;
            }
        }

        [Fact]
        public void Evaluate_Utf16NewerSchemaWithUnknownEnumContent_ReturnsRefuseWithSafeMessage()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            string futureJson = @"{
  ""SchemaVersion"": 12,
  ""Installations"": [
    {
      ""InstallationPath"": ""C:\\Games\\FutureUtf16"",
      ""DisplayName"": ""Future UTF16"",
      ""ManagementPolicy"": {
        ""Mode"": ""FuturisticAutonomousMode""
      }
    }
  ]
}";
            File.WriteAllText(libraryPath, futureJson, System.Text.Encoding.Unicode);

            var result = StartupCompatibility.Evaluate(libraryPath);

            Assert.Equal(StartupAction.RefuseIncompatibleSchema, result.Action);
            Assert.True(result.ShouldRefuse);
            Assert.Equal("12", result.FoundSchemaVersionText);
            Assert.NotNull(result.Message);
            Assert.Contains("12", result.Message);
            Assert.Contains(GameLibrary.CurrentSchemaVersion.ToString(), result.Message);
            Assert.DoesNotContain(libraryPath, result.Message);
        }

        [Theory]
        [InlineData(@"{ ""SchemaVersion"": 2, ""Installations"": [], }")]
        [InlineData(@"{ /* comment */ ""SchemaVersion"": 2, ""Installations"": [] }")]
        public void Evaluate_TrailingCommaOrComment_ReturnsContinue_ToAllowStoreRecovery(string json)
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            File.WriteAllText(libraryPath, json);

            var result = StartupCompatibility.Evaluate(libraryPath);

            // Startup continues so the store can perform standard recovery
            Assert.Equal(StartupAction.Continue, result.Action);
            Assert.False(result.ShouldRefuse);
            Assert.Null(result.Message);
        }
    }
}
