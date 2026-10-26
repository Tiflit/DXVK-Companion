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

[CollectionDefinition("ProcessLifecycleTrialSerialCollection", DisableParallelization = true)]
public sealed class ProcessLifecycleTrialSerialCollectionDefinition
{
}

[Collection("ProcessLifecycleTrialSerialCollection")]
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

        var result = RunScript($"-Action Supervise -FixtureRoot \"{root}\" -Commands status -JsonOutput");
        Assert.True(result.ExitCode == 0, $"Supervise status failed with exit code {result.ExitCode}.\nSTDOUT: {result.StandardOutput}\nSTDERR: {result.StandardError}");

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

        var result = RunScript($"-Action Supervise -FixtureRoot \"{root}\" -Commands \"start-helper 1\" -JsonOutput");
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
        Assert.True(result.ExitCode == 0, $"Verify-Checkpoint Pending failed with exit code {result.ExitCode}.\nSTDOUT: {result.StandardOutput}\nSTDERR: {result.StandardError}");
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
        Assert.True(result.ExitCode == 0, $"Verify-Checkpoint Applied failed with exit code {result.ExitCode}.\nSTDOUT: {result.StandardOutput}\nSTDERR: {result.StandardError}");
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

    // =========================================================================
    // B1 Regressions: Interactive Stdin Supervision Without PowerShell Runspace Fault
    // =========================================================================

    [Fact]
    public async Task Supervise_InteractiveMode_WithRedirectedStdin_ProcessesIdleObservationAndCommandsCleanly()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        var statePath = Path.Combine(root, "state.json");
        using var initialDoc = JsonDocument.Parse(File.ReadAllText(statePath));
        var initialElapsedMs = initialDoc.RootElement.GetProperty("ElapsedMilliseconds").GetInt64();

        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        var powershellPath = GetPowerShellExecutablePath();

        var psi = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Action Supervise -FixtureRoot \"{root}\" -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
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

            // Wait for Supervise to initialize control/supervisor-session.json, then write readiness.json bound to the live SupervisorPid and SupervisorSessionId
            var sessionPath = Path.Combine(root, "control", "supervisor-session.json");
            var swSession = Stopwatch.StartNew();
            while (!File.Exists(sessionPath) && swSession.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(50);
            }
            Assert.True(File.Exists(sessionPath), "Expected Supervise to create control/supervisor-session.json on startup.");
            using (var sessionDoc = JsonDocument.Parse(File.ReadAllText(sessionPath)))
            {
                var livePid = sessionDoc.RootElement.GetProperty("SupervisorPid").GetInt32();
                var liveSessionId = sessionDoc.RootElement.GetProperty("SupervisorSessionId").GetString()!;
                Assert.Equal(process.Id, livePid);
                Assert.False(string.IsNullOrWhiteSpace(liveSessionId));
                WriteReadinessRecord(
                    root,
                    supervisorPidOverride: livePid,
                    supervisorSessionIdOverride: liveSessionId,
                    bindSessionInState: false);
            }

            // Allow >600ms of idle interactive waiting so at least two 200ms PollReadLine observation cycles run on the owning runspace
            await Task.Delay(650);

            await process.StandardInput.WriteLineAsync("status");
            await process.StandardInput.FlushAsync();
            await Task.Delay(250);

            await process.StandardInput.WriteLineAsync("end-batch");
            await process.StandardInput.FlushAsync();
            await Task.Delay(150);

            await process.StandardInput.WriteLineAsync("exit");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();

            if (!process.WaitForExit(15000))
            {
                lock (s_retainedProcesses) { s_retainedProcesses.Add(process); }
                throw new TimeoutException($"Interactive Supervise timed out after 15000ms. Residual process {process.Id} retained.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.True(process.ExitCode == 0, $"Interactive Supervise failed with exit code {process.ExitCode}.\nSTDOUT: {stdout}\nSTDERR: {stderr}");
            Assert.DoesNotContain("Runspace", stdout + stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Entering interactive supervisor mode", stdout);
            Assert.Contains("[SUCCESS] Batch ended.", stdout);
            Assert.Contains("[SUCCESS] Interactive supervisor exited.", stdout);

            using var afterDoc = JsonDocument.Parse(File.ReadAllText(statePath));
            var afterRoot = afterDoc.RootElement;
            Assert.True(afterRoot.GetProperty("IsEnded").GetBoolean());
            Assert.Equal("Ready", afterRoot.GetProperty("CurrentReadiness").GetString());
            Assert.Equal("docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md", afterRoot.GetProperty("AdoptedProfilePath").GetString());
            Assert.Equal(GetCurrentProfileSha256(), afterRoot.GetProperty("AdoptedProfileSha256").GetString());
            Assert.Equal(GetCurrentScriptSha256(), afterRoot.GetProperty("AdoptedScriptSha256").GetString());
            Assert.Equal(process.Id, afterRoot.GetProperty("AdoptedSupervisorPid").GetInt32());
            Assert.Equal(JsonValueKind.Null, afterRoot.GetProperty("ActiveSupervisorPid").ValueKind);
            Assert.Equal(JsonValueKind.Null, afterRoot.GetProperty("ActiveSupervisorSessionId").ValueKind);
            Assert.Equal(0, afterRoot.GetProperty("HelperStartAttempts").GetInt32());
            Assert.Equal(0, afterRoot.GetProperty("CompanionStartAttempts").GetInt32());
            Assert.True(afterRoot.GetProperty("ElapsedMilliseconds").GetInt64() > initialElapsedMs,
                $"Expected monotonic ElapsedMilliseconds to advance during idle interactive observation (initial={initialElapsedMs}, after={afterRoot.GetProperty("ElapsedMilliseconds").GetInt64()}).");
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

    [Fact]
    public async Task Supervise_InteractiveMode_EofOnRedirectedStdin_ExitsCleanlyWithoutRunspaceFault()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        var powershellPath = GetPowerShellExecutablePath();

        var psi = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Action Supervise -FixtureRoot \"{root}\" -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot
        };

        var sessionFilePath = Path.Combine(root, "control", "supervisor-session.json");
        var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var sw = Stopwatch.StartNew();
            while (!File.Exists(sessionFilePath) && sw.ElapsedMilliseconds < 10000)
            {
                await Task.Delay(50);
            }
            Assert.True(File.Exists(sessionFilePath), "Expected Supervise to write control/supervisor-session.json on startup.");

            using (var sessionDoc = JsonDocument.Parse(File.ReadAllText(sessionFilePath)))
            {
                var livePid = sessionDoc.RootElement.GetProperty("SupervisorPid").GetInt32();
                var liveSessionId = sessionDoc.RootElement.GetProperty("SupervisorSessionId").GetString()!;
                WriteReadinessRecord(
                    root,
                    supervisorPidOverride: livePid,
                    supervisorSessionIdOverride: liveSessionId,
                    bindSessionInState: false);
            }

            // Wait for at least one idle PollReadLine observation interval, then close stdin (EOF)
            await Task.Delay(450);
            process.StandardInput.Close();

            if (!process.WaitForExit(15000))
            {
                lock (s_retainedProcesses) { s_retainedProcesses.Add(process); }
                throw new TimeoutException($"Interactive EOF Supervise timed out after 15000ms. Residual process {process.Id} retained.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.True(process.ExitCode == 0, $"Interactive EOF Supervise failed with exit code {process.ExitCode}.\nSTDOUT: {stdout}\nSTDERR: {stderr}");
            Assert.DoesNotContain("Runspace", stdout + stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("EOF detected on supervisor input.", stdout);
            Assert.Contains("[SUCCESS] Interactive supervisor exited.", stdout);

            var statePath = Path.Combine(root, "state.json");
            using var afterDoc = JsonDocument.Parse(File.ReadAllText(statePath));
            var afterRoot = afterDoc.RootElement;
            Assert.Equal(JsonValueKind.Null, afterRoot.GetProperty("ActiveSupervisorPid").ValueKind);
            Assert.Equal(JsonValueKind.Null, afterRoot.GetProperty("ActiveSupervisorSessionId").ValueKind);
            Assert.Equal(0, afterRoot.GetProperty("HelperStartAttempts").GetInt32());
            Assert.Equal(0, afterRoot.GetProperty("CompanionStartAttempts").GetInt32());
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

    // =========================================================================
    // B2 Regressions: Zero Force-Kill & Bounded Child Stream Draining / Retention
    // =========================================================================

    [Fact]
    public void Script_SourceInspection_ContainsZeroForceKillCalls()
    {
        AssertHostedCiEnvironment();

        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.DoesNotContain(".Kill(", scriptText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stop-Process", scriptText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taskkill", scriptText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoundedChildProcess_NormalCompletion_DrainsStdoutAndStderrToLogs()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var stdoutLog = temp.GetPath("child_stdout.log");
        var stderrLog = temp.GetPath("child_stderr.log");
        var cmdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        var result = RunScript(
            $"-Action Test-BoundedChild -ChildFileName \"{cmdPath}\" -ChildArguments \"/c echo CHILD_STDOUT_OK & echo CHILD_STDERR_OK 1>&2\" -LogPath \"{stdoutLog}\" -StderrLogPath \"{stderrLog}\" -TimeoutMs 5000 -JsonOutput");
        Assert.Equal(0, result.ExitCode);
        AssertScriptSuccess(result, true);

        using var doc = JsonDocument.Parse(result.StandardOutput);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("Completed").GetBoolean());
        Assert.False(root.GetProperty("TimedOut").GetBoolean());
        Assert.False(root.GetProperty("Retained").GetBoolean());
        Assert.Equal(0, root.GetProperty("RetainedProcessCount").GetInt32());
        Assert.Equal(0, root.GetProperty("ExitCode").GetInt32());
        Assert.True(root.GetProperty("StdoutDrained").GetBoolean());
        Assert.True(root.GetProperty("StderrDrained").GetBoolean());
        Assert.False(root.GetProperty("StdoutFault").GetBoolean());
        Assert.False(root.GetProperty("StderrFault").GetBoolean());

        Assert.True(File.Exists(stdoutLog));
        Assert.True(File.Exists(stderrLog));
        Assert.Contains("CHILD_STDOUT_OK", File.ReadAllText(stdoutLog));
        Assert.Contains("CHILD_STDERR_OK", File.ReadAllText(stderrLog));
    }

    [Fact]
    public async Task BoundedChildProcess_Timeout_RetainsProcessAndLogsWithoutKill()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var stdoutLog = temp.GetPath("timeout_stdout.log");
        var stderrLog = temp.GetPath("timeout_stderr.log");
        var residualPath = stdoutLog + ".residual.json";
        var releaseSignal = temp.GetPath("release_timeout.signal");
        var batchScript = temp.CreateFile("gated_child.cmd",
            "@echo off\r\n" +
            "echo BEFORE_TIMEOUT_OUT\r\n" +
            "echo BEFORE_TIMEOUT_ERR 1>&2\r\n" +
            ":waitloop\r\n" +
            $"if exist \"{releaseSignal}\" goto done\r\n" +
            "ping 127.0.0.1 -n 2 > nul\r\n" +
            "goto waitloop\r\n" +
            ":done\r\n" +
            "echo AFTER_TIMEOUT_OUT\r\n" +
            "echo AFTER_TIMEOUT_ERR 1>&2\r\n" +
            "exit /b 0\r\n");

        var cmdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        var powershellPath = GetPowerShellExecutablePath();

        var psi = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" -Action Test-BoundedChild -ChildFileName \"{cmdPath}\" -ChildArguments \"/c {batchScript}\" -TargetPath \"{temp.RootPath}\" -LogPath \"{stdoutLog}\" -StderrLogPath \"{stderrLog}\" -TimeoutMs 200 -JsonOutput",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot
        };

        var psProcess = new Process { StartInfo = psi };
        try
        {
            psProcess.Start();
            var stdoutTask = psProcess.StandardOutput.ReadToEndAsync();
            var stderrTask = psProcess.StandardError.ReadToEndAsync();

            // Wait for the 200ms timeout to fire and write the initial unsettled residual record
            var sw = Stopwatch.StartNew();
            while (!File.Exists(residualPath) && sw.ElapsedMilliseconds < 10000)
            {
                await Task.Delay(50);
            }
            Assert.True(File.Exists(residualPath), "Expected initial residual JSON record to be written upon child timeout.");

            using (var unsettledDoc = JsonDocument.Parse(File.ReadAllText(residualPath)))
            {
                Assert.True(unsettledDoc.RootElement.GetProperty("TimedOut").GetBoolean());
                Assert.False(unsettledDoc.RootElement.GetProperty("Settled").GetBoolean());
            }

            // Verify the owning PowerShell script remains alive while the timed-out child is still running
            await Task.Delay(350);
            Assert.False(psProcess.HasExited, "Owning PowerShell script must NOT exit while the timed-out child process is still running.");

            // Signal the child to emit late output and exit naturally
            File.WriteAllText(releaseSignal, "RELEASE");

            if (!psProcess.WaitForExit(15000))
            {
                lock (s_retainedProcesses) { s_retainedProcesses.Add(psProcess); }
                throw new TimeoutException($"Test-BoundedChild did not settle within 15000ms after child release. Residual process {psProcess.Id} retained.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            // Timeout must still be reported as a failure after settlement
            Assert.NotEqual(0, psProcess.ExitCode);
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            Assert.False(root.GetProperty("Success").GetBoolean());
            Assert.False(root.GetProperty("Completed").GetBoolean());
            Assert.True(root.GetProperty("TimedOut").GetBoolean());
            Assert.True(root.GetProperty("Retained").GetBoolean());
            Assert.Equal(1, root.GetProperty("RetainedProcessCount").GetInt32());
            Assert.True(root.GetProperty("ProcessId").GetInt32() > 0);

            // Both pre-timeout and post-timeout output must be drained and preserved
            var outText = File.ReadAllText(stdoutLog);
            var errText = File.ReadAllText(stderrLog);
            Assert.Contains("BEFORE_TIMEOUT_OUT", outText);
            Assert.Contains("AFTER_TIMEOUT_OUT", outText);
            Assert.Contains("BEFORE_TIMEOUT_ERR", errText);
            Assert.Contains("AFTER_TIMEOUT_ERR", errText);

            using var settledDoc = JsonDocument.Parse(File.ReadAllText(residualPath));
            var settledRoot = settledDoc.RootElement;
            Assert.True(settledRoot.GetProperty("Settled").GetBoolean());
            Assert.Equal(0, settledRoot.GetProperty("FinalExitCode").GetInt32());
            Assert.True(settledRoot.GetProperty("StdoutDrained").GetBoolean());
            Assert.True(settledRoot.GetProperty("StderrDrained").GetBoolean());
        }
        finally
        {
            if (!File.Exists(releaseSignal))
            {
                try { File.WriteAllText(releaseSignal, "RELEASE"); } catch { }
            }
            lock (s_retainedProcesses)
            {
                if (!s_retainedProcesses.Contains(psProcess))
                {
                    psProcess.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task Setup_ArchiveTimeout_RetainsOwningSupervisorUntilChildExits_DrainsLateOutputAndFailsClosed()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = temp.GetPath("setup_timeout_root");
        var releaseSignal = temp.GetPath("release_tar.signal");
        var batchScript = temp.CreateFile("gated_tar.cmd",
            "@echo off\r\n" +
            "echo TAR_STARTED_OUT\r\n" +
            "echo TAR_STARTED_ERR 1>&2\r\n" +
            ":waitloop\r\n" +
            $"if exist \"{releaseSignal}\" goto done\r\n" +
            "ping 127.0.0.1 -n 2 > nul\r\n" +
            "goto waitloop\r\n" +
            ":done\r\n" +
            "echo TAR_LATE_AFTER_TIMEOUT_OUT\r\n" +
            "echo TAR_LATE_AFTER_TIMEOUT_ERR 1>&2\r\n" +
            "exit /b 0\r\n");

        var helperExeBytes = Encoding.ASCII.GetBytes("TEST_HELPER_EXE_BYTES");
        var helperExeHash = ComputeSha256Hex(helperExeBytes);
        var helperProvBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            SourcePath = "tools/validation/QueueFixture.cpp",
            SourceSha256 = "TEST_SRC_SHA",
            CheckoutCommit = "TEST_COMMIT",
            CheckoutTree = "TEST_TREE",
            GithubRunId = "12345",
            Files = new[] { new { Name = "QueueFixture.exe", Sha256 = helperExeHash, SizeBytes = (long)helperExeBytes.Length } }
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
        var compExeHash = ComputeSha256Hex(compExeBytes);
        var compZip = temp.GetPath("companion.zip");
        CreateSyntheticZip(compZip, new Dictionary<string, byte[]> { { "DXVK-Companion.exe", compExeBytes } });
        var compZipHash = ComputeSha256Hex(File.ReadAllBytes(compZip));
        var compZipSize = new FileInfo(compZip).Length;

        var fakeTarBytes = Encoding.ASCII.GetBytes("SYNTHETIC_DXVK_TAR_GZ_PAYLOAD");
        var fakeTarPath = temp.CreateFile("dxvk-3.1.1.tar.gz", "SYNTHETIC_DXVK_TAR_GZ_PAYLOAD");
        var fakeTarHash = ComputeSha256Hex(fakeTarBytes);

        var cmdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var testPinsJson = JsonSerializer.Serialize(new
        {
            HelperZipSize = helperZipSize,
            HelperZipSha256 = helperZipHash,
            HelperExeSize = helperExeBytes.Length,
            HelperExeSha256 = helperExeHash,
            HelperProvenance = new { SourceSha256 = "TEST_SRC_SHA", CheckoutCommit = "TEST_COMMIT", CheckoutTree = "TEST_TREE" },
            CompanionZipSize = compZipSize,
            CompanionZipSha256 = compZipHash,
            CompanionExeSize = compExeBytes.Length,
            CompanionExeSha256 = compExeHash,
            Dxvk311Size = (long)fakeTarBytes.Length,
            Dxvk311Sha256 = fakeTarHash,
            TarExecutable = cmdPath,
            TarInspectArguments = $"/c {batchScript}",
            TarTimeoutMs = 200
        });
        var escapedJson = testPinsJson.Replace("\"", "\\\"");

        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        var powershellPath = GetPowerShellExecutablePath();

        var psi = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" -Action Setup -FixtureRoot \"{root}\" -HelperZipPath \"{helperZip}\" -CompanionZipPath \"{compZip}\" -Dxvk311TarGzPath \"{fakeTarPath}\" -TestPinsJson \"{escapedJson}\" -JsonOutput",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot
        };

        var listingPath = Path.Combine(root, "logs", "dxvk-v3.1.1-listing.txt");
        var inspectErrPath = Path.Combine(root, "logs", "dxvk-v3.1.1-inspect-stderr.txt");
        var residualPath = listingPath + ".residual.json";

        var psProcess = new Process { StartInfo = psi };
        try
        {
            psProcess.Start();
            var stdoutTask = psProcess.StandardOutput.ReadToEndAsync();
            var stderrTask = psProcess.StandardError.ReadToEndAsync();

            var sw = Stopwatch.StartNew();
            while (!File.Exists(residualPath) && sw.ElapsedMilliseconds < 10000)
            {
                await Task.Delay(50);
            }
            Assert.True(File.Exists(residualPath), "Expected Setup archive timeout to write unsettled residual JSON record.");

            await Task.Delay(350);
            Assert.False(psProcess.HasExited, "Setup must remain alive while timed-out archive inspection child process is still running.");

            File.WriteAllText(releaseSignal, "RELEASE");

            if (!psProcess.WaitForExit(15000))
            {
                lock (s_retainedProcesses) { s_retainedProcesses.Add(psProcess); }
                throw new TimeoutException($"Setup did not settle within 15000ms after child release. Residual process {psProcess.Id} retained.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.NotEqual(0, psProcess.ExitCode);
            using var doc = JsonDocument.Parse(stdout);
            Assert.False(doc.RootElement.GetProperty("Success").GetBoolean());
            Assert.Contains("DXVK tar archive inspection timed out", doc.RootElement.GetProperty("Message").GetString()!);

            Assert.Contains("TAR_STARTED_OUT", File.ReadAllText(listingPath));
            Assert.Contains("TAR_LATE_AFTER_TIMEOUT_OUT", File.ReadAllText(listingPath));
            Assert.Contains("TAR_STARTED_ERR", File.ReadAllText(inspectErrPath));
            Assert.Contains("TAR_LATE_AFTER_TIMEOUT_ERR", File.ReadAllText(inspectErrPath));

            using var settledDoc = JsonDocument.Parse(File.ReadAllText(residualPath));
            Assert.True(settledDoc.RootElement.GetProperty("Settled").GetBoolean());
            Assert.Equal(0, settledDoc.RootElement.GetProperty("FinalExitCode").GetInt32());
        }
        finally
        {
            if (!File.Exists(releaseSignal))
            {
                try { File.WriteAllText(releaseSignal, "RELEASE"); } catch { }
            }
            lock (s_retainedProcesses)
            {
                if (!s_retainedProcesses.Contains(psProcess))
                {
                    psProcess.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task BoundedChildProcess_PostStartFault_RetainsOwningScriptUntilChildExitsAndRecordsFailure()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var stdoutLog = temp.GetPath("fault_stdout.log");
        var stderrLog = temp.GetPath("fault_stderr.log");
        var residualPath = stdoutLog + ".residual.json";
        var releaseSignal = temp.GetPath("release_fault.signal");
        var batchScript = temp.CreateFile("gated_fault_child.cmd",
            "@echo off\r\n" +
            "echo BEFORE_FAULT_OUT\r\n" +
            "echo BEFORE_FAULT_ERR 1>&2\r\n" +
            ":waitloop\r\n" +
            $"if exist \"{releaseSignal}\" goto done\r\n" +
            "ping 127.0.0.1 -n 2 > nul\r\n" +
            "goto waitloop\r\n" +
            ":done\r\n" +
            "echo AFTER_FAULT_OUT\r\n" +
            "echo AFTER_FAULT_ERR 1>&2\r\n" +
            "exit /b 0\r\n");

        var cmdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        var powershellPath = GetPowerShellExecutablePath();

        var psi = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" -Action Test-BoundedChild -ChildFileName \"{cmdPath}\" -ChildArguments \"/c {batchScript}\" -TargetPath \"{temp.RootPath}\" -LogPath \"{stdoutLog}\" -StderrLogPath \"{stderrLog}\" -TimeoutMs 5000 -SimulateFault -JsonOutput",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot
        };

        var psProcess = new Process { StartInfo = psi };
        try
        {
            psProcess.Start();
            var stdoutTask = psProcess.StandardOutput.ReadToEndAsync();
            var stderrTask = psProcess.StandardError.ReadToEndAsync();

            var sw = Stopwatch.StartNew();
            while (!File.Exists(residualPath) && sw.ElapsedMilliseconds < 10000)
            {
                await Task.Delay(50);
            }
            Assert.True(File.Exists(residualPath), "Expected post-start fault to write unsettled residual JSON record.");

            await Task.Delay(350);
            Assert.False(psProcess.HasExited, "Owning PowerShell script must remain alive after post-start fault until child exits naturally.");

            File.WriteAllText(releaseSignal, "RELEASE");

            if (!psProcess.WaitForExit(15000))
            {
                lock (s_retainedProcesses) { s_retainedProcesses.Add(psProcess); }
                throw new TimeoutException($"Post-start fault script did not settle within 15000ms. Residual process {psProcess.Id} retained.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.NotEqual(0, psProcess.ExitCode);
            using var doc = JsonDocument.Parse(stdout);
            Assert.False(doc.RootElement.GetProperty("Success").GetBoolean());
            Assert.Contains("Simulated post-start fault", doc.RootElement.GetProperty("Message").GetString()!);

            Assert.Contains("AFTER_FAULT_OUT", File.ReadAllText(stdoutLog));
            Assert.Contains("AFTER_FAULT_ERR", File.ReadAllText(stderrLog));

            using var settledDoc = JsonDocument.Parse(File.ReadAllText(residualPath));
            Assert.True(settledDoc.RootElement.GetProperty("Settled").GetBoolean());
            Assert.Equal(0, settledDoc.RootElement.GetProperty("FinalExitCode").GetInt32());
        }
        finally
        {
            if (!File.Exists(releaseSignal))
            {
                try { File.WriteAllText(releaseSignal, "RELEASE"); } catch { }
            }
            lock (s_retainedProcesses)
            {
                if (!s_retainedProcesses.Contains(psProcess))
                {
                    psProcess.Dispose();
                }
            }
        }
    }

    [Fact]
    public void BoundedChildProcess_LogOpenFailure_FallbackDrainsPipeCapacityAndSettlesWithoutKill()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        // Create an existing directory at stdoutLogDir so opening it as a log file fails with UnauthorizedAccessException
        var stdoutLogDir = temp.GetPath("unopenable_stdout_log_dir");
        Directory.CreateDirectory(stdoutLogDir);
        var stderrLog = temp.GetPath("child_stderr.log");
        var residualPath = stdoutLogDir + ".residual.json";

        // Emit ~176 KiB across 2,200 lines (well above the ~4-64 KiB Windows redirected pipe buffer capacity)
        var batchPath = temp.CreateFile(
            "heavy_pipe_child.cmd",
            "@echo off\r\n" +
            "for /L %%i in (1,1,2200) do @echo DRAIN_FALLBACK_LINE_0123456789ABCDEF_0123456789ABCDEF_0123456789ABCDEF_%%i\r\n" +
            "echo STDERR_DIAGNOSTIC_LINE 1>&2\r\n" +
            "exit /b 0\r\n");

        var cmdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var result = RunScript(
            $"-Action Test-BoundedChild -ChildFileName \"{cmdPath}\" -ChildArguments \"/c {batchPath}\" -LogPath \"{stdoutLogDir}\" -StderrLogPath \"{stderrLog}\" -TargetPath \"{temp.RootPath}\" -TimeoutMs 15000 -JsonOutput");

        // Must fail closed on the log-open fault without deadlocking the child on a full stdout pipe
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.StandardOutput);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("Success").GetBoolean());
        Assert.True(root.GetProperty("Completed").GetBoolean());
        Assert.False(root.GetProperty("TimedOut").GetBoolean());
        Assert.Equal(0, root.GetProperty("ExitCode").GetInt32());
        Assert.True(root.GetProperty("StdoutFault").GetBoolean());
        Assert.True(root.GetProperty("HasDrainFault").GetBoolean());
        Assert.True(root.GetProperty("StdoutBytesRead").GetInt64() > 150000L,
            $"Expected fallback drainer to consume >150 KiB from stdout pipe, got {root.GetProperty("StdoutBytesRead").GetInt64()} bytes.");
        Assert.True(root.GetProperty("StdoutLostBytes").GetInt64() > 150000L,
            $"Expected fallback drainer to record >150 KiB of lost stdout bytes, got {root.GetProperty("StdoutLostBytes").GetInt64()} bytes.");

        var childPid = root.GetProperty("ProcessId").GetInt32();
        try
        {
            var childProc = Process.GetProcessById(childPid);
            Assert.True(childProc.HasExited, $"Expected child PID {childPid} to have exited naturally.");
        }
        catch (ArgumentException)
        {
            // Child process already exited and was reaped by OS
        }

        Assert.True(File.Exists(residualPath), $"Expected durable residual record at {residualPath}.");
        using var settledDoc = JsonDocument.Parse(File.ReadAllText(residualPath));
        var settled = settledDoc.RootElement;
        Assert.True(settled.GetProperty("Settled").GetBoolean());
        Assert.Equal(0, settled.GetProperty("FinalExitCode").GetInt32());
        Assert.True(settled.GetProperty("HasDrainFault").GetBoolean());
        Assert.Contains("Log writer initialization failed", settled.GetProperty("FaultMessage").GetString()!);
        Assert.True(settled.GetProperty("StdoutBytesRead").GetInt64() > 150000L);
        Assert.True(settled.GetProperty("StdoutLostBytes").GetInt64() > 150000L);
        Assert.True(settled.GetProperty("StdoutLinesRead").GetInt32() >= 2200);
        Assert.True(settled.GetProperty("StdoutLostLines").GetInt32() >= 2200);
    }

    // =========================================================================
    // B3 Regressions: Cooperative Readiness/Revision Binding & Monotonic Clock
    // =========================================================================

    [Fact]
    public void Setup_DoesNotAutoAdoptRunningScript_AndStartsNotReady()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        var statePath = Path.Combine(root, "state.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
        var state = doc.RootElement;

        Assert.Equal("NotReady", state.GetProperty("CurrentReadiness").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("AdoptedProfilePath").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("AdoptedProfileSha256").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("AdoptedScriptSha256").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("AdoptedToken").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("AdoptedSupervisorPid").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("AdoptedSupervisorSessionId").ValueKind);
        Assert.Equal(GetCurrentScriptSha256(), state.GetProperty("SetupScriptSha256").GetString());
        Assert.Equal(GetCurrentProfileSha256(), state.GetProperty("SetupProfileSha256").GetString());
        Assert.True(state.GetProperty("MonotonicOriginTimestamp").GetInt64() > 0);
        Assert.True(state.GetProperty("MonotonicFrequency").GetInt64() > 0);
        Assert.True(state.GetProperty("LastMonotonicTimestamp").GetInt64() >= state.GetProperty("MonotonicOriginTimestamp").GetInt64());
    }

    [Fact]
    public void BatchGuard_MissingReadinessRecord_RefusesBeforeProcessStart()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpIsolatedState(temp, testPinsUsed: false, includeReadinessRecord: false);

        var guardRes = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, guardRes.ExitCode);
        Assert.Contains("missing explicit owner readiness record", guardRes.StandardOutput + guardRes.StandardError);

        ClearActiveSupervisorInState(root);
        var supRes = RunScript($"-Action Supervise -FixtureRoot \"{root}\" -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -Commands \"start-helper 1\" -JsonOutput");
        Assert.NotEqual(0, supRes.ExitCode);
        Assert.Contains("missing explicit owner readiness record", supRes.StandardOutput + supRes.StandardError);

        using var stateDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "state.json")));
        Assert.Equal(0, stateDoc.RootElement.GetProperty("HelperStartAttempts").GetInt32());
        Assert.Equal(JsonValueKind.Null, stateDoc.RootElement.GetProperty("AdoptedScriptSha256").ValueKind);
    }

    [Fact]
    public void BatchGuard_WrongAdoptedScriptHash_RefusesBeforeProcessStart()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpIsolatedState(
            temp,
            testPinsUsed: false,
            adoptedScriptShaOverride: "0000000000000000000000000000000000000000000000000000000000000000");

        ClearActiveSupervisorInState(root);
        var supRes = RunScript($"-Action Supervise -FixtureRoot \"{root}\" -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -Commands \"start-helper 1\" -JsonOutput");
        Assert.NotEqual(0, supRes.ExitCode);
        Assert.Contains("does not match running script SHA256", supRes.StandardOutput + supRes.StandardError);

        using var stateDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "state.json")));
        Assert.Equal(0, stateDoc.RootElement.GetProperty("HelperStartAttempts").GetInt32());
        Assert.Equal(JsonValueKind.Null, stateDoc.RootElement.GetProperty("AdoptedScriptSha256").ValueKind);
    }

    [Fact]
    public void BatchGuard_MissingOrMismatchedProfileBinding_RefusesBeforeProcessStart()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpIsolatedState(
            temp,
            testPinsUsed: false,
            adoptedProfilePathOverride: "");

        var missingPathRes = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, missingPathRes.ExitCode);
        Assert.Contains("AdoptedProfilePath", missingPathRes.StandardOutput + missingPathRes.StandardError);

        WriteReadinessRecord(
            root,
            adoptedProfilePathOverride: "docs/validation/OTHER-PROFILE.md",
            bindSessionInState: false);
        var wrongPathRes = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, wrongPathRes.ExitCode);
        Assert.Contains("AdoptedProfilePath", wrongPathRes.StandardOutput + wrongPathRes.StandardError);

        ClearActiveSupervisorInState(root);
        WriteReadinessRecord(
            root,
            adoptedProfileShaOverride: "0000000000000000000000000000000000000000000000000000000000000000",
            bindSessionInState: false);
        var wrongHashRes = RunScript($"-Action Supervise -FixtureRoot \"{root}\" -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -Commands \"start-helper 1\" -JsonOutput");
        Assert.NotEqual(0, wrongHashRes.ExitCode);
        Assert.Contains("AdoptedProfileSha256", wrongHashRes.StandardOutput + wrongHashRes.StandardError);

        using var stateDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "state.json")));
        Assert.Equal(0, stateDoc.RootElement.GetProperty("HelperStartAttempts").GetInt32());
        Assert.Equal(JsonValueKind.Null, stateDoc.RootElement.GetProperty("AdoptedProfileSha256").ValueKind);
    }

    [Fact]
    public async Task BatchGuard_MissingOrMismatchedSessionBinding_RefusesBeforeProcessStart()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpIsolatedState(
            temp,
            testPinsUsed: false,
            supervisorPidOverride: 0);

        var missingPidRes = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, missingPidRes.ExitCode);
        Assert.Contains("missing a valid positive SupervisorPid binding", missingPidRes.StandardOutput + missingPidRes.StandardError);

        WriteReadinessRecord(root, supervisorPidOverride: 999999);
        var wrongPidRes = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, wrongPidRes.ExitCode);
        Assert.Contains("SupervisorPid (999999) does not match active supervisor session PID", wrongPidRes.StandardOutput + wrongPidRes.StandardError);

        WriteReadinessRecord(root, supervisorSessionIdOverride: "");
        var missingSessionRes = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, missingSessionRes.ExitCode);
        Assert.Contains("missing mandatory SupervisorSessionId binding", missingSessionRes.StandardOutput + missingSessionRes.StandardError);

        // Exercise real Supervise handshake with mismatched session ID
        ClearActiveSupervisorInState(root);
        var wrongSessionRes = await RunSuperviseWithHandshakeAsync(
            root,
            (livePid, _) => WriteReadinessRecord(
                root,
                supervisorPidOverride: livePid,
                supervisorSessionIdOverride: "stale-other-session-id",
                bindSessionInState: false),
            "start-helper 1");
        Assert.Contains("[ERROR]", wrongSessionRes.StandardOutput);
        Assert.Contains("stale-other-session-id", wrongSessionRes.StandardOutput);
        Assert.Contains("does not match active supervisor session ID", wrongSessionRes.StandardOutput);

        // Now complete a real Supervise handshake for Session 1, run status and exit normally (which clears ActiveSupervisorPid/ActiveSupervisorSessionId),
        // and verify a second Supervise session refuses to reuse Session 1's readiness record.
        var firstSessionRes = await RunSuperviseWithHandshakeAsync(
            root,
            (livePid, liveSessionId) => WriteReadinessRecord(
                root,
                supervisorPidOverride: livePid,
                supervisorSessionIdOverride: liveSessionId,
                bindSessionInState: false),
            "status");
        Assert.Equal(0, firstSessionRes.ExitCode);

        var secondSessionRes = RunScript($"-Action Supervise -FixtureRoot \"{root}\" -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -Commands \"start-helper 1\" -JsonOutput");
        Assert.NotEqual(0, secondSessionRes.ExitCode);
        Assert.Contains("does not match active supervisor session", secondSessionRes.StandardOutput + secondSessionRes.StandardError);

        using var stateDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "state.json")));
        Assert.Equal(0, stateDoc.RootElement.GetProperty("HelperStartAttempts").GetInt32());
    }

    [Fact]
    public async Task Supervise_ConcurrentSessionWithCopiedSessionId_RefusesWithoutModifyingActiveLockOrReadiness()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);
        var statePath = Path.Combine(root, "state.json");
        var sessionPath = Path.Combine(root, "control", "supervisor-session.json");
        if (File.Exists(sessionPath)) File.Delete(sessionPath);

        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        var powershellPath = GetPowerShellExecutablePath();

        var psiA = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Action Supervise -FixtureRoot \"{root}\" -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot
        };

        var processA = new Process { StartInfo = psiA };
        try
        {
            processA.Start();
            var stdoutATask = processA.StandardOutput.ReadToEndAsync();
            var stderrATask = processA.StandardError.ReadToEndAsync();

            var sw = Stopwatch.StartNew();
            while (!File.Exists(sessionPath) && sw.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(50);
            }
            Assert.True(File.Exists(sessionPath), "Expected Supervisor A to write control/supervisor-session.json.");

            int pidA;
            string sessionIdA;
            using (var sessionDoc = JsonDocument.Parse(File.ReadAllText(sessionPath)))
            {
                pidA = sessionDoc.RootElement.GetProperty("SupervisorPid").GetInt32();
                sessionIdA = sessionDoc.RootElement.GetProperty("SupervisorSessionId").GetString()!;
            }
            Assert.Equal(processA.Id, pidA);
            Assert.False(string.IsNullOrWhiteSpace(sessionIdA));

            // Complete real handshake for Supervisor A
            WriteReadinessRecord(
                root,
                supervisorPidOverride: pidA,
                supervisorSessionIdOverride: sessionIdA,
                bindSessionInState: false);

            await processA.StandardInput.WriteLineAsync("status");
            await processA.StandardInput.FlushAsync();
            await Task.Delay(350);

            using (var readyStateDoc = JsonDocument.Parse(File.ReadAllText(statePath)))
            {
                var st = readyStateDoc.RootElement;
                Assert.Equal(pidA, st.GetProperty("ActiveSupervisorPid").GetInt32());
                Assert.Equal(sessionIdA, st.GetProperty("ActiveSupervisorSessionId").GetString());
                Assert.Equal("Ready", st.GetProperty("CurrentReadiness").GetString());
                Assert.Equal(pidA, st.GetProperty("AdoptedSupervisorPid").GetInt32());
                Assert.Equal(sessionIdA, st.GetProperty("AdoptedSupervisorSessionId").GetString());
            }

            // While Supervisor A is still open, invoke Supervisor B passing A's copied SupervisorSessionId
            var resultB = RunScript(
                $"-Action Supervise -FixtureRoot \"{root}\" -SupervisorSessionId \"{sessionIdA}\" -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -Commands status -JsonOutput");
            Assert.NotEqual(0, resultB.ExitCode);
            Assert.Contains("Another supervisor session is already active on fixture root", resultB.StandardOutput + resultB.StandardError);
            Assert.Contains($"(PID {pidA})", resultB.StandardOutput + resultB.StandardError);

            // Verify Supervisor B did not clobber or clear Supervisor A's active lock, session record, or readiness
            using (var stateAfterB = JsonDocument.Parse(File.ReadAllText(statePath)))
            {
                var st = stateAfterB.RootElement;
                Assert.Equal(pidA, st.GetProperty("ActiveSupervisorPid").GetInt32());
                Assert.Equal(sessionIdA, st.GetProperty("ActiveSupervisorSessionId").GetString());
                Assert.Equal("Ready", st.GetProperty("CurrentReadiness").GetString());
                Assert.Equal(0, st.GetProperty("HelperStartAttempts").GetInt32());
                Assert.Equal(0, st.GetProperty("CompanionStartAttempts").GetInt32());
            }
            using (var sessionAfterB = JsonDocument.Parse(File.ReadAllText(sessionPath)))
            {
                Assert.Equal(pidA, sessionAfterB.RootElement.GetProperty("SupervisorPid").GetInt32());
                Assert.Equal(sessionIdA, sessionAfterB.RootElement.GetProperty("SupervisorSessionId").GetString());
            }

            // Now close Supervisor A normally
            await processA.StandardInput.WriteLineAsync("exit");
            await processA.StandardInput.FlushAsync();
            processA.StandardInput.Close();

            if (!processA.WaitForExit(15000))
            {
                lock (s_retainedProcesses) { s_retainedProcesses.Add(processA); }
                throw new TimeoutException($"Supervisor A timed out after 15000ms. Residual process {processA.Id} retained.");
            }

            var stdoutA = await stdoutATask;
            var stderrA = await stderrATask;
            Assert.True(processA.ExitCode == 0, $"Supervisor A failed with exit code {processA.ExitCode}.\nSTDOUT: {stdoutA}\nSTDERR: {stderrA}");

            using (var finalStateDoc = JsonDocument.Parse(File.ReadAllText(statePath)))
            {
                var st = finalStateDoc.RootElement;
                Assert.Equal(JsonValueKind.Null, st.GetProperty("ActiveSupervisorPid").ValueKind);
                Assert.Equal(JsonValueKind.Null, st.GetProperty("ActiveSupervisorSessionId").ValueKind);
                Assert.Equal(0, st.GetProperty("HelperStartAttempts").GetInt32());
                Assert.Equal(0, st.GetProperty("CompanionStartAttempts").GetInt32());
            }
        }
        finally
        {
            lock (s_retainedProcesses)
            {
                if (!s_retainedProcesses.Contains(processA))
                {
                    processA.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task BatchGuard_StaleReadinessConfirmation_RefusesBeforeProcessStart()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var staleUtc = DateTime.UtcNow.AddHours(-2).ToString("o");
        var root = SetUpIsolatedState(
            temp,
            testPinsUsed: false,
            confirmedAtUtcOverride: staleUtc);

        var guardRes = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, guardRes.ExitCode);
        Assert.Contains("stale readiness confirmation", guardRes.StandardOutput + guardRes.StandardError);

        ClearActiveSupervisorInState(root);
        var supRes = await RunSuperviseWithHandshakeAsync(
            root,
            (livePid, liveSessionId) => WriteReadinessRecord(
                root,
                supervisorPidOverride: livePid,
                supervisorSessionIdOverride: liveSessionId,
                confirmedAtUtcOverride: staleUtc,
                bindSessionInState: false),
            "start-helper 1");
        Assert.Contains("[ERROR]", supRes.StandardOutput);
        Assert.Contains("stale readiness confirmation", supRes.StandardOutput);

        using var stateDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "state.json")));
        Assert.Equal(0, stateDoc.RootElement.GetProperty("HelperStartAttempts").GetInt32());
    }

    [Fact]
    public async Task BatchGuard_UnacknowledgedOwnerAvailabilityOrOpenGames_RefusesBeforeProcessStart()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpIsolatedState(
            temp,
            testPinsUsed: false,
            ownerAvailable: false,
            gamesAndCompanionClosed: false);

        var guardRes = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, guardRes.ExitCode);
        Assert.Contains("OwnerAvailable = true", guardRes.StandardOutput + guardRes.StandardError);

        ClearActiveSupervisorInState(root);
        var supRes = await RunSuperviseWithHandshakeAsync(
            root,
            (livePid, liveSessionId) => WriteReadinessRecord(
                root,
                supervisorPidOverride: livePid,
                supervisorSessionIdOverride: liveSessionId,
                ownerAvailable: false,
                gamesAndCompanionClosed: false,
                bindSessionInState: false),
            "start-helper 1");
        Assert.Contains("[ERROR]", supRes.StandardOutput);
        Assert.Contains("OwnerAvailable = true", supRes.StandardOutput);

        using var stateDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "state.json")));
        Assert.Equal(0, stateDoc.RootElement.GetProperty("HelperStartAttempts").GetInt32());
    }

    [Fact]
    public void MonotonicClock_WallClockRollback_StillAdvancesElapsedViaMonotonicTicks()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);
        WriteReadinessRecord(root, bindSessionInState: false);

        var statePath = Path.Combine(root, "state.json");
        using var initialDoc = JsonDocument.Parse(File.ReadAllText(statePath));
        var stateRoot = initialDoc.RootElement;
        var originTick = stateRoot.GetProperty("MonotonicOriginTimestamp").GetInt64();
        var freq = stateRoot.GetProperty("MonotonicFrequency").GetInt64();
        var trialStartUtc = DateTime.Parse(stateRoot.GetProperty("TrialStartUtc").GetString()!).ToUniversalTime();

        // Advance monotonic tick by +15 seconds, while rolling wall-clock time BACKWARD by -120 seconds before TrialStartUtc
        var advancedTick = originTick + (freq * 15L);
        var rolledBackUtc = trialStartUtc.AddSeconds(-120).ToString("o");

        var result = RunScript(
            $"-Action Supervise -FixtureRoot \"{root}\" -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -TestMonotonicTick {advancedTick} -TestMonotonicFrequency {freq} -TestCurrentUtc \"{rolledBackUtc}\" -Commands status -JsonOutput");
        Assert.True(result.ExitCode == 0, $"Supervise status failed: {result.StandardOutput}\n{result.StandardError}");

        using var afterDoc = JsonDocument.Parse(File.ReadAllText(statePath));
        var afterState = afterDoc.RootElement;
        Assert.False(afterState.GetProperty("TerminalHold").GetBoolean());
        var elapsedMs = afterState.GetProperty("ElapsedMilliseconds").GetInt64();
        Assert.True(elapsedMs >= 15000L, $"Expected ElapsedMilliseconds >= 15000ms via monotonic ticks despite backward wall-clock shift, got {elapsedMs}ms.");
    }

    [Fact]
    public void MonotonicClock_SupervisorRestartGap_ConservativelyAccountsForElapsedTime()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        var statePath = Path.Combine(root, "state.json");
        var stateObj = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(statePath))!;
        var originTick = long.Parse(stateObj["MonotonicOriginTimestamp"].ToString()!);
        var freq = long.Parse(stateObj["MonotonicFrequency"].ToString()!);
        var trialStartUtc = DateTime.Parse(stateObj["TrialStartUtc"].ToString()!).ToUniversalTime();

        // Simulate a previous supervisor session (different PID) that recorded LastWallClockUtc at T+5s,
        // and a new supervisor session starting at T+45s where monotonic ticks only advanced by +2s
        stateObj["LastMonotonicSessionPid"] = 999999;
        stateObj["LastWallClockUtc"] = trialStartUtc.AddSeconds(5).ToString("o");
        stateObj["ElapsedMilliseconds"] = 5000L;
        stateObj["AccumulatedMonotonicMs"] = 5000L;
        File.WriteAllText(statePath, JsonSerializer.Serialize(stateObj, new JsonSerializerOptions { WriteIndented = true }));

        var futureUtc = trialStartUtc.AddSeconds(45).ToString("o");
        WriteReadinessRecord(root, confirmedAtUtcOverride: futureUtc, bindSessionInState: false);

        var nextTick = originTick + (freq * 7L);
        var result = RunScript(
            $"-Action Supervise -FixtureRoot \"{root}\" -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -TestMonotonicTick {nextTick} -TestMonotonicFrequency {freq} -TestCurrentUtc \"{futureUtc}\" -Commands status -JsonOutput");
        Assert.True(result.ExitCode == 0, $"Supervise status failed: {result.StandardOutput}\n{result.StandardError}");

        using var afterDoc = JsonDocument.Parse(File.ReadAllText(statePath));
        var afterState = afterDoc.RootElement;
        Assert.False(afterState.GetProperty("TerminalHold").GetBoolean());
        var elapsedMs = afterState.GetProperty("ElapsedMilliseconds").GetInt64();
        Assert.True(elapsedMs >= 45000L, $"Expected conservative restart accounting >= 45000ms, got {elapsedMs}ms.");
    }

    [Fact]
    public void MonotonicClock_MissingOrInvalidOrigin_LatchesTerminalHoldWithoutReset()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);
        WriteReadinessRecord(root, bindSessionInState: false);

        var statePath = Path.Combine(root, "state.json");
        var stateObj = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(statePath))!;
        stateObj["ElapsedMilliseconds"] = 12345L;
        stateObj["AccumulatedMonotonicMs"] = 12345L;
        stateObj["MonotonicOriginTimestamp"] = 0L; // Invalid non-positive origin
        File.WriteAllText(statePath, JsonSerializer.Serialize(stateObj, new JsonSerializerOptions { WriteIndented = true }));

        var result = RunScript($"-Action Supervise -FixtureRoot \"{root}\" -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -Commands status -JsonOutput");
        Assert.Equal(0, result.ExitCode);

        using var afterDoc = JsonDocument.Parse(File.ReadAllText(statePath));
        var afterState = afterDoc.RootElement;
        Assert.True(afterState.GetProperty("TerminalHold").GetBoolean());
        Assert.Equal("Failed", afterState.GetProperty("LastCheckpointOutcome").GetString());
        Assert.Equal("NotReady", afterState.GetProperty("CurrentReadiness").GetString());
        Assert.Contains("Monotonic clock integrity failure", afterState.GetProperty("TerminalHoldReason").GetString()!);
        Assert.True(afterState.GetProperty("ElapsedMilliseconds").GetInt64() >= 12345L,
            "Invalid monotonic state must not reset prior ElapsedMilliseconds.");
    }

    [Fact]
    public void Supervise_TerminalHold_EnforcesOwnerFirstCompanionStopBeforeHelperStop()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);
        WriteReadinessRecord(root, bindSessionInState: false);

        var statePath = Path.Combine(root, "state.json");
        var stateObj = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(statePath))!;
        stateObj["TerminalHold"] = true;
        stateObj["TerminalHoldReason"] = "Simulated hold while Companion and Helper active";
        stateObj["LastCheckpointOutcome"] = "Failed";
        stateObj["ActiveCompanionPid"] = 42424;
        stateObj["ActiveHelperPid"] = 42425;
        stateObj["ActiveHelperSlot"] = 1;
        File.WriteAllText(statePath, JsonSerializer.Serialize(stateObj, new JsonSerializerOptions { WriteIndented = true }));

        var result = RunScript($"-Action Supervise -FixtureRoot \"{root}\" -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -Commands \"stop-helper 1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("close Companion normally (stop-companion) before stopping helper", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void ResourceObservation_CompanionWorkingSet_BelowAndEqualAndAboveOneGiB_RetainsGreaterThanSemantics()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);
        WriteReadinessRecord(root, bindSessionInState: false);

        var statePath = Path.Combine(root, "state.json");

        // 1. Below 1 GiB (1,073,741,823 bytes)
        var resBelow = RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 5001 -SampleWorkingSetBytes 1073741823 -JsonOutput");
        Assert.Equal(0, resBelow.ExitCode);
        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            Assert.False(doc.RootElement.GetProperty("TerminalHold").GetBoolean());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("FirstResourceHold").ValueKind);
            var obs = doc.RootElement.GetProperty("ProcessMemoryObservations");
            Assert.Equal(1, obs.GetArrayLength());
            var entry = obs[0];
            Assert.Equal(1073741824L, entry.GetProperty("ConfiguredWorkingSetCapBytes").GetInt64());
            Assert.Equal(1073741823L, entry.GetProperty("CurrentWorkingSetBytes").GetInt64());
            Assert.Equal(1073741823L, entry.GetProperty("MaxSampledWorkingSetBytes").GetInt64());
            Assert.Equal(1, entry.GetProperty("SampleCount").GetInt32());
            Assert.True(entry.GetProperty("IsLive").GetBoolean());
        }

        // 2. Exactly equal to 1 GiB (1,073,741,824 bytes) - strictly greater-than semantics preserved (no hold)
        var resEqual = RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 5001 -SampleWorkingSetBytes 1073741824 -JsonOutput");
        Assert.Equal(0, resEqual.ExitCode);
        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            Assert.False(doc.RootElement.GetProperty("TerminalHold").GetBoolean());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("FirstResourceHold").ValueKind);
            var obs = doc.RootElement.GetProperty("ProcessMemoryObservations");
            Assert.Equal(1, obs.GetArrayLength());
            var entry = obs[0];
            Assert.Equal(1073741824L, entry.GetProperty("CurrentWorkingSetBytes").GetInt64());
            Assert.Equal(1073741824L, entry.GetProperty("MaxSampledWorkingSetBytes").GetInt64());
            Assert.Equal(2, entry.GetProperty("SampleCount").GetInt32());
        }

        // 3. Above 1 GiB (1,073,741,825 bytes) - latches terminal hold
        var resAbove = RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 5001 -SampleWorkingSetBytes 1073741825 -JsonOutput");
        Assert.Equal(0, resAbove.ExitCode);
        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            Assert.True(doc.RootElement.GetProperty("TerminalHold").GetBoolean());
            var hold = doc.RootElement.GetProperty("FirstResourceHold");
            Assert.NotEqual(JsonValueKind.Null, hold.ValueKind);
            Assert.Equal("WorkingSet", hold.GetProperty("Metric").GetString());
            Assert.Equal("Companion", hold.GetProperty("Role").GetString());
            Assert.Equal(5001, hold.GetProperty("Pid").GetInt32());
            Assert.Equal(1073741825L, hold.GetProperty("ObservedValue").GetInt64());
            Assert.Equal(1073741824L, hold.GetProperty("ConfiguredLimit").GetInt64());
            Assert.Equal("bytes", hold.GetProperty("Unit").GetString());
            Assert.Equal("CompanionMemoryExhausted", hold.GetProperty("Checkpoint").GetString());
        }

        // 4. Batch guard refuses further starts while on terminal hold
        var guardRes = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -NoAutoDiscovery -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, guardRes.ExitCode);
        Assert.Contains("Terminal hold active", guardRes.StandardOutput + guardRes.StandardError);
    }

    [Fact]
    public void ResourceObservation_HelperWorkingSetAndCpuLimits_RemainUnchangedAndGuarded()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);
        WriteReadinessRecord(root, bindSessionInState: false);

        var statePath = Path.Combine(root, "state.json");

        // 1. Helper at exactly 50 MiB (52,428,800 bytes) and 10 minutes CPU (600,000 ms) - no hold
        var resEqual = RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Helper -SamplePid 6001 -Slot 1 -SampleWorkingSetBytes 52428800 -SampleCpuMilliseconds 600000 -JsonOutput");
        Assert.Equal(0, resEqual.ExitCode);
        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            Assert.False(doc.RootElement.GetProperty("TerminalHold").GetBoolean());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("FirstResourceHold").ValueKind);
            var obs = doc.RootElement.GetProperty("ProcessMemoryObservations");
            Assert.Equal(1, obs.GetArrayLength());
            Assert.Equal(52428800L, obs[0].GetProperty("ConfiguredWorkingSetCapBytes").GetInt64());
        }

        // 2. Helper above 50 MiB (52,428,801 bytes) - latches terminal hold
        var resAbove = RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Helper -SamplePid 6001 -Slot 1 -SampleWorkingSetBytes 52428801 -SampleCpuMilliseconds 600000 -JsonOutput");
        Assert.Equal(0, resAbove.ExitCode);
        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            Assert.True(doc.RootElement.GetProperty("TerminalHold").GetBoolean());
            var hold = doc.RootElement.GetProperty("FirstResourceHold");
            Assert.NotEqual(JsonValueKind.Null, hold.ValueKind);
            Assert.Equal("WorkingSet", hold.GetProperty("Metric").GetString());
            Assert.Equal("Helper", hold.GetProperty("Role").GetString());
            Assert.Equal(6001, hold.GetProperty("Pid").GetInt32());
            Assert.Equal(52428801L, hold.GetProperty("ObservedValue").GetInt64());
            Assert.Equal(52428800L, hold.GetProperty("ConfiguredLimit").GetInt64());
            Assert.Equal("HelperMemoryExhausted", hold.GetProperty("Checkpoint").GetString());
        }
    }

    [Fact]
    public void ResourceObservation_MaximaAndOsReportedPeakSemantics_PreservesUnavailableMetricsHonestly()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);
        WriteReadinessRecord(root, bindSessionInState: false);

        var statePath = Path.Combine(root, "state.json");

        // 1. Sample 1 with available OS peak
        var s1 = RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 7001 -SampleWorkingSetBytes 200000000 -SamplePeakWorkingSetBytes 250000000 -JsonOutput");
        Assert.Equal(0, s1.ExitCode);
        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            var obs = doc.RootElement.GetProperty("ProcessMemoryObservations")[0];
            Assert.Equal(200000000L, obs.GetProperty("MaxSampledWorkingSetBytes").GetInt64());
            Assert.Equal(200000000L, obs.GetProperty("CurrentWorkingSetBytes").GetInt64());
            Assert.Equal(250000000L, obs.GetProperty("OsReportedPeakWorkingSetBytes").GetInt64());
            Assert.True(obs.GetProperty("OsPeakAvailable").GetBoolean());
            Assert.Equal(1, obs.GetProperty("SampleCount").GetInt32());
        }

        // 2. Sample 2 with higher working set and higher peak
        var s2 = RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 7001 -SampleWorkingSetBytes 400000000 -SamplePeakWorkingSetBytes 450000000 -JsonOutput");
        Assert.Equal(0, s2.ExitCode);
        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            var obs = doc.RootElement.GetProperty("ProcessMemoryObservations")[0];
            Assert.Equal(400000000L, obs.GetProperty("MaxSampledWorkingSetBytes").GetInt64());
            Assert.Equal(400000000L, obs.GetProperty("CurrentWorkingSetBytes").GetInt64());
            Assert.Equal(450000000L, obs.GetProperty("OsReportedPeakWorkingSetBytes").GetInt64());
            Assert.True(obs.GetProperty("OsPeakAvailable").GetBoolean());
            Assert.Equal(2, obs.GetProperty("SampleCount").GetInt32());
        }

        // 3. Sample 3: working set drops, and OS peak is unavailable (-1)
        var s3 = RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 7001 -SampleWorkingSetBytes 150000000 -SamplePeakWorkingSetBytes -1 -JsonOutput");
        Assert.Equal(0, s3.ExitCode);
        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            var obs = doc.RootElement.GetProperty("ProcessMemoryObservations")[0];
            Assert.Equal(150000000L, obs.GetProperty("CurrentWorkingSetBytes").GetInt64());
            Assert.Equal(400000000L, obs.GetProperty("MaxSampledWorkingSetBytes").GetInt64());
            Assert.Equal(450000000L, obs.GetProperty("OsReportedPeakWorkingSetBytes").GetInt64());
            Assert.Equal(3, obs.GetProperty("SampleCount").GetInt32());
        }

        // 4. Distinct process invocation where OS peak is never available from start
        var s4 = RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 7002 -SampleWorkingSetBytes 300000000 -SamplePeakWorkingSetBytes -1 -JsonOutput");
        Assert.Equal(0, s4.ExitCode);
        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            var obs = doc.RootElement.GetProperty("ProcessMemoryObservations")[1];
            Assert.Equal(7002, obs.GetProperty("Pid").GetInt32());
            Assert.Equal(300000000L, obs.GetProperty("MaxSampledWorkingSetBytes").GetInt64());
            Assert.Equal(JsonValueKind.Null, obs.GetProperty("OsReportedPeakWorkingSetBytes").ValueKind);
            Assert.False(obs.GetProperty("OsPeakAvailable").GetBoolean());
        }
    }

    [Fact]
    public void ResourceObservation_PostExitRetention_PreservesSummariesWhenActiveProcessesEmpty()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);
        WriteReadinessRecord(root, bindSessionInState: false);

        var statePath = Path.Combine(root, "state.json");

        // 1. Sample Helper and Companion while live
        RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Helper -SamplePid 8001 -Slot 1 -SampleWorkingSetBytes 30000000 -SamplePeakWorkingSetBytes 35000000 -JsonOutput");
        RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 8002 -SampleWorkingSetBytes 600000000 -SamplePeakWorkingSetBytes 650000000 -JsonOutput");

        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            Assert.True(doc.RootElement.GetProperty("ActiveProcesses").GetArrayLength() > 0);
            var obs = doc.RootElement.GetProperty("ProcessMemoryObservations");
            Assert.Equal(2, obs.GetArrayLength());
        }

        // 2. Processes exit normally; ActiveProcesses becomes empty
        var resExit = RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -ProcessExited -JsonOutput");
        Assert.Equal(0, resExit.ExitCode);

        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("ActiveProcesses").GetArrayLength());

            var obs = doc.RootElement.GetProperty("ProcessMemoryObservations");
            Assert.Equal(2, obs.GetArrayLength());

            var helperObs = obs[0];
            Assert.Equal("Helper", helperObs.GetProperty("Role").GetString());
            Assert.Equal(8001, helperObs.GetProperty("Pid").GetInt32());
            Assert.False(helperObs.GetProperty("IsLive").GetBoolean());
            Assert.Equal(JsonValueKind.Null, helperObs.GetProperty("CurrentWorkingSetBytes").ValueKind);
            Assert.Equal(30000000L, helperObs.GetProperty("LastSampledWorkingSetBytes").GetInt64());
            Assert.Equal(30000000L, helperObs.GetProperty("MaxSampledWorkingSetBytes").GetInt64());
            Assert.Equal(35000000L, helperObs.GetProperty("OsReportedPeakWorkingSetBytes").GetInt64());

            var compObs = obs[1];
            Assert.Equal("Companion", compObs.GetProperty("Role").GetString());
            Assert.Equal(8002, compObs.GetProperty("Pid").GetInt32());
            Assert.False(compObs.GetProperty("IsLive").GetBoolean());
            Assert.Equal(JsonValueKind.Null, compObs.GetProperty("CurrentWorkingSetBytes").ValueKind);
            Assert.Equal(600000000L, compObs.GetProperty("LastSampledWorkingSetBytes").GetInt64());
            Assert.Equal(600000000L, compObs.GetProperty("MaxSampledWorkingSetBytes").GetInt64());
            Assert.Equal(650000000L, compObs.GetProperty("OsReportedPeakWorkingSetBytes").GetInt64());
        }
    }

    [Fact]
    public void ResourceObservation_FirstResourceHold_IsImmutableAcrossSubsequentSamplesAndExit()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);
        WriteReadinessRecord(root, bindSessionInState: false);

        var statePath = Path.Combine(root, "state.json");

        // 1. Initial over-limit sample (1.1 GiB)
        RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 9001 -SampleWorkingSetBytes 1100000000 -JsonOutput");

        string firstHoldTime;
        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            Assert.True(doc.RootElement.GetProperty("TerminalHold").GetBoolean());
            var hold = doc.RootElement.GetProperty("FirstResourceHold");
            Assert.Equal(1100000000L, hold.GetProperty("ObservedValue").GetInt64());
            Assert.Equal(1073741824L, hold.GetProperty("ConfiguredLimit").GetInt64());
            Assert.Equal("WorkingSet", hold.GetProperty("Metric").GetString());
            firstHoldTime = hold.GetProperty("TimestampUtc").GetString()!;
        }

        // 2. Subsequent higher sample (1.5 GiB) - observation updates maxima, but FirstResourceHold is immutable
        RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 9001 -SampleWorkingSetBytes 1500000000 -JsonOutput");

        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            var obs = doc.RootElement.GetProperty("ProcessMemoryObservations")[0];
            Assert.Equal(1500000000L, obs.GetProperty("MaxSampledWorkingSetBytes").GetInt64());

            var hold = doc.RootElement.GetProperty("FirstResourceHold");
            Assert.Equal(1100000000L, hold.GetProperty("ObservedValue").GetInt64());
            Assert.Equal(firstHoldTime, hold.GetProperty("TimestampUtc").GetString());
        }

        // 3. Subsequent CPU limit violation (2,000,000 ms > 1,800,000 ms) - FirstResourceHold remains WorkingSet
        RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 9001 -SampleWorkingSetBytes 1500000000 -SampleCpuMilliseconds 2000000 -JsonOutput");

        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            var hold = doc.RootElement.GetProperty("FirstResourceHold");
            Assert.Equal("WorkingSet", hold.GetProperty("Metric").GetString());
            Assert.Equal(1100000000L, hold.GetProperty("ObservedValue").GetInt64());
            Assert.Equal(firstHoldTime, hold.GetProperty("TimestampUtc").GetString());
        }

        // 4. Process exits; FirstResourceHold remains preserved
        RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -ProcessExited -JsonOutput");

        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            Assert.True(doc.RootElement.GetProperty("TerminalHold").GetBoolean());
            var hold = doc.RootElement.GetProperty("FirstResourceHold");
            Assert.Equal("WorkingSet", hold.GetProperty("Metric").GetString());
            Assert.Equal(1100000000L, hold.GetProperty("ObservedValue").GetInt64());
            Assert.Equal(firstHoldTime, hold.GetProperty("TimestampUtc").GetString());
        }
    }

    [Fact]
    public void ResourceObservation_SeparateProcessInvocations_DistinctSummariesWithoutStateReset()
    {
        AssertHostedCiEnvironment();

        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);
        WriteReadinessRecord(root, bindSessionInState: false);

        var statePath = Path.Combine(root, "state.json");
        long initialOriginTimestamp;
        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            initialOriginTimestamp = doc.RootElement.GetProperty("MonotonicOriginTimestamp").GetInt64();
        }

        // Invocation 1: Companion PID 9101, sampled twice, then exits
        RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 9101 -SampleWorkingSetBytes 300000000 -JsonOutput");
        RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 9101 -SampleWorkingSetBytes 400000000 -JsonOutput");
        RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -ProcessExited -JsonOutput");

        // Invocation 2: Companion PID 9102, sampled once
        RunScript($"-Action Test-ResourceObservation -FixtureRoot \"{root}\" -Role Companion -SamplePid 9102 -SampleWorkingSetBytes 500000000 -JsonOutput");

        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            var obs = doc.RootElement.GetProperty("ProcessMemoryObservations");
            Assert.Equal(2, obs.GetArrayLength());

            var inv1 = obs[0];
            Assert.Equal(9101, inv1.GetProperty("Pid").GetInt32());
            Assert.Equal("Companion", inv1.GetProperty("Role").GetString());
            Assert.False(inv1.GetProperty("IsLive").GetBoolean());
            Assert.Equal(2, inv1.GetProperty("SampleCount").GetInt32());
            Assert.Equal(400000000L, inv1.GetProperty("MaxSampledWorkingSetBytes").GetInt64());

            var inv2 = obs[1];
            Assert.Equal(9102, inv2.GetProperty("Pid").GetInt32());
            Assert.Equal("Companion", inv2.GetProperty("Role").GetString());
            Assert.True(inv2.GetProperty("IsLive").GetBoolean());
            Assert.Equal(1, inv2.GetProperty("SampleCount").GetInt32());
            Assert.Equal(500000000L, inv2.GetProperty("MaxSampledWorkingSetBytes").GetInt64());

            // Monotonic state preserved without reset
            Assert.Equal(initialOriginTimestamp, doc.RootElement.GetProperty("MonotonicOriginTimestamp").GetInt64());
        }
    }

    private static void ClearActiveSupervisorInState(string root)
    {
        var statePath = Path.Combine(root, "state.json");
        var stateObj = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(statePath))!;
        stateObj["ActiveSupervisorPid"] = null;
        stateObj["ActiveSupervisorSessionId"] = null;
        File.WriteAllText(statePath, JsonSerializer.Serialize(stateObj, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<ProcessResult> RunSuperviseWithHandshakeAsync(
        string root,
        Action<int, string> configureReadiness,
        params string[] commands)
    {
        ClearActiveSupervisorInState(root);
        var sessionPath = Path.Combine(root, "control", "supervisor-session.json");
        if (File.Exists(sessionPath))
        {
            File.Delete(sessionPath);
        }

        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        var powershellPath = GetPowerShellExecutablePath();

        var psi = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Action Supervise -FixtureRoot \"{root}\" -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
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

            var sw = Stopwatch.StartNew();
            while (!File.Exists(sessionPath) && sw.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(50);
            }
            Assert.True(File.Exists(sessionPath), "Expected Supervise to create control/supervisor-session.json on startup.");

            using (var sessionDoc = JsonDocument.Parse(File.ReadAllText(sessionPath)))
            {
                var livePid = sessionDoc.RootElement.GetProperty("SupervisorPid").GetInt32();
                var liveSessionId = sessionDoc.RootElement.GetProperty("SupervisorSessionId").GetString()!;
                Assert.Equal(process.Id, livePid);
                Assert.False(string.IsNullOrWhiteSpace(liveSessionId));
                configureReadiness(livePid, liveSessionId);
            }

            foreach (var cmd in commands)
            {
                await process.StandardInput.WriteLineAsync(cmd);
                await process.StandardInput.FlushAsync();
                await Task.Delay(200);
            }

            await process.StandardInput.WriteLineAsync("exit");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();

            if (!process.WaitForExit(15000))
            {
                lock (s_retainedProcesses) { s_retainedProcesses.Add(process); }
                throw new TimeoutException($"Supervise handshake helper timed out after 15000ms. Residual process {process.Id} retained.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessResult(process.ExitCode, stdout, stderr);
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

    private static string GetCurrentScriptSha256()
    {
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        return ComputeSha256Hex(File.ReadAllBytes(scriptPath));
    }

    private static string GetCurrentProfileSha256()
    {
        var repoRoot = FindRepositoryRoot();
        var profilePath = Path.Combine(repoRoot, "docs", "validation", "PACKAGED-PROCESS-LIFECYCLE-V1.md");
        return ComputeSha256Hex(File.ReadAllBytes(profilePath));
    }

    private static void WriteReadinessRecord(
        string root,
        string? adoptedScriptShaOverride = null,
        string? adoptedProfilePathOverride = null,
        string? adoptedProfileShaOverride = null,
        int? supervisorPidOverride = null,
        string? supervisorSessionIdOverride = null,
        string? confirmedAtUtcOverride = null,
        bool ownerAvailable = true,
        bool gamesAndCompanionClosed = true,
        string? batchStartTimeUtcOverride = null,
        bool bindSessionInState = true)
    {
        var statePath = Path.Combine(root, "state.json");
        var stateObj = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(statePath))!;
        var batchStartUtc = batchStartTimeUtcOverride ?? stateObj["BatchStartTimeUtc"]!.ToString()!;

        var defaultSessionPid = Environment.ProcessId;
        var defaultSessionId = "test-supervisor-session-1";

        if (bindSessionInState)
        {
            stateObj["ActiveSupervisorPid"] = defaultSessionPid;
            stateObj["ActiveSupervisorSessionId"] = defaultSessionId;
            File.WriteAllText(statePath, JsonSerializer.Serialize(stateObj, new JsonSerializerOptions { WriteIndented = true }));
        }

        var readiness = new
        {
            ProfileId = "process-lifecycle-v1",
            AdoptedProfilePath = adoptedProfilePathOverride ?? "docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md",
            AdoptedProfileSha256 = adoptedProfileShaOverride ?? GetCurrentProfileSha256(),
            AdoptionToken = "ADOPTED-PROCESS-LIFECYCLE-V1",
            AdoptedScriptSha256 = adoptedScriptShaOverride ?? GetCurrentScriptSha256(),
            FixtureRoot = Path.GetFullPath(root).TrimEnd('\\', '/'),
            BatchStartTimeUtc = batchStartUtc,
            SupervisorPid = supervisorPidOverride ?? defaultSessionPid,
            SupervisorSessionId = supervisorSessionIdOverride ?? defaultSessionId,
            OwnerAvailable = ownerAvailable,
            GamesAndCompanionClosed = gamesAndCompanionClosed,
            ConfirmedAtUtc = confirmedAtUtcOverride ?? DateTime.UtcNow.ToString("o")
        };

        var controlDir = Path.Combine(root, "control");
        Directory.CreateDirectory(controlDir);
        File.WriteAllText(
            Path.Combine(controlDir, "readiness.json"),
            JsonSerializer.Serialize(readiness, new JsonSerializerOptions { WriteIndented = true }));
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
        string lastOutcome = "Success",
        bool includeReadinessRecord = true,
        string? adoptedScriptShaOverride = null,
        string? adoptedProfilePathOverride = null,
        string? adoptedProfileShaOverride = null,
        int? supervisorPidOverride = null,
        string? supervisorSessionIdOverride = null,
        string? confirmedAtUtcOverride = null,
        bool ownerAvailable = true,
        bool gamesAndCompanionClosed = true)
    {
        var root = temp.GetPath("isolated_state_root");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "control"));

        var nowUtc = DateTime.UtcNow.ToString("o");
        var originTick = Stopwatch.GetTimestamp();
        var freq = Stopwatch.Frequency;
        var sessionPid = Environment.ProcessId;
        var sessionId = "test-supervisor-session-1";

        var state = new
        {
            BatchStartTimeUtc = nowUtc,
            TrialStartUtc = nowUtc,
            SetupElapsedMilliseconds = 100L,
            ElapsedMilliseconds = 100L,
            MonotonicOriginTimestamp = originTick,
            MonotonicFrequency = freq,
            LastMonotonicTimestamp = originTick + (freq / 10),
            AccumulatedMonotonicMs = 100L,
            LastWallClockUtc = nowUtc,
            LastMonotonicSessionPid = sessionPid,
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
            CurrentReadiness = "NotReady",
            ReadinessReason = "Pending explicit owner adoption and readiness confirmation",
            CurrentFootprintBytes = 1000L,
            ActiveProcesses = Array.Empty<object>(),
            ProcessMemoryObservations = Array.Empty<object>(),
            FirstResourceHold = (object?)null,
            SetupScriptSha256 = GetCurrentScriptSha256(),
            SetupProfileSha256 = GetCurrentProfileSha256(),
            AdoptedProfilePath = (string?)null,
            AdoptedProfileSha256 = (string?)null,
            AdoptedScriptSha256 = (string?)null,
            AdoptedToken = (string?)null,
            AdoptedSupervisorPid = (int?)null,
            AdoptedSupervisorSessionId = (string?)null,
            IsEnded = isEnded,
            TestPinsUsed = testPinsUsed,
            ActiveHelperPid = (int?)null,
            ActiveHelperSlot = (int?)null,
            ActiveHelperStartTimeUtc = (string?)null,
            LastStoppedHelperSlot = (int?)null,
            NonQualifyingPureDataCheck = false,
            ActiveCompanionPid = (int?)null,
            ActiveSupervisorPid = sessionPid,
            ActiveSupervisorSessionId = sessionId,
            Checkpoints = new[] { "Setup" }
        };

        var statePath = Path.Combine(root, "state.json");
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

        if (includeReadinessRecord)
        {
            WriteReadinessRecord(
                root,
                adoptedScriptShaOverride: adoptedScriptShaOverride,
                adoptedProfilePathOverride: adoptedProfilePathOverride,
                adoptedProfileShaOverride: adoptedProfileShaOverride,
                supervisorPidOverride: supervisorPidOverride,
                supervisorSessionIdOverride: supervisorSessionIdOverride,
                confirmedAtUtcOverride: confirmedAtUtcOverride,
                ownerAvailable: ownerAvailable,
                gamesAndCompanionClosed: gamesAndCompanionClosed,
                batchStartTimeUtcOverride: nowUtc,
                bindSessionInState: false);
        }

        return root;
    }
}
