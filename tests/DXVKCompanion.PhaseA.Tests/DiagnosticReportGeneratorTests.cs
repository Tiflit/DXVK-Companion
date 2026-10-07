using System;
using DXVKCompanion.Diagnostics;
using DXVKCompanion.Models;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public sealed class DiagnosticReportGeneratorTests
    {
        [Fact]
        public void Generate_ValidExactOutput_ProducesExpectedReport()
        {
            var profile = new GameProfile(@"C:\Games\SecretGame\game.exe")
            {
                Api = GraphicsApi.DX11,
                Architecture = "x64"
            };

            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\SecretGame",
                DisplayName = "Secret Game",
                RestorationState = RestorationState.AttentionRequired,
                ManagedDxvkVersion = "2.5",
                ConflictFlags = InstallationConflictFlags.None,
                ManagementPolicy = ManagementPolicy.UseGlobal()
            };

            string report = DiagnosticReportGenerator.Generate(profile, installation, "1.0.0");

            string expected = string.Join(Environment.NewLine, new[]
            {
                "DXVK Companion Diagnostic Report (v1)",
                "App Version: 1.0.0",
                "Recorded API: DX11",
                "Recorded Arch: x64",
                "Restoration State: AttentionRequired",
                "Managed DXVK: 2.5",
                "Conflict Flags: None",
                "Installation Policy Mode: UseGlobal",
                "Refusal Details: Unavailable",
                "Evidence Freshness: RecordedSnapshotOnly",
                "Anti-Cheat Assessment: NotAcquired"
            });

            Assert.Equal(expected, report);
        }

        [Fact]
        public void Generate_NullInstallation_ReportsUnavailableForInstallationFields()
        {
            var profile = new GameProfile(@"C:\Games\SecretGame\game.exe")
            {
                Api = GraphicsApi.DX9,
                Architecture = "x86"
            };

            string report = DiagnosticReportGenerator.Generate(profile, null, "1.0.0");

            Assert.Contains("Recorded API: DX9", report);
            Assert.Contains("Recorded Arch: x86", report);
            Assert.Contains("Restoration State: Unavailable", report);
            Assert.Contains("Managed DXVK: Unavailable", report);
            Assert.Contains("Conflict Flags: Unavailable", report);
            Assert.Contains("Installation Policy Mode: Unavailable", report);
            Assert.Contains("Refusal Details: Unavailable", report);
            Assert.Contains("Evidence Freshness: RecordedSnapshotOnly", report);
            Assert.Contains("Anti-Cheat Assessment: NotAcquired", report);
        }

        [Fact]
        public void Generate_NullProfile_ReportsUnknownForProfileFields()
        {
            var installation = new GameInstallation
            {
                RestorationState = RestorationState.Managed,
                ManagedDxvkVersion = "2.4",
                ConflictFlags = InstallationConflictFlags.None,
                ManagementPolicy = ManagementPolicy.Automatic()
            };

            string report = DiagnosticReportGenerator.Generate(null, installation, "1.0.0");

            Assert.Contains("Recorded API: Unknown", report);
            Assert.Contains("Recorded Arch: Unknown", report);
            Assert.Contains("Restoration State: Managed", report);
            Assert.Contains("Managed DXVK: 2.4", report);
            Assert.Contains("Conflict Flags: None", report);
            Assert.Contains("Installation Policy Mode: Automatic", report);
        }

        [Theory]
        [InlineData(GraphicsApi.Unknown, "Recorded API: Unknown")]
        [InlineData(GraphicsApi.DX9, "Recorded API: DX9")]
        [InlineData(GraphicsApi.DX10, "Recorded API: DX10")]
        [InlineData(GraphicsApi.DX11, "Recorded API: DX11")]
        [InlineData(GraphicsApi.ModernAPI, "Recorded API: ModernAPI")]
        [InlineData(GraphicsApi.DX12, "Recorded API: DX12")]
        [InlineData(GraphicsApi.Vulkan, "Recorded API: Vulkan")]
        [InlineData((GraphicsApi)99, "Recorded API: Unknown")]
        public void Generate_GraphicsApiClassifications_MappedCorrectly(GraphicsApi api, string expectedLine)
        {
            var profile = new GameProfile("game.exe") { Api = api, Architecture = "x64" };
            string report = DiagnosticReportGenerator.Generate(profile, null, "1.0.0");
            Assert.Contains(expectedLine, report);
        }

        [Theory]
        [InlineData(RestorationState.None, "Restoration State: None")]
        [InlineData(RestorationState.Managed, "Restoration State: Managed")]
        [InlineData(RestorationState.Restored, "Restoration State: Restored")]
        [InlineData(RestorationState.AttentionRequired, "Restoration State: AttentionRequired")]
        [InlineData((RestorationState)99, "Restoration State: Unavailable")]
        public void Generate_RestorationStateClassifications_MappedCorrectly(RestorationState state, string expectedLine)
        {
            var installation = new GameInstallation { RestorationState = state };
            string report = DiagnosticReportGenerator.Generate(null, installation, "1.0.0");
            Assert.Contains(expectedLine, report);
        }

        [Theory]
        [InlineData(ManagementMode.UseGlobal, "Installation Policy Mode: UseGlobal")]
        [InlineData(ManagementMode.Automatic, "Installation Policy Mode: Automatic")]
        [InlineData(ManagementMode.PinnedVersion, "Installation Policy Mode: PinnedVersion")]
        [InlineData(ManagementMode.Disabled, "Installation Policy Mode: Disabled")]
        [InlineData((ManagementMode)99, "Installation Policy Mode: Unavailable")]
        public void Generate_ManagementModes_MappedCorrectly(ManagementMode mode, string expectedLine)
        {
            var installation = new GameInstallation
            {
                ManagementPolicy = new ManagementPolicy { Mode = mode }
            };
            string report = DiagnosticReportGenerator.Generate(null, installation, "1.0.0");
            Assert.Contains(expectedLine, report);
        }

        [Theory]
        [InlineData("x64", "Recorded Arch: x64")]
        [InlineData("X64", "Recorded Arch: x64")]
        [InlineData("x86", "Recorded Arch: x86")]
        [InlineData("X86", "Recorded Arch: x86")]
        [InlineData("x32", "Recorded Arch: x32")]
        [InlineData("X32", "Recorded Arch: x32")]
        [InlineData("arm64", "Recorded Arch: Unknown")]
        [InlineData("ARM64", "Recorded Arch: Unknown")]
        [InlineData("Unknown", "Recorded Arch: Unknown")]
        [InlineData("", "Recorded Arch: Unknown")]
        [InlineData(null, "Recorded Arch: Unknown")]
        [InlineData("x64; injected text", "Recorded Arch: Unknown")]
        public void Generate_ArchitectureMappings_MatchConstantsOrUnknown(string? arch, string expectedLine)
        {
            var profile = new GameProfile("game.exe") { Architecture = arch! };
            string report = DiagnosticReportGenerator.Generate(profile, null, "1.0.0");
            Assert.Contains(expectedLine, report);
        }

        [Theory]
        [InlineData("1.0", "App Version: 1.0")]
        [InlineData("2.5", "App Version: 2.5")]
        [InlineData("1.0.0", "App Version: 1.0.0")]
        [InlineData("2.5.1", "App Version: 2.5.1")]
        [InlineData("0.0.1", "App Version: 0.0.1")]
        [InlineData("123456789.123456789.123456789", "App Version: 123456789.123456789.123456789")]
        public void Generate_VersionFormatting_AcceptsValidBorders(string version, string expectedLine)
        {
            string report = DiagnosticReportGenerator.Generate(null, null, version);
            Assert.Contains(expectedLine, report);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("1")]
        [InlineData("1.0.0.0")]
        [InlineData("v1.0.0")]
        [InlineData("V1.0")]
        [InlineData("1.0.0-alpha")]
        [InlineData("1.0.0+build1")]
        [InlineData(" 1.0.0")]
        [InlineData("1.0.0 ")]
        [InlineData("+1.0")]
        [InlineData("-1.0")]
        [InlineData("1.0\n0")]
        [InlineData("1.0\r\n.0")]
        [InlineData("1.0\0.0")]
        [InlineData("١.٠.٠")]
        [InlineData("１.０.０")]
        [InlineData("1.1234567890")] // Component with 10 digits
        [InlineData("123456789.123456789.1234567890")] // Length 30 > 29
        [InlineData("NotAVersion")]
        public void Generate_VersionFormatting_RejectsInvalidAndEmitsUnavailable(string? invalidVersion)
        {
            string report = DiagnosticReportGenerator.Generate(null, null, invalidVersion);
            Assert.Contains("App Version: Unavailable", report);
        }

        [Fact]
        public void Generate_ManagedDxvkVersion_ValidatedWithSameRules()
        {
            var validInstallation = new GameInstallation { ManagedDxvkVersion = "2.5" };
            string reportValid = DiagnosticReportGenerator.Generate(null, validInstallation, "1.0.0");
            Assert.Contains("Managed DXVK: 2.5", reportValid);

            var invalidInstallation = new GameInstallation { ManagedDxvkVersion = "v2.5-dev\nmalicious" };
            string reportInvalid = DiagnosticReportGenerator.Generate(null, invalidInstallation, "1.0.0");
            Assert.Contains("Managed DXVK: Unavailable", reportInvalid);
        }

        [Theory]
        [InlineData(InstallationConflictFlags.None, "Conflict Flags: None")]
        [InlineData(InstallationConflictFlags.DxvkVersion, "Conflict Flags: DxvkVersion")]
        [InlineData(InstallationConflictFlags.Architecture, "Conflict Flags: Architecture")]
        [InlineData(InstallationConflictFlags.FrameLimit, "Conflict Flags: FrameLimit")]
        [InlineData(InstallationConflictFlags.UnknownOriginalFile, "Conflict Flags: UnknownOriginalFile")]
        [InlineData(InstallationConflictFlags.DxvkVersion | InstallationConflictFlags.Architecture, "Conflict Flags: DxvkVersion, Architecture")]
        [InlineData(InstallationConflictFlags.DxvkVersion | InstallationConflictFlags.UnknownOriginalFile, "Conflict Flags: DxvkVersion, UnknownOriginalFile")]
        [InlineData(
            InstallationConflictFlags.DxvkVersion | InstallationConflictFlags.Architecture | InstallationConflictFlags.FrameLimit | InstallationConflictFlags.UnknownOriginalFile,
            "Conflict Flags: DxvkVersion, Architecture, FrameLimit, UnknownOriginalFile")]
        [InlineData((InstallationConflictFlags)16, "Conflict Flags: Unknown")]
        [InlineData((InstallationConflictFlags)32, "Conflict Flags: Unknown")]
        [InlineData((InstallationConflictFlags)(1 | 16), "Conflict Flags: Unknown")]
        [InlineData((InstallationConflictFlags)(-1), "Conflict Flags: Unknown")]
        public void Generate_ConflictFlags_RendersOrderedOrUnknown(InstallationConflictFlags flags, string expectedLine)
        {
            var installation = new GameInstallation { ConflictFlags = flags };
            string report = DiagnosticReportGenerator.Generate(null, installation, "1.0.0");
            Assert.Contains(expectedLine, report);
        }

        [Fact]
        public void Generate_DirtyModelsWithSensitiveData_NeverLeaksIdentifyingValues()
        {
            var profile = new GameProfile(@"D:\CustomInstall\ConfidentialLocation\SecretGameFolder\SecretGameExe.exe")
            {
                ExeName = "SecretGameExe.exe",
                Api = GraphicsApi.DX11,
                Architecture = "x64",
                DxvkVersion = "2.5"
            };

            var installation = new GameInstallation
            {
                Id = "guid-sensitive-identifier-99999",
                InstallationPath = @"D:\SteamLibrary\steamapps\common\TopSecretGame",
                DisplayName = "Top Secret Game Deluxe Edition",
                ManagedDxvkVersion = "2.5",
                RestorationState = RestorationState.Managed,
                ConflictFlags = InstallationConflictFlags.None,
                LastRefusalReason = "Target executable 'SecretGameExe.exe' at 'D:\\CustomInstall\\ConfidentialLocation' deployment refused.",
                ManagementPolicy = ManagementPolicy.UseGlobal()
            };

            string dirtyAppVersion = "D:\\CustomInstall\\ConfidentialLocation\\version.txt";

            string report = DiagnosticReportGenerator.Generate(profile, installation, dirtyAppVersion);

            Assert.DoesNotContain("ConfidentialLocation", report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SecretGame", report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TopSecret", report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("guid-sensitive", report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SteamLibrary", report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("steamapps", report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"D:\", report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".exe", report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("refused", report, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("App Version: Unavailable", report);
        }

        [Fact]
        public void Generate_Repeatability_ProducesIdenticalOutput()
        {
            var profile = new GameProfile(@"C:\Games\Game\game.exe")
            {
                Api = GraphicsApi.DX10,
                Architecture = "x86"
            };
            var installation = new GameInstallation
            {
                RestorationState = RestorationState.Restored,
                ManagedDxvkVersion = "2.3",
                ConflictFlags = InstallationConflictFlags.FrameLimit,
                ManagementPolicy = ManagementPolicy.Disabled()
            };

            string report1 = DiagnosticReportGenerator.Generate(profile, installation, "1.0.0");
            string report2 = DiagnosticReportGenerator.Generate(profile, installation, "1.0.0");

            Assert.Equal(report1, report2);
        }

        [Fact]
        public void Generate_NoInputMutation_PreservesModelProperties()
        {
            var profile = new GameProfile(@"C:\Games\Game\game.exe")
            {
                Api = GraphicsApi.DX11,
                Architecture = "x64",
                DxvkVersion = "2.5",
                HudEnabled = true,
                FrameLimit = 60
            };
            var installation = new GameInstallation
            {
                Id = "stable-id",
                InstallationPath = @"C:\Games\Game",
                DisplayName = "Game",
                RestorationState = RestorationState.Managed,
                ManagedDxvkVersion = "2.5",
                ConflictFlags = InstallationConflictFlags.None,
                ManagementPolicy = ManagementPolicy.Automatic(),
                LastRefusalReason = "some refusal"
            };

            DiagnosticReportGenerator.Generate(profile, installation, "1.0.0");

            Assert.Equal(@"C:\Games\Game\game.exe", profile.ExePath);
            Assert.Equal(GraphicsApi.DX11, profile.Api);
            Assert.Equal("x64", profile.Architecture);
            Assert.Equal("2.5", profile.DxvkVersion);
            Assert.True(profile.HudEnabled);
            Assert.Equal(60, profile.FrameLimit);

            Assert.Equal("stable-id", installation.Id);
            Assert.Equal(@"C:\Games\Game", installation.InstallationPath);
            Assert.Equal("Game", installation.DisplayName);
            Assert.Equal(RestorationState.Managed, installation.RestorationState);
            Assert.Equal("2.5", installation.ManagedDxvkVersion);
            Assert.Equal(InstallationConflictFlags.None, installation.ConflictFlags);
            Assert.Equal(ManagementMode.Automatic, installation.ManagementPolicy.Mode);
            Assert.Equal("some refusal", installation.LastRefusalReason);
        }
    }
}
