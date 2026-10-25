using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class ProcessLifecycleTrialPreparationTests
{
    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private static void AssertHostedCiEnvironment()
    {
        var isCi = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        if (!string.Equals(isCi, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Host workstation execution is strictly prohibited. ProcessLifecycleTrialPreparationTests may only run in approved hosted GitHub Actions CI.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (current != null)
        {
            var targetFile = Path.Combine(current.FullName, "tools", "validation", "ProcessLifecycleTrial.ps1");
            if (File.Exists(targetFile))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new FileNotFoundException("Could not find repository root containing tools/validation/ProcessLifecycleTrial.ps1");
    }

    private static string GetPowerShellExecutablePath()
    {
        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershellPath = Path.Combine(systemDir, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(powershellPath))
        {
            return powershellPath;
        }
        return "powershell.exe";
    }

    private static ProcessResult RunScript(string arguments, int timeoutMilliseconds = 30000)
    {
        AssertHostedCiEnvironment();

        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        Assert.True(File.Exists(scriptPath), $"Script not found at: {scriptPath}");

        var powershellPath = GetPowerShellExecutablePath();
        Assert.True(File.Exists(powershellPath), $"PowerShell executable not found at: {powershellPath}");

        var psi = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" {arguments}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMilliseconds))
        {
            // Do NOT force-kill child process! Report timeout and retain residual process information.
            throw new TimeoutException($"PowerShell process timed out after {timeoutMilliseconds}ms for args: {arguments}. Residual process {process.Id} retained.");
        }

        Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 5000);
        return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static void AssertScriptSuccess(ProcessResult result, bool expected = true)
    {
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            Assert.Fail($"Expected JSON output with Success={expected}, but stdout was empty. Stderr: {result.StandardError}");
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            if (doc.RootElement.TryGetProperty("Success", out var successProp))
            {
                Assert.Equal(expected, successProp.GetBoolean());
                return;
            }
        }
        catch (JsonException)
        {
        }

        var pattern = expected ? "\"Success\"\\s*:\\s*true" : "\"Success\"\\s*:\\s*false";
        Assert.Matches(pattern, result.StandardOutput);
    }

    private static string ComputeSha256Hex(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CreateSyntheticZip(string zipFilePath, IDictionary<string, byte[]> entries, Action<ZipArchive>? customMutator = null)
    {
        var dir = Path.GetDirectoryName(zipFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fileStream = new FileStream(zipFilePath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

        foreach (var (entryName, entryBytes) in entries)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            using var entryStream = entry.Open();
            entryStream.Write(entryBytes, 0, entryBytes.Length);
        }

        customMutator?.Invoke(archive);
        return zipFilePath;
    }

    [Fact]
    public async Task Script_Exists_And_ParsesWithoutSyntaxErrors()
    {
        AssertHostedCiEnvironment();

        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        Assert.True(File.Exists(scriptPath));

        var powershellPath = GetPowerShellExecutablePath();
        var psi = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$errs = $null; [System.Management.Automation.Language.Parser]::ParseFile('{scriptPath.Replace("'", "''")}', [ref]$null, [ref]$errs); if ($errs.Count -gt 0) {{ $errs | ForEach-Object {{ Write-Error $_ }}; exit 1 }} else {{ Write-Output 'PARSED_OK' }}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot
        };

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        bool exited = proc.WaitForExit(10000);
        Assert.True(exited, "AST parser process timed out after 10000ms.");
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.Equal(0, proc.ExitCode);
        Assert.Contains("PARSED_OK", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public void Script_FrozenPins_MatchPublishedConstants()
    {
        AssertHostedCiEnvironment();

        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        var text = File.ReadAllText(scriptPath);

        Assert.Contains("f75ec55738e6ca3c1a0cd0a342aefeaa0fb3addd03538f03f2ac7a43295750de", text);
        Assert.Contains("f720cecae0f13eeef6d9b5e2d259b51df97b37bc3347b241071e25e8c1548d70", text);
        Assert.Contains("19229f59a4ab2504634cf9cd1bfff131a89a68e539808ab68c5459451e6ce0cc", text);
        Assert.Contains("32660fd3e3ed87385e47940d490db2e6ab93daad7e993fe4225dd157232b3b14", text);
        Assert.Contains("c9b46817bc60e89b3848ecf2705cb929b718cf950689a15071c65933d2a0537d", text);
        Assert.Contains("25012c67ade281e0d4585282bf485e4783a1f7c486018f8d1a038c5f2dc18ca4", text);
        Assert.Contains("40565b4a724aadc4433fa4e010b4b23916d9b1f1baeee64e17186db94f54e608", text);
        Assert.Contains("30f9cc326874be344285582275446968cfa4c069db31ce56df312d6644179154", text);
    }

    [Fact]
    public void SafeZipArchive_ValidArchive_PassesValidation()
    {
        using var temp = new SyntheticTestDirectory();
        var zipPath = temp.GetPath("valid.zip");
        CreateSyntheticZip(zipPath, new Dictionary<string, byte[]>
        {
            { "file1.txt", Encoding.ASCII.GetBytes("hello") },
            { "sub/file2.txt", Encoding.ASCII.GetBytes("world") }
        });

        var result = RunScript($"-Action Test-SafeZipArchive -ZipPath \"{zipPath}\" -JsonOutput");
        Assert.Equal(0, result.ExitCode);
        AssertScriptSuccess(result, true);
    }

    [Fact]
    public void SafeZipArchive_ZipSlipTraversal_IsRejected()
    {
        using var temp = new SyntheticTestDirectory();
        var zipPath = temp.GetPath("slip.zip");
        CreateSyntheticZip(zipPath, new Dictionary<string, byte[]>
        {
            { "../slip.txt", Encoding.ASCII.GetBytes("malicious") }
        });

        var result = RunScript($"-Action Test-SafeZipArchive -ZipPath \"{zipPath}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("traversal", result.StandardOutput);
    }

    [Fact]
    public void SafeZipArchive_RootRelativePath_IsRejected()
    {
        using var temp = new SyntheticTestDirectory();
        var zipPath = temp.GetPath("rootrel.zip");
        CreateSyntheticZip(zipPath, new Dictionary<string, byte[]>
        {
            { "/escaped.txt", Encoding.ASCII.GetBytes("root") }
        });

        var result = RunScript($"-Action Test-SafeZipArchive -ZipPath \"{zipPath}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("root-relative", result.StandardOutput);
    }

    [Fact]
    public void SafeZipArchive_SymlinkEntry_IsRejected()
    {
        using var temp = new SyntheticTestDirectory();
        var zipPath = temp.GetPath("symlink.zip");
        CreateSyntheticZip(zipPath, new Dictionary<string, byte[]>
        {
            { "normal.txt", Encoding.ASCII.GetBytes("hello") }
        }, archive =>
        {
            var symlinkEntry = archive.CreateEntry("link.txt");
            // Set Unix mode to S_IFLNK (0xA000) in high word of ExternalAttributes
            symlinkEntry.ExternalAttributes = 0xA1ED << 16;
            using var s = symlinkEntry.Open();
            s.Write(Encoding.ASCII.GetBytes("target.txt"));
        });

        var result = RunScript($"-Action Test-SafeZipArchive -ZipPath \"{zipPath}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("symbolic link", result.StandardOutput);
    }

    [Fact]
    public void SafeZipArchive_WindowsTrailingAlias_IsRejected()
    {
        using var temp = new SyntheticTestDirectory();
        var zipPath = temp.GetPath("alias.zip");
        CreateSyntheticZip(zipPath, new Dictionary<string, byte[]>
        {
            { "badname.txt ", Encoding.ASCII.GetBytes("aliased") }
        });

        var result = RunScript($"-Action Test-SafeZipArchive -ZipPath \"{zipPath}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("Windows-aliased", result.StandardOutput);
    }

    [Fact]
    public void SafeZipArchive_DuplicateEntries_IsRejected()
    {
        using var temp = new SyntheticTestDirectory();
        var zipPath = temp.GetPath("duplicate.zip");

        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e1 = archive.CreateEntry("collide.txt");
            using (var s = e1.Open()) s.Write(Encoding.ASCII.GetBytes("1"));
            var e2 = archive.CreateEntry("collide.txt");
            using (var s = e2.Open()) s.Write(Encoding.ASCII.GetBytes("2"));
        }

        var result = RunScript($"-Action Test-SafeZipArchive -ZipPath \"{zipPath}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("duplicate/colliding", result.StandardOutput);
    }

    [Fact]
    public void PathContainment_SiblingPrefixEscape_IsRejected()
    {
        using var temp = new SyntheticTestDirectory();
        var root = temp.GetPath("trial_root");
        Directory.CreateDirectory(root);
        var sibling = temp.GetPath("trial_root_other");
        Directory.CreateDirectory(sibling);

        var result = RunScript($"-Action Test-PathContainment -PathA \"{root}\" -PathB \"{sibling}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
    }

    [Fact]
    public void PathContainment_ContainedPath_Succeeds()
    {
        using var temp = new SyntheticTestDirectory();
        var root = temp.GetPath("base_root");
        Directory.CreateDirectory(root);
        var contained = Path.Combine(root, "subdir", "child.txt");

        var result = RunScript($"-Action Test-PathContainment -PathA \"{root}\" -PathB \"{contained}\" -JsonOutput");
        Assert.Equal(0, result.ExitCode);
        AssertScriptSuccess(result, true);
    }

    [Fact]
    public void CheckpointName_PathTraversal_IsRejected()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        var result = RunScript($"-Action Take-Snapshot -FixtureRoot \"{root}\" -CheckpointName \"..\\escape\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Invalid checkpoint name", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void Setup_RefusesPreExistingEmptyRoot()
    {
        using var temp = new SyntheticTestDirectory();
        var root = temp.GetPath("existing_empty_root");
        Directory.CreateDirectory(root);

        var helperZip = temp.GetPath("dummy_helper.zip");
        CreateSyntheticZip(helperZip, new Dictionary<string, byte[]> { { "QueueFixture.exe", Encoding.ASCII.GetBytes("dummy") } });
        var compZip = temp.GetPath("dummy_comp.zip");
        CreateSyntheticZip(compZip, new Dictionary<string, byte[]> { { "DXVK-Companion.exe", Encoding.ASCII.GetBytes("dummy") } });

        var result = RunScript($"-Action Setup -FixtureRoot \"{root}\" -HelperZipPath \"{helperZip}\" -CompanionZipPath \"{compZip}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("already exists", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void Setup_RefusesMismatchedArchiveHash()
    {
        using var temp = new SyntheticTestDirectory();
        var root = temp.GetPath("fresh_root");

        var helperZip = temp.GetPath("dummy_helper.zip");
        CreateSyntheticZip(helperZip, new Dictionary<string, byte[]> { { "QueueFixture.exe", Encoding.ASCII.GetBytes("dummy") } });
        var compZip = temp.GetPath("dummy_comp.zip");
        CreateSyntheticZip(compZip, new Dictionary<string, byte[]> { { "DXVK-Companion.exe", Encoding.ASCII.GetBytes("dummy") } });

        var result = RunScript($"-Action Setup -FixtureRoot \"{root}\" -HelperZipPath \"{helperZip}\" -CompanionZipPath \"{compZip}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("mismatch", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void PositiveSetup_RealStores_LoadSeededInstallationAndSettings()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        var settingsPath = Path.Combine(root, "companion", "settings.json");
        var libraryPath = Path.Combine(root, "companion", "Profiles", "game-library.json");
        var backupsDir = Path.Combine(root, "companion", "Profiles", "Backups");

        // Verify with real SettingsStore
        var settings = SettingsStore.Load(settingsPath);
        Assert.Equal(GlobalManagementPolicy.Manual, settings.GlobalPolicy);
        Assert.False(settings.LaunchOnStartup);
        Assert.False(settings.AutoEnableDxvkForNewGames);

        // Verify with real GameLibraryStore
        var store = new GameLibraryStore(libraryPath, backupsDir);
        var installations = store.GetAll();
        Assert.Single(installations);

        var target = installations.First();
        Assert.Equal("QueueFixture Trial Target", target.DisplayName);
        Assert.Equal(ManagementMode.UseGlobal, target.ManagementPolicy.Mode);
        Assert.False(target.ManagementPolicy.IsAutomated(settings.GlobalPolicy));
        Assert.Null(target.ManagedDxvkVersion);
        Assert.Equal("x64", target.ManagedDxvkArchitecture);
        Assert.Equal(RestorationState.None, target.RestorationState);
        Assert.Equal(InstallationConflictFlags.None, target.ConflictFlags);
        Assert.False(target.Configuration.HudEnabled);
        Assert.Equal(0, target.Configuration.FrameLimit);
        Assert.False(target.Configuration.FrameLimitEnabled);

        Assert.Single(target.Executables);
        var exe = target.Executables[0];
        Assert.Equal("QueueFixture.exe", exe.RelativePath);
        Assert.Equal(GraphicsApi.DX11, exe.LastKnownApi);
        Assert.Equal("x64", exe.LastKnownArchitecture);
        Assert.Empty(target.ManagedFiles);
        Assert.Null(target.PendingAction);

        // Verify baseline manifest asserting absent dxgi
        var manifestPath = Path.Combine(root, "baseline", "baseline_manifest.json");
        Assert.True(File.Exists(manifestPath));
        var manifestText = File.ReadAllText(manifestPath);
        Assert.Matches("\"DxgiAbsent\"\\s*:\\s*true", manifestText);

        // Verify games.json was also seeded
        var gamesJsonPath = Path.Combine(root, "companion", "Profiles", "games.json");
        Assert.True(File.Exists(gamesJsonPath));
    }

    [Fact]
    public void BatchGuard_MissingAdoptionAttestation_RefusesWithExactReason()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpIsolatedState(temp, testPinsUsed: false);

        var result = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"WRONG_TOKEN\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("missing or invalid adoption attestation token", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void BatchGuard_TestPinsUsed_RefusesWithExactReason()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpIsolatedState(temp, testPinsUsed: true);

        var result = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("fixture root was initialized with test pins and cannot be launched", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void BatchGuard_PreExistingStopMarker_RefusesWithExactReason()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpIsolatedState(temp, testPinsUsed: false);

        var marker = Path.Combine(root, "control", "stop-1.request");
        File.WriteAllText(marker, "STOP\n");

        var result = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Stop marker already exists: stop-1.request. Startup refused.", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void BatchGuard_MaxHelperStartsExhausted_RefusesWithExactReason()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpIsolatedState(temp, testPinsUsed: false, helperStartAttempts: 4);

        var result = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Maximum helper start attempt count (4) reached.", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void BatchGuard_MaxCompanionStartsExhausted_RefusesWithExactReason()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpIsolatedState(temp, testPinsUsed: false, companionStartAttempts: 8);

        var result = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -NoAutoDiscovery -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Maximum Companion start attempt count (8) reached.", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void BatchGuard_SlotReused_RefusesWithExactReason()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpIsolatedState(temp, testPinsUsed: false, usedSlots: new[] { 1 });

        var result = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Slot 1 has already been used in this batch. Slot reuse is prohibited.", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void BatchGuard_BatchEnded_RefusesWithExactReason()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpIsolatedState(temp, testPinsUsed: false, isEnded: true);

        var result = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Batch is marked ended. Further starts are refused.", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void BatchGuard_PriorCheckpointFailed_RefusesWithExactReason()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpIsolatedState(temp, testPinsUsed: false, lastOutcome: "Failed");

        var result = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Prior checkpoint failed. Further starts are refused.", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void TakeSnapshot_CapturesFilesAndManifest()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        var result = RunScript($"-Action Take-Snapshot -FixtureRoot \"{root}\" -CheckpointName \"Checkpoint0-Baseline\" -JsonOutput");
        Assert.Equal(0, result.ExitCode);

        var snapshotManifest = Path.Combine(root, "snapshots", "Checkpoint0-Baseline", "manifest.json");
        Assert.True(File.Exists(snapshotManifest));
        var manifestText = File.ReadAllText(snapshotManifest);
        Assert.Contains("Checkpoint0-Baseline", manifestText);
        Assert.Contains("QueueFixture.exe", manifestText);
    }

    [Fact]
    public void SemanticJson_IdentifiesEquivalentObjects_RegardlessOfKeyOrder()
    {
        using var temp = new SyntheticTestDirectory();
        var jsonA = temp.CreateFile("a.json", @"{
  ""SchemaVersion"": 1,
  ""Installations"": [
    {
      ""Id"": ""test-inst"",
      ""DisplayName"": ""Game 1"",
      ""InstallationPath"": ""C:\\Games\\Game1"",
      ""ManagementPolicy"": { ""Mode"": ""UseGlobal"", ""PinnedDxvkVersion"": null },
      ""ManagedDxvkVersion"": null,
      ""ManagedDxvkArchitecture"": ""x64"",
      ""RestorationState"": ""None"",
      ""ConflictFlags"": ""None"",
      ""PendingAction"": null
    }
  ]
}");

        var jsonB = temp.CreateFile("b.json", @"{
  ""Installations"": [
    {
      ""PendingAction"": null,
      ""RestorationState"": ""None"",
      ""ManagedDxvkArchitecture"": ""x64"",
      ""ManagedDxvkVersion"": null,
      ""ManagementPolicy"": { ""PinnedDxvkVersion"": null, ""Mode"": ""UseGlobal"" },
      ""InstallationPath"": ""C:/Games/Game1"",
      ""DisplayName"": ""Game 1"",
      ""Id"": ""test-inst"",
      ""ConflictFlags"": ""None""
    }
  ],
  ""SchemaVersion"": 1
}");

        var result = RunScript($"-Action Compare-SemanticLibraryJson -PathA \"{jsonA}\" -PathB \"{jsonB}\" -JsonOutput");
        Assert.Equal(0, result.ExitCode);
        AssertScriptSuccess(result, true);
    }

    [Fact]
    public void SemanticJson_DetectsFieldDifferences()
    {
        using var temp = new SyntheticTestDirectory();
        var jsonA = temp.CreateFile("a.json", @"{
  ""SchemaVersion"": 1,
  ""Installations"": [
    {
      ""Id"": ""test-inst"",
      ""DisplayName"": ""Game 1"",
      ""InstallationPath"": ""C:\\Games\\Game1"",
      ""ManagementPolicy"": { ""Mode"": ""UseGlobal"", ""PinnedDxvkVersion"": null },
      ""ManagedDxvkVersion"": ""v3.1"",
      ""ManagedDxvkArchitecture"": ""x64"",
      ""RestorationState"": ""None"",
      ""ConflictFlags"": ""None"",
      ""PendingAction"": null
    }
  ]
}");

        var jsonB = temp.CreateFile("b.json", @"{
  ""SchemaVersion"": 1,
  ""Installations"": [
    {
      ""Id"": ""test-inst"",
      ""DisplayName"": ""Game 1"",
      ""InstallationPath"": ""C:\\Games\\Game1"",
      ""ManagementPolicy"": { ""Mode"": ""UseGlobal"", ""PinnedDxvkVersion"": null },
      ""ManagedDxvkVersion"": ""v3.1.1"",
      ""ManagedDxvkArchitecture"": ""x64"",
      ""RestorationState"": ""None"",
      ""ConflictFlags"": ""None"",
      ""PendingAction"": null
    }
  ]
}");

        var result = RunScript($"-Action Compare-SemanticLibraryJson -PathA \"{jsonA}\" -PathB \"{jsonB}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("ManagedDxvkVersion", result.StandardOutput);
    }

    [Fact]
    public void CompareTargetBaseline_DetectsMutationDuringPendingCheckpoint()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        // Target currently matches baseline
        var check1 = RunScript($"-Action Compare-TargetBaseline -FixtureRoot \"{root}\" -JsonOutput");
        Assert.Equal(0, check1.ExitCode);
        AssertScriptSuccess(check1, true);

        // Mutate target d3d11.dll
        File.AppendAllText(Path.Combine(root, "target", "d3d11.dll"), "MUTATION");

        var check2 = RunScript($"-Action Compare-TargetBaseline -FixtureRoot \"{root}\" -JsonOutput");
        Assert.NotEqual(0, check2.ExitCode);
        AssertScriptSuccess(check2, false);
        Assert.Contains("mismatch", check2.StandardOutput);
    }

    [Fact]
    public void VerifyCheckpoint_FailsOnLeftoverDxgi_AndPersistsFailedState()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        // Drop unexpected dxgi.dll in target
        File.WriteAllText(Path.Combine(root, "target", "dxgi.dll"), "LEFTOVER");

        var result = RunScript($"-Action Verify-Checkpoint -FixtureRoot \"{root}\" -CheckpointName \"Checkpoint7-Restored\" -ExpectedType \"Restored\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("dxgi.dll unexpectedly present", result.StandardOutput + result.StandardError);

        // Verify LastCheckpointOutcome was persisted as Failed in state.json
        var statePath = Path.Combine(root, "state.json");
        var stateJson = File.ReadAllText(statePath);
        Assert.Matches("\"LastCheckpointOutcome\"\\s*:\\s*\"Failed\"", stateJson);
    }

    private static string SetUpTestFixture(SyntheticTestDirectory temp)
    {
        var root = temp.GetPath("trial_root");

        var helperExeBytes = Encoding.ASCII.GetBytes("TEST_HELPER_EXE_BYTES");
        var helperExeHash = ComputeSha256Hex(helperExeBytes);
        var helperZip = temp.GetPath("helper.zip");
        CreateSyntheticZip(helperZip, new Dictionary<string, byte[]> { { "QueueFixture.exe", helperExeBytes } });
        var helperZipHash = ComputeSha256Hex(File.ReadAllBytes(helperZip));
        var helperZipSize = new FileInfo(helperZip).Length;
        var helperExeSize = helperExeBytes.Length;

        var compExeBytes = Encoding.ASCII.GetBytes("TEST_COMPANION_EXE_BYTES");
        var compExeHash = ComputeSha256Hex(compExeBytes);
        var compZip = temp.GetPath("companion.zip");
        CreateSyntheticZip(compZip, new Dictionary<string, byte[]> { { "DXVK-Companion.exe", compExeBytes } });
        var compZipHash = ComputeSha256Hex(File.ReadAllBytes(compZip));
        var compZipSize = new FileInfo(compZip).Length;
        var compExeSize = compExeBytes.Length;

        var testPinsJson = JsonSerializer.Serialize(new
        {
            HelperZipSize = helperZipSize,
            HelperZipSha256 = helperZipHash,
            HelperExeSize = helperExeSize,
            HelperExeSha256 = helperExeHash,
            CompanionZipSize = compZipSize,
            CompanionZipSha256 = compZipHash,
            CompanionExeSize = compExeSize,
            CompanionExeSha256 = compExeHash
        });

        var escapedJson = testPinsJson.Replace("\"", "\\\"");
        var result = RunScript($"-Action Setup -FixtureRoot \"{root}\" -HelperZipPath \"{helperZip}\" -CompanionZipPath \"{compZip}\" -TestPinsJson \"{escapedJson}\" -JsonOutput");
        Assert.Equal(0, result.ExitCode);

        return root;
    }

    private static string SetUpIsolatedState(
        SyntheticTestDirectory temp,
        bool testPinsUsed,
        int helperStartAttempts = 0,
        int companionStartAttempts = 0,
        int[]? usedSlots = null,
        bool isEnded = false,
        string lastOutcome = "Success")
    {
        var root = temp.GetPath("isolated_state_root");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "control"));

        var state = new
        {
            BatchStartTimeUtc = DateTime.UtcNow.ToString("o"),
            SetupElapsedMilliseconds = 100,
            HelperStartAttempts = helperStartAttempts,
            CompanionStartAttempts = companionStartAttempts,
            MaxHelperStarts = 4,
            MaxCompanionStarts = 8,
            MaxDurationSeconds = 7200,
            MaxFootprintBytes = 2147483648L,
            UsedSlots = usedSlots ?? Array.Empty<int>(),
            CurrentCheckpoint = "Setup",
            LastCheckpointOutcome = lastOutcome,
            LastFailedCheckpoint = (string?)null,
            IsEnded = isEnded,
            TestPinsUsed = testPinsUsed,
            ActiveHelperPid = (int?)null,
            ActiveCompanionPid = (int?)null,
            Checkpoints = new[] { "Setup" }
        };

        var statePath = Path.Combine(root, "state.json");
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        return root;
    }
}
