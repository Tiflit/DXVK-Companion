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
    private static readonly List<Process> s_retainedProcesses = new();

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

        var process = new Process { StartInfo = psi };
        try
        {
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMilliseconds))
            {
                lock (s_retainedProcesses)
                {
                    s_retainedProcesses.Add(process);
                }
                throw new TimeoutException($"PowerShell process timed out after {timeoutMilliseconds}ms for args: {arguments}. Residual process {process.Id} retained.");
            }

            var waitFinished = Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 5000);
            if (!waitFinished)
            {
                lock (s_retainedProcesses)
                {
                    s_retainedProcesses.Add(process);
                }
                throw new TimeoutException($"PowerShell stdout/stderr draining timed out after 5000ms for args: {arguments}. Residual process {process.Id} retained.");
            }

            return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }
        catch (TimeoutException)
        {
            // Process handle retained in s_retainedProcesses without disposing
            throw;
        }
        finally
        {
            lock (s_retainedProcesses)
            {
                if (!s_retainedProcesses.Contains(process))
                {
                    process.Dispose();
                }
            }
        }
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

        // Verify games.json was seeded as a valid ProfileStore payload
        var gamesJsonPath = Path.Combine(root, "companion", "Profiles", "games.json");
        Assert.True(File.Exists(gamesJsonPath));
        var profileStore = new ProfileStore(gamesJsonPath);
        Assert.False(profileStore.HasBrokenLoad, $"ProfileStore load should not be broken: {profileStore.BrokenLoadReason}");
        Assert.Null(profileStore.BrokenLoadReason);
        var profiles = profileStore.GetAll().ToList();
        Assert.Single(profiles);
        var p = profiles[0];
        Assert.Equal("QueueFixture.exe", p.ExeName);
        Assert.Equal(GraphicsApi.DX11, p.Api);
        Assert.Equal("x64", p.Architecture);
        Assert.False(p.DxvkEnabled);
        Assert.Null(p.DxvkVersion);
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

    [Fact]
    public void ProfileStore_LoadRejectsInvalidObjectSchema_WhenFileContainsLibraryObject()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var gamesPath = temp.CreateFile("games.json", @"{
  ""SchemaVersion"": 1,
  ""Installations"": []
}");

        var store = new ProfileStore(gamesPath);
        Assert.True(store.HasBrokenLoad);
        Assert.NotNull(store.BrokenLoadReason);
        Assert.Contains("corrupted JSON", store.BrokenLoadReason);
    }

    [Fact]
    public void CompareSemanticLibraryJson_DetectsModifiedManagedFilesField_WhenRecordCountsEqual()
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
      ""ManagedFiles"": [
        {
          ""RelativePath"": ""d3d11.dll"",
          ""OriginalState"": ""Existing"",
          ""BackupRelativePath"": ""Backups\\d3d11.dll.bak"",
          ""OriginalSha256"": ""orig_hash_1111"",
          ""ExpectedManagedSha256"": ""exp_hash_2222"",
          ""ManagedDxvkVersion"": ""v3.1"",
          ""OriginalDxvkVersion"": null,
          ""CurrentState"": ""Managed""
        }
      ],
      ""PendingAction"": null
    }
  ]
}");

        // In jsonB, BackupRelativePath has been modified despite same ManagedFiles count (1)
        var jsonB = temp.CreateFile("b.json", @"{
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
      ""ManagedFiles"": [
        {
          ""RelativePath"": ""d3d11.dll"",
          ""OriginalState"": ""Existing"",
          ""BackupRelativePath"": ""Backups\\tampered_backup.bak"",
          ""OriginalSha256"": ""orig_hash_1111"",
          ""ExpectedManagedSha256"": ""exp_hash_2222"",
          ""ManagedDxvkVersion"": ""v3.1"",
          ""OriginalDxvkVersion"": null,
          ""CurrentState"": ""Managed""
        }
      ],
      ""PendingAction"": null
    }
  ]
}");

        var result = RunScript($"-Action Compare-SemanticLibraryJson -PathA \"{jsonA}\" -PathB \"{jsonB}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("BackupRelativePath", result.StandardOutput);
    }

    [Fact]
    public void CompareSemanticLibraryJson_DetectsManagementPolicyPinnedVersionMismatch()
    {
        using var temp = new SyntheticTestDirectory();
        var jsonA = temp.CreateFile("a.json", @"{
  ""SchemaVersion"": 1,
  ""Installations"": [
    {
      ""Id"": ""test-inst"",
      ""DisplayName"": ""Game 1"",
      ""InstallationPath"": ""C:\\Games\\Game1"",
      ""ManagementPolicy"": { ""Mode"": ""PinnedVersion"", ""PinnedDxvkVersion"": ""v3.1"" },
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
      ""ManagementPolicy"": { ""Mode"": ""PinnedVersion"", ""PinnedDxvkVersion"": ""v3.1.1"" },
      ""ManagedDxvkVersion"": ""v3.1"",
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
        Assert.Contains("PinnedDxvkVersion mismatch", result.StandardOutput);
    }

    [Fact]
    public void CompareTargetBaseline_DetectsUnexpectedEmptyDirectory()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        // Add unexpected directory inside target
        Directory.CreateDirectory(Path.Combine(root, "target", "unexpected_subdir"));

        var result = RunScript($"-Action Compare-TargetBaseline -FixtureRoot \"{root}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("Unexpected directory present in target", result.StandardOutput);
    }

    [Fact]
    public void VerifyCheckpoint_Applied_RefusesWhenOfficialReferenceIsMissing()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        // Prepare target with DLLs, but official reference directory is absent
        File.WriteAllText(Path.Combine(root, "target", "dxgi.dll"), "TARGET_DXGI");
        File.WriteAllText(Path.Combine(root, "target", "d3d11.dll"), "TARGET_D3D11");

        // Attempt verification with ExpectedVersion v3.1 whose reference is not in reference/v3.1/x64
        var result = RunScript($"-Action Verify-Checkpoint -FixtureRoot \"{root}\" -CheckpointName \"Checkpoint3-Applied\" -ExpectedType \"Applied\" -ExpectedVersion \"v3.1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("official reference", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void VerifyCheckpoint_TerminalFailureLatching_PreventsSubsequentQualification()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        // Intentionally induce a checkpoint failure (e.g. unrecognized type)
        var failResult = RunScript($"-Action Verify-Checkpoint -FixtureRoot \"{root}\" -CheckpointName \"BadCheckpoint\" -ExpectedType \"NonExistentType\" -JsonOutput");
        Assert.NotEqual(0, failResult.ExitCode);

        // Verify state is Failed
        var stateJson = File.ReadAllText(Path.Combine(root, "state.json"));
        Assert.Matches("\"LastCheckpointOutcome\"\\s*:\\s*\"Failed\"", stateJson);

        // A subsequent valid checkpoint verification must be immediately refused due to terminal failure latch
        var secondResult = RunScript($"-Action Verify-Checkpoint -FixtureRoot \"{root}\" -CheckpointName \"SubsequentCheckpoint\" -ExpectedType \"Restored\" -JsonOutput");
        Assert.NotEqual(0, secondResult.ExitCode);
        Assert.Contains("previously entered terminal failure", secondResult.StandardOutput + secondResult.StandardError);
    }

    [Fact]
    public void TakeSnapshot_TerminalFailureLatching_RefusesSnapshot()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        // Set state to Failed
        var statePath = Path.Combine(root, "state.json");
        var stateObj = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(statePath))!;
        stateObj["LastCheckpointOutcome"] = "Failed";
        stateObj["LastFailedCheckpoint"] = "PriorFailure";
        File.WriteAllText(statePath, JsonSerializer.Serialize(stateObj, new JsonSerializerOptions { WriteIndented = true }));

        var result = RunScript($"-Action Take-Snapshot -FixtureRoot \"{root}\" -CheckpointName \"Snap1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("terminal failure state", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void SafeProcessStreamDrainer_ShippedDrainer_StreamsAndDetectsTokens()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var logFile = temp.GetPath("test_output.log");
        var text = $"Initializing QueueFixture{Environment.NewLine}READY slot 1{Environment.NewLine}Processing payload{Environment.NewLine}STOPPED slot 1{Environment.NewLine}";
        var inputFile = temp.CreateFile("input.txt", text);

        var result = RunScript($"-Action Test-Drainer -TestInputPath \"{inputFile}\" -LogPath \"{logFile}\" -JsonOutput");
        Assert.Equal(0, result.ExitCode);
        AssertScriptSuccess(result, true);

        using var doc = JsonDocument.Parse(result.StandardOutput);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ReadyReceived").GetBoolean());
        Assert.True(root.GetProperty("StoppedReceived").GetBoolean());
        Assert.True(root.GetProperty("IsCompleted").GetBoolean());
        Assert.False(root.GetProperty("HasFault").GetBoolean());
        Assert.True(root.GetProperty("DrainedSuccessfully").GetBoolean());
        Assert.Equal(4, root.GetProperty("LinesRead").GetInt32());

        Assert.True(File.Exists(logFile));
        var written = File.ReadAllText(logFile);
        Assert.Contains("READY slot 1", written);
        Assert.Contains("STOPPED slot 1", written);
    }

    [Fact]
    public void SafeProcessStreamDrainer_ShippedDrainer_DetectsAndExposesFault()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var logFile = temp.GetPath("test_output.log");

        var result = RunScript($"-Action Test-Drainer -SimulateFault -LogPath \"{logFile}\" -JsonOutput");
        Assert.Equal(0, result.ExitCode);

        using var doc = JsonDocument.Parse(result.StandardOutput);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("HasFault").GetBoolean());
        Assert.False(root.GetProperty("DrainedSuccessfully").GetBoolean());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("FaultMessage").GetString()));
    }

    [Fact]
    public void SafeProcessStreamDrainer_ShippedDrainer_EnforcesMaxBytesCap()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var logFile = temp.GetPath("test_output.log");
        var longLine = new string('A', 500);
        var inputFile = temp.CreateFile("input.txt", longLine);

        var result = RunScript($"-Action Test-Drainer -TestInputPath \"{inputFile}\" -LogPath \"{logFile}\" -MaxBytes 100 -JsonOutput");
        Assert.Equal(0, result.ExitCode);

        using var doc = JsonDocument.Parse(result.StandardOutput);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("HasFault").GetBoolean());
        Assert.Contains("Maximum log stream size exceeded", root.GetProperty("FaultMessage").GetString());
    }

    [Fact]
    public void ParseTarListing_AcceptsValidBsdtarListing()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var listFile = temp.CreateFile("listing.txt", string.Join(Environment.NewLine, new[]
        {
            "-rw-r--r--  0 root   root  8000000 Jan  1  1970 dxvk-3.1/x64/d3d11.dll",
            "-rw-r--r--  0 root   root  2000000 Jan  1  1970 dxvk-3.1/x64/dxgi.dll"
        }));

        var result = RunScript($"-Action Test-ParseTarListing -ListingFilePath \"{listFile}\" -JsonOutput");
        Assert.Equal(0, result.ExitCode);
        AssertScriptSuccess(result, true);

        using var doc = JsonDocument.Parse(result.StandardOutput);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("EntryCount").GetInt32());
        Assert.Equal(10000000L, root.GetProperty("TotalBytes").GetInt64());
        Assert.True(root.GetProperty("HasD3d11").GetBoolean());
        Assert.True(root.GetProperty("HasDxgi").GetBoolean());
    }

    [Fact]
    public void ParseTarListing_RejectsMalformedOrUnknownSize()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var listFile = temp.CreateFile("listing.txt", string.Join(Environment.NewLine, new[]
        {
            "-rw-r--r--  0 root   root  NOT_A_NUMBER Jan  1  1970 dxvk-3.1/x64/d3d11.dll",
            "-rw-r--r--  0 root   root  2000000 Jan  1  1970 dxvk-3.1/x64/dxgi.dll"
        }));

        var result = RunScript($"-Action Test-ParseTarListing -ListingFilePath \"{listFile}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("Malformed or unknown entry size in tar listing", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void ParseTarListing_RejectsForbiddenEntryType()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var listFile = temp.CreateFile("listing.txt", string.Join(Environment.NewLine, new[]
        {
            "lrwxrwxrwx  0 root   root       12 Jan  1  1970 dxvk-3.1/x64/d3d11.dll -> /etc/passwd",
            "-rw-r--r--  0 root   root  2000000 Jan  1  1970 dxvk-3.1/x64/dxgi.dll"
        }));

        var result = RunScript($"-Action Test-ParseTarListing -ListingFilePath \"{listFile}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("forbidden entry type", result.StandardOutput + result.StandardError);

        using var doc = JsonDocument.Parse(result.StandardOutput);
        Assert.Contains("forbidden entry type 'l'", doc.RootElement.GetProperty("Message").GetString()!);
    }

    [Fact]
    public void ParseTarListing_RejectsDuplicateEntry()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var listFile = temp.CreateFile("listing.txt", string.Join(Environment.NewLine, new[]
        {
            "-rw-r--r--  0 root   root  8000000 Jan  1  1970 dxvk-3.1/x64/d3d11.dll",
            "-rw-r--r--  0 root   root  8000000 Jan  1  1970 ./dxvk-3.1/x64/d3d11.dll",
            "-rw-r--r--  0 root   root  2000000 Jan  1  1970 dxvk-3.1/x64/dxgi.dll"
        }));

        var result = RunScript($"-Action Test-ParseTarListing -ListingFilePath \"{listFile}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("duplicate or colliding entry", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void ParseTarListing_RejectsOverLimitTotalBytes()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var listFile = temp.CreateFile("listing.txt", string.Join(Environment.NewLine, new[]
        {
            "-rw-r--r--  0 root   root  105000000 Jan  1  1970 dxvk-3.1/x64/d3d11.dll",
            "-rw-r--r--  0 root   root    2000000 Jan  1  1970 dxvk-3.1/x64/dxgi.dll"
        }));

        var result = RunScript($"-Action Test-ParseTarListing -ListingFilePath \"{listFile}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("exceeds limit", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void ExtractCompanionProvenance_ValidZip_SucceedsWithHeadSha()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var provZip = temp.GetPath("provenance.zip");
        var destDir = temp.GetPath("prov_dest");

        var provJson = JsonSerializer.Serialize(new
        {
            head_sha = "abc12345def67890abc12345def67890abc12345",
            run_id = "777888",
            run_attempt = "1"
        });
        CreateSyntheticZip(provZip, new Dictionary<string, byte[]>
        {
            { "build-provenance.json", Encoding.UTF8.GetBytes(provJson) }
        });
        var provZipHash = ComputeSha256Hex(File.ReadAllBytes(provZip));
        var provZipSize = new FileInfo(provZip).Length;

        var pins = JsonSerializer.Serialize(new
        {
            CompanionProvenanceSize = provZipSize,
            CompanionProvenanceSha256 = provZipHash,
            CompanionCommit = "abc12345def67890abc12345def67890abc12345",
            CompanionRunId = "777888"
        });
        var escapedPins = pins.Replace("\"", "\\\"");

        var result = RunScript($"-Action Test-Provenance -PathA \"{provZip}\" -PathB \"{destDir}\" -TestPinsJson \"{escapedPins}\" -JsonOutput");
        Assert.Equal(0, result.ExitCode);
        AssertScriptSuccess(result, true);
        Assert.True(File.Exists(Path.Combine(destDir, "build-provenance.json")));
    }

    [Fact]
    public void ExtractCompanionProvenance_WrongSourceSha_Throws()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var provZip = temp.GetPath("provenance.zip");
        var destDir = temp.GetPath("prov_dest");

        var provJson = JsonSerializer.Serialize(new
        {
            head_sha = "WRONG_HEAD_SHA_00000000000000000000000000",
            run_id = "777888"
        });
        CreateSyntheticZip(provZip, new Dictionary<string, byte[]>
        {
            { "build-provenance.json", Encoding.UTF8.GetBytes(provJson) }
        });
        var provZipHash = ComputeSha256Hex(File.ReadAllBytes(provZip));
        var provZipSize = new FileInfo(provZip).Length;

        var pins = JsonSerializer.Serialize(new
        {
            CompanionProvenanceSize = provZipSize,
            CompanionProvenanceSha256 = provZipHash,
            CompanionCommit = "EXPECTED_COMMIT_1111111111111111111111111111111",
            CompanionRunId = "777888"
        });
        var escapedPins = pins.Replace("\"", "\\\"");

        var result = RunScript($"-Action Test-Provenance -PathA \"{provZip}\" -PathB \"{destDir}\" -TestPinsJson \"{escapedPins}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("Companion provenance commit mismatch", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void ExtractCompanionProvenance_MissingManifest_Throws()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var provZip = temp.GetPath("provenance.zip");
        var destDir = temp.GetPath("prov_dest");

        CreateSyntheticZip(provZip, new Dictionary<string, byte[]>
        {
            { "other_file.txt", Encoding.UTF8.GetBytes("not_provenance") }
        });
        var provZipHash = ComputeSha256Hex(File.ReadAllBytes(provZip));
        var provZipSize = new FileInfo(provZip).Length;

        var pins = JsonSerializer.Serialize(new
        {
            CompanionProvenanceSize = provZipSize,
            CompanionProvenanceSha256 = provZipHash
        });
        var escapedPins = pins.Replace("\"", "\\\"");

        var result = RunScript($"-Action Test-Provenance -PathA \"{provZip}\" -PathB \"{destDir}\" -TestPinsJson \"{escapedPins}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("does not contain build-provenance.json", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void Setup_HelperPackageProvenance_RejectsTamperedSourceSha()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = temp.GetPath("trial_root");

        var helperExeBytes = Encoding.ASCII.GetBytes("TEST_HELPER_EXE_BYTES");
        var helperExeHash = ComputeSha256Hex(helperExeBytes);
        var helperProvBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            SourcePath = "tools/validation/QueueFixture.cpp",
            SourceSha256 = "ACTUAL_TAMPERED_SHA",
            CheckoutCommit = "TEST_COMMIT",
            CheckoutTree = "TEST_TREE",
            GithubRunId = "12345",
            Files = new[]
            {
                new { Name = "QueueFixture.exe", Sha256 = helperExeHash, SizeBytes = (long)helperExeBytes.Length }
            }
        }));
        var helperZip = temp.GetPath("helper.zip");
        CreateSyntheticZip(helperZip, new Dictionary<string, byte[]>
        {
            { "QueueFixture.exe", helperExeBytes },
            { "provenance.json", helperProvBytes }
        });
        var helperZipHash = ComputeSha256Hex(File.ReadAllBytes(helperZip));
        var helperZipSize = new FileInfo(helperZip).Length;

        var compExeBytes = Encoding.ASCII.GetBytes("TEST_COMPANION_EXE_BYTES");
        var compZip = temp.GetPath("companion.zip");
        CreateSyntheticZip(compZip, new Dictionary<string, byte[]> { { "DXVK-Companion.exe", compExeBytes } });
        var compZipHash = ComputeSha256Hex(File.ReadAllBytes(compZip));
        var compZipSize = new FileInfo(compZip).Length;

        var testPinsJson = JsonSerializer.Serialize(new
        {
            HelperZipSize = helperZipSize,
            HelperZipSha256 = helperZipHash,
            HelperExeSize = helperExeBytes.Length,
            HelperExeSha256 = helperExeHash,
            HelperProvenance = new
            {
                SourceSha256 = "EXPECTED_UNMODIFIED_SHA",
                CheckoutCommit = "TEST_COMMIT",
                CheckoutTree = "TEST_TREE"
            },
            CompanionZipSize = compZipSize,
            CompanionZipSha256 = compZipHash,
            CompanionExeSize = compExeBytes.Length,
            CompanionExeSha256 = ComputeSha256Hex(compExeBytes)
        });
        var escapedJson = testPinsJson.Replace("\"", "\\\"");

        var result = RunScript($"-Action Setup -FixtureRoot \"{root}\" -HelperZipPath \"{helperZip}\" -CompanionZipPath \"{compZip}\" -TestPinsJson \"{escapedJson}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        AssertScriptSuccess(result, false);
        Assert.Contains("Helper provenance SourceSha256 mismatch", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void ObservationCycle_ExceedingDurationBudget_LatchesTerminalHold()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        var statePath = Path.Combine(root, "state.json");
        var stateObj = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(statePath))!;
        stateObj["TrialStartUtc"] = DateTime.UtcNow.AddSeconds(-8000).ToString("o");
        File.WriteAllText(statePath, JsonSerializer.Serialize(stateObj, new JsonSerializerOptions { WriteIndented = true }));

        var result = RunScript($"-Action Supervise -FixtureRoot \"{root}\" -Commands @(\"status\") -JsonOutput");
        Assert.Equal(0, result.ExitCode);

        var stateAfter = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(statePath))!;
        Assert.True(bool.Parse(stateAfter["TerminalHold"].ToString()!));
        Assert.Equal("Failed", stateAfter["LastCheckpointOutcome"].ToString());
        Assert.Equal("NotReady", stateAfter["CurrentReadiness"].ToString());
    }

    [Fact]
    public void Supervise_TerminalHold_RefusesNewActions_RetainsSupervision()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        var statePath = Path.Combine(root, "state.json");
        var stateObj = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(statePath))!;
        stateObj["TerminalHold"] = true;
        stateObj["TerminalHoldReason"] = "Simulated terminal hold for test";
        stateObj["LastCheckpointOutcome"] = "Failed";
        File.WriteAllText(statePath, JsonSerializer.Serialize(stateObj, new JsonSerializerOptions { WriteIndented = true }));

        var result = RunScript($"-Action Supervise -FixtureRoot \"{root}\" -Commands @(\"start-helper 1\") -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Supervisor is in terminal hold state", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void VerifyCheckpoint_PendingInstall_WithManagedTarget_AcceptsQueuedVersionUpdate()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        // Target currently has v3.1 DLLs
        var refDir = Path.Combine(root, "reference", "v3.1", "x64");
        Directory.CreateDirectory(refDir);
        var refD3d11 = Path.Combine(refDir, "d3d11.dll");
        var refDxgi = Path.Combine(refDir, "dxgi.dll");
        File.WriteAllText(refD3d11, "DXVK_31_D3D11_BYTES");
        File.WriteAllText(refDxgi, "DXVK_31_DXGI_BYTES");

        var targetDir = Path.Combine(root, "target");
        File.WriteAllText(Path.Combine(targetDir, "d3d11.dll"), "DXVK_31_D3D11_BYTES");
        File.WriteAllText(Path.Combine(targetDir, "dxgi.dll"), "DXVK_31_DXGI_BYTES");

        // Library store has ManagedDxvkVersion = "v3.1", and PendingAction Type="Install" targeting v3.1.1
        var libPath = Path.Combine(root, "companion", "Profiles", "game-library.json");
        var rootObj = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(libPath))!;
        var installations = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(rootObj["Installations"].ToString()!)!;
        var inst = installations[0];
        inst["ManagedDxvkVersion"] = "v3.1";
        inst["PendingAction"] = new Dictionary<string, object>
        {
            { "Type", "Install" },
            { "TargetDxvkVersion", "v3.1.1" },
            { "ScheduledUtc", DateTime.UtcNow.ToString("o") }
        };
        rootObj["Installations"] = installations;
        File.WriteAllText(libPath, JsonSerializer.Serialize(rootObj, new JsonSerializerOptions { WriteIndented = true }));

        // Run Verify-Checkpoint with PureDataCheck
        var result = RunScript($"-Action Verify-Checkpoint -FixtureRoot \"{root}\" -CheckpointName \"PendingUpdate\" -ExpectedType \"Pending\" -ExpectedVersion \"v3.1.1\" -PureDataCheck -JsonOutput");
        Assert.Equal(0, result.ExitCode);
        AssertScriptSuccess(result, true);
    }

    [Fact]
    public void VerifyCheckpoint_Applied_ResolvesBackupRelativeToBackupsRoot()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        // Prepare official reference v3.1
        var refDir = Path.Combine(root, "reference", "v3.1", "x64");
        Directory.CreateDirectory(refDir);
        File.WriteAllText(Path.Combine(refDir, "d3d11.dll"), "DXVK_31_D3D11_BYTES");
        File.WriteAllText(Path.Combine(refDir, "dxgi.dll"), "DXVK_31_DXGI_BYTES");

        // Target matching v3.1
        var targetDir = Path.Combine(root, "target");
        File.WriteAllText(Path.Combine(targetDir, "d3d11.dll"), "DXVK_31_D3D11_BYTES");
        File.WriteAllText(Path.Combine(targetDir, "dxgi.dll"), "DXVK_31_DXGI_BYTES");

        // Helper normal exit record
        var controlDir = Path.Combine(root, "control");
        var exitRecord = new
        {
            Slot = 1,
            Pid = 1234,
            ExitCode = 0,
            NormalStop = true,
            StoppedAtUtc = DateTime.UtcNow.ToString("o")
        };
        File.WriteAllText(Path.Combine(controlDir, "helper-slot-1.exit.json"), JsonSerializer.Serialize(exitRecord));

        // Native baseline hash from baseline/d3d11.dll
        var baselineD3d11Path = Path.Combine(root, "baseline", "d3d11.dll");
        var baselineHash = ComputeSha256Hex(File.ReadAllBytes(baselineD3d11Path));

        // Create native backup relative to companion/Profiles/Backups/
        var backupRel = Path.Combine("queue-fixture-trial-target", "d3d11.dll.bak");
        var backupFull = Path.Combine(root, "companion", "Profiles", "Backups", backupRel);
        Directory.CreateDirectory(Path.GetDirectoryName(backupFull)!);
        File.Copy(baselineD3d11Path, backupFull, true);

        // Update library store
        var libPath = Path.Combine(root, "companion", "Profiles", "game-library.json");
        var rootObj = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(libPath))!;
        var installations = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(rootObj["Installations"].ToString()!)!;
        var inst = installations[0];
        inst["ManagedDxvkVersion"] = "v3.1";
        inst["PendingAction"] = null!;
        inst["ManagedFiles"] = new List<object>
        {
            new
            {
                RelativePath = "d3d11.dll",
                OriginalState = "Existing",
                OriginalSha256 = baselineHash,
                BackupRelativePath = backupRel,
                ExpectedManagedSha256 = ComputeSha256Hex(Encoding.UTF8.GetBytes("DXVK_31_D3D11_BYTES")),
                ManagedDxvkVersion = "v3.1",
                OriginalDxvkVersion = (string?)null,
                CurrentState = "Managed"
            },
            new
            {
                RelativePath = "dxgi.dll",
                OriginalState = "Missing",
                OriginalSha256 = (string?)null,
                BackupRelativePath = (string?)null,
                ExpectedManagedSha256 = ComputeSha256Hex(Encoding.UTF8.GetBytes("DXVK_31_DXGI_BYTES")),
                ManagedDxvkVersion = "v3.1",
                OriginalDxvkVersion = (string?)null,
                CurrentState = "Managed"
            }
        };
        rootObj["Installations"] = installations;
        File.WriteAllText(libPath, JsonSerializer.Serialize(rootObj, new JsonSerializerOptions { WriteIndented = true }));

        // Update games.json
        var gamesPath = Path.Combine(root, "companion", "Profiles", "games.json");
        var gamesProfile = new[]
        {
            new
            {
                ExePath = Path.Combine(targetDir, "QueueFixture.exe"),
                ExeName = "QueueFixture.exe",
                Api = 3,
                Architecture = "x64",
                DxvkEnabled = true,
                DxvkVersion = "v3.1",
                HudEnabled = false,
                FrameLimit = 0
            }
        };
        File.WriteAllText(gamesPath, JsonSerializer.Serialize(gamesProfile));

        // Run Verify-Checkpoint
        var result = RunScript($"-Action Verify-Checkpoint -FixtureRoot \"{root}\" -CheckpointName \"Applied1\" -ExpectedType \"Applied\" -ExpectedVersion \"v3.1\" -Slot 1 -PureDataCheck -JsonOutput");
        Assert.Equal(0, result.ExitCode);
        AssertScriptSuccess(result, true);
    }

    [Fact]
    public void VerifyCheckpoint_GamesJson_RequiresSingleProfile()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        // games.json containing multiple profiles
        var gamesPath = Path.Combine(root, "companion", "Profiles", "games.json");
        var multiProfiles = new[]
        {
            new { ExeName = "QueueFixture.exe", DxvkEnabled = false, DxvkVersion = (string?)null },
            new { ExeName = "OtherGame.exe", DxvkEnabled = false, DxvkVersion = (string?)null }
        };
        File.WriteAllText(gamesPath, JsonSerializer.Serialize(multiProfiles));

        var result = RunScript($"-Action Verify-Checkpoint -FixtureRoot \"{root}\" -CheckpointName \"RestoredTest\" -ExpectedType \"Restored\" -PureDataCheck -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("expected exactly 1 profile in games.json", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void VerifyCheckpoint_Pending_RequiresLiveHelper_WhenNotPureDataCheck()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        // Library store with pending action
        var libPath = Path.Combine(root, "companion", "Profiles", "game-library.json");
        var rootObj = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(libPath))!;
        var installations = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(rootObj["Installations"].ToString()!)!;
        var inst = installations[0];
        inst["PendingAction"] = new Dictionary<string, object>
        {
            { "Type", "Install" },
            { "TargetDxvkVersion", "v3.1" },
            { "ScheduledUtc", DateTime.UtcNow.ToString("o") }
        };
        rootObj["Installations"] = installations;
        File.WriteAllText(libPath, JsonSerializer.Serialize(rootObj, new JsonSerializerOptions { WriteIndented = true }));

        // Calling Verify-Checkpoint WITHOUT -PureDataCheck when no helper process is running
        var result = RunScript($"-Action Verify-Checkpoint -FixtureRoot \"{root}\" -CheckpointName \"PendingOffline\" -ExpectedType \"Pending\" -ExpectedVersion \"v3.1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("live owned helper process handle is required", result.StandardOutput + result.StandardError);
    }

    private static string SetUpTestFixture(SyntheticTestDirectory temp)
    {
        var root = temp.GetPath("trial_root");

        var helperExeBytes = Encoding.ASCII.GetBytes("TEST_HELPER_EXE_BYTES");
        var helperExeHash = ComputeSha256Hex(helperExeBytes);
        var helperProvBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            SourcePath = "tools/validation/QueueFixture.cpp",
            SourceSha256 = "TEST_SRC_SHA",
            CheckoutCommit = "TEST_COMMIT",
            CheckoutTree = "TEST_TREE",
            GithubRunId = "12345",
            Files = new[]
            {
                new { Name = "QueueFixture.exe", Sha256 = helperExeHash, SizeBytes = (long)helperExeBytes.Length }
            }
        }));
        var helperZip = temp.GetPath("helper.zip");
        CreateSyntheticZip(helperZip, new Dictionary<string, byte[]>
        {
            { "QueueFixture.exe", helperExeBytes },
            { "provenance.json", helperProvBytes }
        });
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
            HelperProvenance = new
            {
                SourceSha256 = "TEST_SRC_SHA",
                CheckoutCommit = "TEST_COMMIT",
                CheckoutTree = "TEST_TREE"
            },
            CompanionZipSize = compZipSize,
            CompanionZipSha256 = compZipHash,
            CompanionExeSize = compExeSize,
            CompanionExeSha256 = compExeHash
        });

        var escapedJson = testPinsJson.Replace("\"", "\\\"");
        var result = RunScript($"-Action Setup -FixtureRoot \"{root}\" -HelperZipPath \"{helperZip}\" -CompanionZipPath \"{compZip}\" -TestPinsJson \"{escapedJson}\" -JsonOutput");
        Assert.True(result.ExitCode == 0, $"Setup failed with exit code {result.ExitCode}.\nSTDOUT: {result.StandardOutput}\nSTDERR: {result.StandardError}");

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
            TrialStartUtc = DateTime.UtcNow.ToString("o"),
            SetupElapsedMilliseconds = 100,
            ElapsedMilliseconds = 100,
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
            LastFailedReason = (string?)null,
            TerminalHold = false,
            TerminalHoldReason = (string?)null,
            CurrentReadiness = "Ready",
            ReadinessReason = "Ready",
            CurrentFootprintBytes = 1000L,
            ActiveProcesses = Array.Empty<object>(),
            AdoptedScriptSha256 = (string?)null,
            AdoptedToken = (string?)null,
            IsEnded = isEnded,
            TestPinsUsed = testPinsUsed,
            ActiveHelperPid = (int?)null,
            ActiveHelperSlot = (int?)null,
            ActiveCompanionPid = (int?)null,
            ActiveSupervisorPid = (int?)null,
            Checkpoints = new[] { "Setup" }
        };

        var statePath = Path.Combine(root, "state.json");
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        return root;
    }
}
