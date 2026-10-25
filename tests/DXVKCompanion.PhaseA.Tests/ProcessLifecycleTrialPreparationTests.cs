using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class ProcessLifecycleTrialPreparationTests
{
    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

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
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        Assert.True(File.Exists(scriptPath), $"Script not found at: {scriptPath}");

        var psi = new ProcessStartInfo
        {
            FileName = GetPowerShellExecutablePath(),
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
            try { process.Kill(); } catch { }
            throw new TimeoutException($"PowerShell process timed out after {timeoutMilliseconds}ms for args: {arguments}");
        }

        Task.WaitAll(stdoutTask, stderrTask);
        return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static string ComputeSha256Hex(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CreateSyntheticZip(string zipFilePath, IDictionary<string, byte[]> entries)
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

        return zipFilePath;
    }

    [Fact]
    public void Script_Exists_And_ParsesWithoutSyntaxErrors()
    {
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "validation", "ProcessLifecycleTrial.ps1");
        Assert.True(File.Exists(scriptPath));

        var psi = new ProcessStartInfo
        {
            FileName = GetPowerShellExecutablePath(),
            Arguments = $"-NoProfile -NonInteractive -Command \"$errs = $null; [System.Management.Automation.Language.Parser]::ParseFile('{scriptPath.Replace("'", "''")}', [ref]$null, [ref]$errs); if ($errs.Count -gt 0) {{ $errs | ForEach-Object {{ Write-Error $_ }}; exit 1 }} else {{ Write-Output 'PARSED_OK' }}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot
        };

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10000);

        Assert.Equal(0, proc.ExitCode);
        Assert.Contains("PARSED_OK", output);
    }

    [Fact]
    public void Script_FrozenPins_MatchPublishedConstants()
    {
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
        Assert.Contains("\"Success\": true", result.StandardOutput);
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
        Assert.Contains("\"Success\": false", result.StandardOutput);
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
        Assert.Contains("\"Success\": false", result.StandardOutput);
        Assert.Contains("root-relative", result.StandardOutput);
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
        Assert.Contains("\"Success\": false", result.StandardOutput);
        Assert.Contains("duplicate/colliding", result.StandardOutput);
    }

    [Fact]
    public void PathContainment_PathOutsideRoot_IsRejected()
    {
        using var temp = new SyntheticTestDirectory();
        var root = temp.GetPath("base_root");
        Directory.CreateDirectory(root);
        var outside = temp.GetPath("other_root");
        Directory.CreateDirectory(outside);

        var result = RunScript($"-Action Test-PathContainment -PathA \"{root}\" -PathB \"{outside}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("\"Success\": false", result.StandardOutput);
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
        Assert.Contains("\"Success\": true", result.StandardOutput);
    }

    [Fact]
    public void Setup_RefusesExistingNonEmptyRoot()
    {
        using var temp = new SyntheticTestDirectory();
        var root = temp.GetPath("non_empty_root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "pre_existing.txt"), "data");

        var helperZip = temp.GetPath("dummy_helper.zip");
        CreateSyntheticZip(helperZip, new Dictionary<string, byte[]> { { "QueueFixture.exe", Encoding.ASCII.GetBytes("dummy") } });
        var compZip = temp.GetPath("dummy_comp.zip");
        CreateSyntheticZip(compZip, new Dictionary<string, byte[]> { { "DXVK-Companion.exe", Encoding.ASCII.GetBytes("dummy") } });

        var result = RunScript($"-Action Setup -FixtureRoot \"{root}\" -HelperZipPath \"{helperZip}\" -CompanionZipPath \"{compZip}\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not empty", result.StandardOutput + result.StandardError);
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
        Assert.Contains("SHA256 mismatch", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void Setup_SucceedsWithTestPins_InitializesDirectoryStructure()
    {
        using var temp = new SyntheticTestDirectory();
        var root = temp.GetPath("trial_root");

        var helperExeBytes = Encoding.ASCII.GetBytes("TEST_HELPER_EXE_BYTES");
        var helperExeHash = ComputeSha256Hex(helperExeBytes);
        var helperZip = temp.GetPath("helper.zip");
        CreateSyntheticZip(helperZip, new Dictionary<string, byte[]> { { "QueueFixture.exe", helperExeBytes } });
        var helperZipHash = ComputeSha256Hex(File.ReadAllBytes(helperZip));

        var compExeBytes = Encoding.ASCII.GetBytes("TEST_COMPANION_EXE_BYTES");
        var compExeHash = ComputeSha256Hex(compExeBytes);
        var compZip = temp.GetPath("companion.zip");
        CreateSyntheticZip(compZip, new Dictionary<string, byte[]> { { "DXVK-Companion.exe", compExeBytes } });
        var compZipHash = ComputeSha256Hex(File.ReadAllBytes(compZip));

        var testPinsJson = JsonSerializer.Serialize(new
        {
            HelperZipSha256 = helperZipHash,
            HelperExeSha256 = helperExeHash,
            CompanionZipSha256 = compZipHash,
            CompanionExeSha256 = compExeHash
        });

        var escapedJson = testPinsJson.Replace("\"", "\\\"");
        var result = RunScript($"-Action Setup -FixtureRoot \"{root}\" -HelperZipPath \"{helperZip}\" -CompanionZipPath \"{compZip}\" -TestPinsJson \"{escapedJson}\" -JsonOutput");
        Assert.Equal(0, result.ExitCode);

        // Verify seeded layout
        Assert.True(File.Exists(Path.Combine(root, "target", "QueueFixture.exe")));
        Assert.True(File.Exists(Path.Combine(root, "target", "d3d11.dll")));
        Assert.True(File.Exists(Path.Combine(root, "target", "dxvk.conf")));
        Assert.True(File.Exists(Path.Combine(root, "target", "sentinel.txt")));
        Assert.True(Directory.Exists(Path.Combine(root, "control")));
        Assert.True(File.Exists(Path.Combine(root, "baseline", "baseline_manifest.json")));
        Assert.True(File.Exists(Path.Combine(root, "companion", "DXVK-Companion.exe")));
        Assert.True(File.Exists(Path.Combine(root, "companion", "settings.json")));
        Assert.True(File.Exists(Path.Combine(root, "companion", "Profiles", "game-library.json")));
        Assert.True(File.Exists(Path.Combine(root, "state.json")));

        var stateText = File.ReadAllText(Path.Combine(root, "state.json"));
        Assert.Contains("\"TestPinsUsed\": true", stateText);
    }

    [Fact]
    public void LaunchHelper_RefusesMissingAdoptionAttestation()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        var result = RunScript($"-Action Start-Helper -FixtureRoot \"{root}\" -Slot 1 -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("adoption attestation", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void LaunchHelper_RefusesWhenTestPinsUsed()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        // Even with valid adoption token, test pins refuse execution before Process.Start!
        var result = RunScript($"-Action Start-Helper -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("prepared with test pins and cannot be launched", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void LaunchHelper_RefusesPreExistingStopMarker()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        // Create pre-existing stop marker
        var marker = Path.Combine(root, "control", "stop-1.request");
        File.WriteAllText(marker, "STOP\n");

        var result = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Stop marker already exists", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void BatchGuard_RefusesWhenExhaustedOrEnded()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        var statePath = Path.Combine(root, "state.json");
        var stateJson = File.ReadAllText(statePath);
        using var doc = JsonDocument.Parse(stateJson);
        var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(stateJson)!;
        dict["HelperStartCount"] = 4;
        File.WriteAllText(statePath, JsonSerializer.Serialize(dict));

        var result = RunScript($"-Action Test-BatchGuard -FixtureRoot \"{root}\" -Slot 1 -AdoptionAttestation \"ADOPTED-PROCESS-LIFECYCLE-V1\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Maximum helper start count", result.StandardOutput + result.StandardError);
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
      ""ManagementPolicy"": { ""Mode"": ""Disabled"", ""PinnedDxvkVersion"": null },
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
      ""ManagementPolicy"": { ""PinnedDxvkVersion"": null, ""Mode"": ""Disabled"" },
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
        Assert.Contains("\"Success\": true", result.StandardOutput);
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
      ""ManagementPolicy"": { ""Mode"": ""Disabled"", ""PinnedDxvkVersion"": null },
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
      ""ManagementPolicy"": { ""Mode"": ""Disabled"", ""PinnedDxvkVersion"": null },
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
        Assert.Contains("\"Success\": false", result.StandardOutput);
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
        Assert.Contains("\"Success\": true", check1.StandardOutput);

        // Mutate target d3d11.dll
        File.AppendAllText(Path.Combine(root, "target", "d3d11.dll"), "MUTATION");

        var check2 = RunScript($"-Action Compare-TargetBaseline -FixtureRoot \"{root}\" -JsonOutput");
        Assert.NotEqual(0, check2.ExitCode);
        Assert.Contains("\"Success\": false", check2.StandardOutput);
        Assert.Contains("mismatch", check2.StandardOutput);
    }

    [Fact]
    public void VerifyCheckpoint_RestoredFailsIfUnexpectedFileOrMismatch()
    {
        using var temp = new SyntheticTestDirectory();
        var root = SetUpTestFixture(temp);

        // Drop unexpected dxgi.dll in target
        File.WriteAllText(Path.Combine(root, "target", "dxgi.dll"), "LEFTOVER");

        var result = RunScript($"-Action Verify-Checkpoint -FixtureRoot \"{root}\" -CheckpointName \"Checkpoint7-Restored\" -ExpectedType \"Restored\" -JsonOutput");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("dxgi.dll unexpectedly present", result.StandardOutput + result.StandardError);
    }

    private static string SetUpTestFixture(SyntheticTestDirectory temp)
    {
        var root = temp.GetPath("trial_root");

        var helperExeBytes = Encoding.ASCII.GetBytes("TEST_HELPER_EXE_BYTES");
        var helperExeHash = ComputeSha256Hex(helperExeBytes);
        var helperZip = temp.GetPath("helper.zip");
        CreateSyntheticZip(helperZip, new Dictionary<string, byte[]> { { "QueueFixture.exe", helperExeBytes } });
        var helperZipHash = ComputeSha256Hex(File.ReadAllBytes(helperZip));

        var compExeBytes = Encoding.ASCII.GetBytes("TEST_COMPANION_EXE_BYTES");
        var compExeHash = ComputeSha256Hex(compExeBytes);
        var compZip = temp.GetPath("companion.zip");
        CreateSyntheticZip(compZip, new Dictionary<string, byte[]> { { "DXVK-Companion.exe", compExeBytes } });
        var compZipHash = ComputeSha256Hex(File.ReadAllBytes(compZip));

        var testPinsJson = JsonSerializer.Serialize(new
        {
            HelperZipSha256 = helperZipHash,
            HelperExeSha256 = helperExeHash,
            CompanionZipSha256 = compZipHash,
            CompanionExeSha256 = compExeHash
        });

        var escapedJson = testPinsJson.Replace("\"", "\\\"");
        var result = RunScript($"-Action Setup -FixtureRoot \"{root}\" -HelperZipPath \"{helperZip}\" -CompanionZipPath \"{compZip}\" -TestPinsJson \"{escapedJson}\" -JsonOutput");
        Assert.Equal(0, result.ExitCode);

        return root;
    }
}
