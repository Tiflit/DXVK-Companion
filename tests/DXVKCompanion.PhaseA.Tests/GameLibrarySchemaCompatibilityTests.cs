using System;
using System.IO;
using System.Text;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class GameLibrarySchemaCompatibilityTests
    {
        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(999)]
        public void AssessJson_WellFormedPositiveIntegerAboveCurrentSchema_IsIdentifiedAsIncompatibleNewer(int newerVersion)
        {
            string json = $@"{{
  ""SchemaVersion"": {newerVersion},
  ""Installations"": []
}}";
            var result = GameLibrarySchemaCompatibility.AssessJson(json);

            Assert.Equal(SchemaCompatibilityStatus.IncompatibleNewer, result.Status);
            Assert.True(result.IsRefused);
            Assert.False(result.IsCompatible);
            Assert.Equal(newerVersion.ToString(), result.FoundSchemaVersionText);
            Assert.Equal(GameLibrary.CurrentSchemaVersion, result.SupportedSchemaVersion);
        }

        [Fact]
        public void AssessJson_IntegerGreaterThanInt64MaxValue_IsIdentifiedAsIncompatibleNewer()
        {
            string hugeVersion = "99999999999999999999999999999999999999999999999999";
            string json = $@"{{
  ""SchemaVersion"": {hugeVersion},
  ""Installations"": []
}}";
            var result = GameLibrarySchemaCompatibility.AssessJson(json);

            Assert.Equal(SchemaCompatibilityStatus.IncompatibleNewer, result.Status);
            Assert.True(result.IsRefused);
            Assert.Equal(hugeVersion, result.FoundSchemaVersionText);
        }

        [Fact]
        public void AssessJson_CurrentSupportedSchema_IsIdentifiedAsCompatible()
        {
            string json = $@"{{
  ""SchemaVersion"": {GameLibrary.CurrentSchemaVersion},
  ""Installations"": []
}}";
            var result = GameLibrarySchemaCompatibility.AssessJson(json);

            Assert.Equal(SchemaCompatibilityStatus.Compatible, result.Status);
            Assert.False(result.IsRefused);
            Assert.True(result.IsCompatible);
            Assert.Equal("1", result.FoundSchemaVersionText);
        }

        [Theory]
        [InlineData(@"{ ""SchemaVersion"": 0, ""Installations"": [] }")]
        [InlineData(@"{ ""SchemaVersion"": -1, ""Installations"": [] }")]
        [InlineData(@"{ ""SchemaVersion"": -999, ""Installations"": [] }")]
        public void AssessJson_ZeroOrNegativeSchemaVersion_IsMalformedNotNewer(string json)
        {
            var result = GameLibrarySchemaCompatibility.AssessJson(json);

            Assert.Equal(SchemaCompatibilityStatus.Malformed, result.Status);
            Assert.False(result.IsRefused, "Zero or negative schema version must NOT be labeled newer");
        }

        [Theory]
        [InlineData(@"{ ""SchemaVersion"": 1.5, ""Installations"": [] }")]
        [InlineData(@"{ ""SchemaVersion"": 2.0, ""Installations"": [] }")]
        [InlineData(@"{ ""SchemaVersion"": 1e5, ""Installations"": [] }")]
        public void AssessJson_NonIntegerNumberSchemaVersion_IsMalformedNotNewer(string json)
        {
            var result = GameLibrarySchemaCompatibility.AssessJson(json);

            Assert.Equal(SchemaCompatibilityStatus.Malformed, result.Status);
            Assert.False(result.IsRefused, "Floating point or exponential schema markers must NOT be labeled newer");
        }

        [Theory]
        [InlineData(@"{ ""SchemaVersion"": ""2"", ""Installations"": [] }")]
        [InlineData(@"{ ""SchemaVersion"": null, ""Installations"": [] }")]
        [InlineData(@"{ ""SchemaVersion"": true, ""Installations"": [] }")]
        [InlineData(@"{ ""SchemaVersion"": false, ""Installations"": [] }")]
        [InlineData(@"{ ""SchemaVersion"": [ 2 ], ""Installations"": [] }")]
        [InlineData(@"{ ""SchemaVersion"": { ""version"": 2 }, ""Installations"": [] }")]
        public void AssessJson_NonNumericSchemaVersion_IsMalformedNotNewer(string json)
        {
            var result = GameLibrarySchemaCompatibility.AssessJson(json);

            Assert.Equal(SchemaCompatibilityStatus.Malformed, result.Status);
            Assert.False(result.IsRefused, "Non-numeric schema markers must NOT be labeled newer");
        }

        [Theory]
        [InlineData("{ invalid json !!")]
        [InlineData("{ \"SchemaVersion\": ")]
        [InlineData("")]
        [InlineData("   ")]
        public void AssessJson_InvalidJson_IsMalformedNotNewer(string json)
        {
            var result = GameLibrarySchemaCompatibility.AssessJson(json);

            Assert.Equal(SchemaCompatibilityStatus.Malformed, result.Status);
            Assert.False(result.IsRefused);
        }

        [Theory]
        [InlineData("[ 1, 2, 3 ]")]
        [InlineData("\"hello world\"")]
        [InlineData("12345")]
        public void AssessJson_NonObjectRoot_IsMalformedNotNewer(string json)
        {
            var result = GameLibrarySchemaCompatibility.AssessJson(json);

            Assert.Equal(SchemaCompatibilityStatus.Malformed, result.Status);
            Assert.False(result.IsRefused);
        }

        [Fact]
        public void AssessJson_MissingSchemaVersionProperty_IsCompatibleDefault()
        {
            string json = @"{ ""Installations"": [] }";
            var result = GameLibrarySchemaCompatibility.AssessJson(json);

            Assert.Equal(SchemaCompatibilityStatus.Compatible, result.Status);
            Assert.False(result.IsRefused);
            Assert.Null(result.FoundSchemaVersionText);
        }

        [Theory]
        [InlineData("schemaversion")]
        [InlineData("SCHEMAVERSION")]
        [InlineData("SchemaVersion")]
        [InlineData("schemaVersion")]
        public void AssessJson_CaseInsensitivePropertyHandling_IdentifiesNewerSchema(string propName)
        {
            string json = $@"{{
  ""{propName}"": 42,
  ""Installations"": []
}}";
            var result = GameLibrarySchemaCompatibility.AssessJson(json);

            Assert.Equal(SchemaCompatibilityStatus.IncompatibleNewer, result.Status);
            Assert.Equal("42", result.FoundSchemaVersionText);
        }

        [Fact]
        public void AssessJson_Utf8BomAndLeadingWhitespace_IsHandledCleanly()
        {
            string text = "   \t\r\n{ \"SchemaVersion\": 5, \"Installations\": [] }";
            byte[] textBytes = Encoding.UTF8.GetBytes(text);
            byte[] bomBytes = Encoding.UTF8.GetPreamble();

            byte[] fullBytes = new byte[bomBytes.Length + textBytes.Length];
            Buffer.BlockCopy(bomBytes, 0, fullBytes, 0, bomBytes.Length);
            Buffer.BlockCopy(textBytes, 0, fullBytes, bomBytes.Length, textBytes.Length);

            var result = GameLibrarySchemaCompatibility.AssessJson(fullBytes);

            Assert.Equal(SchemaCompatibilityStatus.IncompatibleNewer, result.Status);
            Assert.Equal("5", result.FoundSchemaVersionText);
        }

        [Fact]
        public void AssessJson_UnknownFutureEnumValuesAndExtraFields_DoesNotDivertToMalformed()
        {
            string json = @"{
  ""SchemaVersion"": 4,
  ""ExtraNewField"": ""SomeFutureData"",
  ""Installations"": [
    {
      ""InstallationPath"": ""C:\\Games\\FutureRPG"",
      ""DisplayName"": ""Future RPG"",
      ""ManagementPolicy"": {
        ""Mode"": ""TotallyNewFutureModeThatDoesNotExistInV1"",
        ""FutureProperty"": 12345
      },
      ""Executables"": [
        {
          ""RelativeExecutablePath"": ""bin/game.exe"",
          ""LastKnownApi"": ""DirectX99""
        }
      ]
    }
  ]
}";
            var result = GameLibrarySchemaCompatibility.AssessJson(json);

            Assert.Equal(SchemaCompatibilityStatus.IncompatibleNewer, result.Status);
            Assert.True(result.IsRefused, "Future enum values or fields must NOT divert explicit newer schema into malformed recovery");
            Assert.Equal("4", result.FoundSchemaVersionText);
        }

        [Fact]
        public void AssessFile_MissingFile_ReturnsMissing()
        {
            using var testDir = new SyntheticTestDirectory();
            string nonExistent = Path.Combine(testDir.RootPath, "non-existent-library.json");

            var result = GameLibrarySchemaCompatibility.AssessFile(nonExistent);

            Assert.Equal(SchemaCompatibilityStatus.Missing, result.Status);
            Assert.False(result.IsRefused);
            Assert.True(result.IsCompatible);
        }

        [Fact]
        public void AssessFile_ExistingNewerFile_ReturnsIncompatibleNewer()
        {
            using var testDir = new SyntheticTestDirectory();
            string file = Path.Combine(testDir.RootPath, "game-library.json");
            File.WriteAllText(file, @"{ ""SchemaVersion"": 2, ""Installations"": [] }");

            var result = GameLibrarySchemaCompatibility.AssessFile(file);

            Assert.Equal(SchemaCompatibilityStatus.IncompatibleNewer, result.Status);
            Assert.Equal("2", result.FoundSchemaVersionText);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AssessFile_Utf16NewerSchemaWithUnknownEnumContent_IdentifiedAsIncompatibleNewer(bool littleEndian)
        {
            using var testDir = new SyntheticTestDirectory();
            string file = Path.Combine(testDir.RootPath, "game-library.json");
            Encoding encoding = littleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode;

            string futureJson = @"{
  ""SchemaVersion"": 8,
  ""Installations"": [
    {
      ""InstallationPath"": ""C:\\Games\\Utf16Game"",
      ""DisplayName"": ""UTF16 Game"",
      ""ManagementPolicy"": {
        ""Mode"": ""FuturisticAutonomousMode777""
      }
    }
  ]
}";
            File.WriteAllText(file, futureJson, encoding);

            var result = GameLibrarySchemaCompatibility.AssessFile(file);

            Assert.Equal(SchemaCompatibilityStatus.IncompatibleNewer, result.Status);
            Assert.True(result.IsRefused);
            Assert.Equal("8", result.FoundSchemaVersionText);
        }

        [Fact]
        public void AssessJson_DuplicateSchemaVersionProperty_LastPropertyDeterminesEffectiveVersion()
        {
            // Case 1: First is supported (1), last is newer (999) -> effective is 999 (IncompatibleNewer)
            string jsonNewerWins = @"{ ""SchemaVersion"": 1, ""schemaVersion"": 999, ""Installations"": [] }";
            var result1 = GameLibrarySchemaCompatibility.AssessJson(jsonNewerWins);
            Assert.Equal(SchemaCompatibilityStatus.IncompatibleNewer, result1.Status);
            Assert.Equal("999", result1.FoundSchemaVersionText);

            // Case 2: First is newer (999), last is supported (1) -> effective is 1 (Compatible)
            string jsonSupportedWins = @"{ ""SchemaVersion"": 999, ""schemaVersion"": 1, ""Installations"": [] }";
            var result2 = GameLibrarySchemaCompatibility.AssessJson(jsonSupportedWins);
            Assert.Equal(SchemaCompatibilityStatus.Compatible, result2.Status);
            Assert.Equal("1", result2.FoundSchemaVersionText);

            // Case 3: First is newer (999), last is malformed string -> effective is malformed
            string jsonMalformedWins = @"{ ""SchemaVersion"": 999, ""schemaVersion"": ""invalid"", ""Installations"": [] }";
            var result3 = GameLibrarySchemaCompatibility.AssessJson(jsonMalformedWins);
            Assert.Equal(SchemaCompatibilityStatus.Malformed, result3.Status);

            // Case 4: First is malformed string, last is newer (999) -> effective is 999 (IncompatibleNewer)
            string jsonNewerOverwritesMalformed = @"{ ""SchemaVersion"": ""invalid"", ""schemaVersion"": 999, ""Installations"": [] }";
            var result4 = GameLibrarySchemaCompatibility.AssessJson(jsonNewerOverwritesMalformed);
            Assert.Equal(SchemaCompatibilityStatus.IncompatibleNewer, result4.Status);
            Assert.Equal("999", result4.FoundSchemaVersionText);
        }

        [Theory]
        [InlineData(@"{ ""SchemaVersion"": 2, ""Installations"": [], }")]
        [InlineData(@"{ /* comment */ ""SchemaVersion"": 2, ""Installations"": [] }")]
        [InlineData(@"{ ""SchemaVersion"": 2 // inline comment
, ""Installations"": [] }")]
        public void AssessJson_TrailingCommasAndComments_TreatedAsMalformedConsistentWithLoader(string json)
        {
            // The unchanged loader uses default JsonOptions which disallows comments and trailing commas.
            // Strict preflight ensures these are classified as Malformed rather than falsely refused.
            var result = GameLibrarySchemaCompatibility.AssessJson(json);

            Assert.Equal(SchemaCompatibilityStatus.Malformed, result.Status);
            Assert.False(result.IsRefused);
        }
    }
}
