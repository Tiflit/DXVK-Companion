using System;
using System.IO;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Startup;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class StartupCompatibilityTests
    {
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
