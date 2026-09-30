using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Monitoring;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class CompatibilityAndCorrectnessTests
    {
        [Fact]
        public void DxvkCapabilityMatrix_MapsAllApisCorrectly()
        {
            // D3D8 -> d3d8.dll + d3d9.dll
            var d3d8 = DxvkCapabilityMatrix.GetDescriptor(GraphicsApi.D3D8);
            Assert.True(d3d8.IsSupported);
            Assert.Equal(new[] { "d3d8.dll", "d3d9.dll" }, d3d8.RequiredDlls);

            // D3D9 -> d3d9.dll
            var d3d9 = DxvkCapabilityMatrix.GetDescriptor(GraphicsApi.D3D9);
            Assert.True(d3d9.IsSupported);
            Assert.Equal(new[] { "d3d9.dll" }, d3d9.RequiredDlls);

            // D3D10 -> d3d10core.dll + d3d11.dll + dxgi.dll
            var d3d10 = DxvkCapabilityMatrix.GetDescriptor(GraphicsApi.D3D10);
            Assert.True(d3d10.IsSupported);
            Assert.Equal(new[] { "d3d10core.dll", "d3d11.dll", "dxgi.dll" }, d3d10.RequiredDlls);

            // D3D11 -> d3d11.dll + dxgi.dll
            var d3d11 = DxvkCapabilityMatrix.GetDescriptor(GraphicsApi.D3D11);
            Assert.True(d3d11.IsSupported);
            Assert.Equal(new[] { "d3d11.dll", "dxgi.dll" }, d3d11.RequiredDlls);

            // D3D12 -> Observe only
            var d3d12 = DxvkCapabilityMatrix.GetDescriptor(GraphicsApi.D3D12);
            Assert.False(d3d12.IsSupported);
            Assert.Empty(d3d12.RequiredDlls);

            // Vulkan -> Observe only
            var vulkan = DxvkCapabilityMatrix.GetDescriptor(GraphicsApi.Vulkan);
            Assert.False(vulkan.IsSupported);
            Assert.Empty(vulkan.RequiredDlls);

            // ModernAPI -> Observe only
            var modern = DxvkCapabilityMatrix.GetDescriptor(GraphicsApi.ModernAPI);
            Assert.False(modern.IsSupported);
            Assert.Empty(modern.RequiredDlls);

            // Unknown -> Observe only
            var unknown = DxvkCapabilityMatrix.GetDescriptor(GraphicsApi.Unknown);
            Assert.False(unknown.IsSupported);
            Assert.Empty(unknown.RequiredDlls);
        }

        [Fact]
        public async Task Installer_RefusesToDeployToD3D12AndVulkan()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            string exePath = gameDir.CreateFile("Game.exe", "fake-exe");

            var libraryStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            var installer = new DxvkInstaller(new HttpClient(), engine, libraryStore, sourceDir.RootPath);

            // Test D3D12
            var profileD3D12 = new GameProfile(exePath)
            {
                Api = GraphicsApi.D3D12,
                Architecture = "x64"
            };
            var release = new ReleaseInfo { Version = "2.6" };

            bool d3d12Result = await installer.ApplyToGameAsync(profileD3D12, release);
            Assert.False(d3d12Result);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));

            // Test Vulkan
            var profileVulkan = new GameProfile(exePath)
            {
                Api = GraphicsApi.Vulkan,
                Architecture = "x64"
            };

            bool vulkanResult = await installer.ApplyToGameAsync(profileVulkan, release);
            Assert.False(vulkanResult);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
        }

        [Fact]
        public async Task Installer_DeploysAndRestoresD3D10CompleteSet()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            string releaseDir = Path.Combine(sourceDir.RootPath, "2.6", "x64");
            Directory.CreateDirectory(releaseDir);
            File.WriteAllText(Path.Combine(releaseDir, "d3d10core.dll"), "dxvk-d3d10core");
            File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-d3d11");
            File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-dxgi");

            string exePath = gameDir.CreateFile("Game.exe", "fake-exe");

            var libraryStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            var installer = new DxvkInstaller(new HttpClient(), engine, libraryStore, sourceDir.RootPath);
            var rollback = new DxvkRollback(engine, libraryStore);

            var profile = new GameProfile(exePath)
            {
                Api = GraphicsApi.D3D10,
                Architecture = "x64"
            };

            // Apply D3D10
            bool ok = await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.6" });
            Assert.True(ok);

            // Verify all 3 D3D10 files deployed
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d10core.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));

            // Rollback
            bool restored = await rollback.RestoreOriginalDllsAsync(profile);
            Assert.True(restored);

            // Clean restoration: none of the 3 existed before, so none should remain
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d10core.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
        }

        [Fact]
        public async Task Installer_DeploysAndRestoresD3D8CompleteSet()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            string releaseDir = Path.Combine(sourceDir.RootPath, "2.6", "x32");
            Directory.CreateDirectory(releaseDir);
            File.WriteAllText(Path.Combine(releaseDir, "d3d8.dll"), "dxvk-d3d8");
            File.WriteAllText(Path.Combine(releaseDir, "d3d9.dll"), "dxvk-d3d9");

            string exePath = gameDir.CreateFile("Game.exe", "fake-exe");

            var libraryStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            var installer = new DxvkInstaller(new HttpClient(), engine, libraryStore, sourceDir.RootPath);
            var rollback = new DxvkRollback(engine, libraryStore);

            var profile = new GameProfile(exePath)
            {
                Api = GraphicsApi.D3D8,
                Architecture = "x32"
            };

            // Apply D3D8
            bool ok = await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.6" });
            Assert.True(ok);

            // Verify both D3D8 files deployed
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d8.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d9.dll")));

            // Rollback
            bool restored = await rollback.RestoreOriginalDllsAsync(profile);
            Assert.True(restored);

            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d8.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d9.dll")));
        }

        [Fact]
        public async Task Installer_Reapply_PreservesDurableBaselineOnTransactionFailure()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            string backupsDir = Path.Combine(storageDir.RootPath, "backups");
            string releaseDir = Path.Combine(sourceDir.RootPath, "2.6", "x64");
            Directory.CreateDirectory(releaseDir);
            File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.6-d3d11");
            File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.6-dxgi");

            // Original game had a native d3d11.dll with content "ORIGINAL_BASELINE_A"
            string originalBaselineContent = "ORIGINAL_BASELINE_A";
            string gameD3D11 = gameDir.CreateFile("d3d11.dll", originalBaselineContent);
            string exePath = gameDir.CreateFile("Game.exe", "fake-exe");

            var libraryStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                backupsDir);
            var initialEngine = new MultiFileTransactionEngine(backupsDir);
            var initialInstaller = new DxvkInstaller(new HttpClient(), initialEngine, libraryStore, sourceDir.RootPath);

            var profile = new GameProfile(exePath)
            {
                Api = GraphicsApi.D3D11,
                Architecture = "x64"
            };

            // Initial install creates backup of ORIGINAL_BASELINE_A
            bool initialOk = await initialInstaller.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.6" });
            Assert.True(initialOk);

            var installation = libraryStore.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(installation);
            var managedD3D11 = installation!.FindManagedFile("d3d11.dll");
            Assert.NotNull(managedD3D11);
            string backupRelPath = managedD3D11!.BackupRelativePath!;
            string durableBackupFile = Path.Combine(backupsDir, backupRelPath);

            Assert.True(File.Exists(durableBackupFile));
            Assert.Equal(originalBaselineContent, File.ReadAllText(durableBackupFile));

            // Now simulate game update: game file d3d11.dll changes externally to "GAME_UPDATE_B"
            string gameUpdateContent = "GAME_UPDATE_B";
            File.WriteAllText(gameD3D11, gameUpdateContent);

            // Set up a failing transaction engine to inject failure during reapply
            var failingEngine = new MultiFileTransactionEngine(backupsDir, new MultiFileTransactionTestHooks
            {
                AfterApply = (target, idx) => throw new IOException("Injected power loss / disk error during deployment")
            });
            var failingInstaller = new DxvkInstaller(new HttpClient(), failingEngine, libraryStore, sourceDir.RootPath);

            // Act: Reapply with updateBaseline: true
            bool reapplyOk = await failingInstaller.ReapplyAsync(profile, updateBaseline: true);

            // Assert: Reapply failed
            Assert.False(reapplyOk);

            // CRITICAL INTEGRITY CHECK: Durable backup A must NOT have been destroyed or overwritten with B!
            Assert.True(File.Exists(durableBackupFile));
            string backupContentAfterFailure = File.ReadAllText(durableBackupFile);
            Assert.Equal(originalBaselineContent, backupContentAfterFailure);
        }

        [Fact]
        public async Task PendingAction_ResolvesExactReleaseAtQueueTime_NoLiteralLatest()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            string releaseDir = Path.Combine(sourceDir.RootPath, "2.6.2", "x64");
            Directory.CreateDirectory(releaseDir);
            File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.6.2-d3d11");
            File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.6.2-dxgi");

            string exePath = gameDir.CreateFile("Game.exe", "fake-exe");

            var libraryStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "games.json"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            var installer = new DxvkInstaller(new HttpClient(), engine, libraryStore, sourceDir.RootPath);
            var rollback = new DxvkRollback(engine, libraryStore);

            // Create a fake HTTP client that returns a valid release for latest
            var cacheStore = new CacheStore();
            cacheStore.SaveLatest(new CachedRelease
            {
                Release = new ReleaseInfo
                {
                    Version = "2.6.2",
                    DownloadUrl = "https://github.com/doitsujin/dxvk/releases/download/v2.6.2/dxvk-2.6.2.tar.gz"
                },
                CachedAt = DateTime.UtcNow
            });
            var github = new DxvkGithubClient(new HttpClient(), cacheStore);

            var manager = new DxvkManager(installer, rollback, github, profileStore, libraryStore);

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.D3D11;
            profile.Architecture = "x64";
            profile.DxvkVersion = null; // New unmanaged game
            profileStore.Save(profile);

            // Simulate game is currently running when enable requested
            using var dummyProcess = Process.GetCurrentProcess(); // Live running process
            var result = await manager.RequestEnableAsync(profile, dummyProcess);
            Assert.Equal(DxvkActionResult.Queued, result);

            var installation = libraryStore.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(installation);
            Assert.NotNull(installation!.PendingAction);

            // CRITICAL CHECK: TargetDxvkVersion must NOT be literal "latest"!
            Assert.NotEqual("latest", installation.PendingAction!.TargetDxvkVersion);
            Assert.Equal("2.6.2", installation.PendingAction.TargetDxvkVersion);
            Assert.Equal("https://github.com/doitsujin/dxvk/releases/download/v2.6.2/dxvk-2.6.2.tar.gz", installation.PendingAction.DownloadUrl);
            Assert.Contains("d3d11.dll", installation.PendingAction.RequiredDlls);
            Assert.Contains("dxgi.dll", installation.PendingAction.RequiredDlls);

            // When game exits, ApplyPendingAsync applies the resolved release deterministically
            bool applied = await manager.ApplyPendingAsync(exePath);
            Assert.True(applied);

            var updatedInstallation = libraryStore.FindByInstallationPath(gameDir.RootPath);
            Assert.Null(updatedInstallation!.PendingAction);
            Assert.Equal("2.6.2", updatedInstallation.ManagedDxvkVersion);
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
        }

        [Fact]
        public async Task PendingAction_RequestUpdate_ResolvesTargetVersionAtQueueTime()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();

            string exePath = gameDir.CreateFile("Game.exe", "fake-exe");

            var libraryStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "games.json"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            var installer = new DxvkInstaller(new HttpClient(), engine, libraryStore);
            var rollback = new DxvkRollback(engine, libraryStore);

            var cacheStore = new CacheStore();
            cacheStore.SaveLatest(new CachedRelease
            {
                Release = new ReleaseInfo
                {
                    Version = "2.6.2",
                    DownloadUrl = "https://fake/dxvk-2.6.2.tar.gz"
                },
                CachedAt = DateTime.UtcNow
            });
            var github = new DxvkGithubClient(new HttpClient(), cacheStore);

            var manager = new DxvkManager(installer, rollback, github, profileStore, libraryStore);

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.D3D11;
            profile.Architecture = "x64";
            profile.DxvkVersion = "2.5"; // Running old version
            profile.DxvkEnabled = true;
            profileStore.Save(profile);

            using var dummyProcess = Process.GetCurrentProcess();
            var result = await manager.RequestUpdateAsync(profile, dummyProcess);
            Assert.Equal(DxvkActionResult.Queued, result);

            var installation = libraryStore.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(installation?.PendingAction);
            Assert.Equal(PendingActionType.Update, installation!.PendingAction!.Type);
            Assert.Equal("2.6.2", installation.PendingAction.TargetDxvkVersion);
        }

        [Fact]
        public void ApiClassifier_ClassifiesDiscreteApisCorrectly()
        {
            var parser = new PeParser();

            // Mock ModuleScanner
            var scannerD3D8 = new TestModuleScanner(new[] { "d3d8.dll", "d3d9.dll" });
            var classifierD3D8 = new ApiClassifier(scannerD3D8, parser);
            using var p = Process.GetCurrentProcess();
            Assert.Equal(GraphicsApi.D3D8, classifierD3D8.Classify(p));

            var scannerD3D10 = new TestModuleScanner(new[] { "d3d10core.dll", "d3d11.dll" });
            var classifierD3D10 = new ApiClassifier(scannerD3D10, parser);
            Assert.Equal(GraphicsApi.D3D10, classifierD3D10.Classify(p));

            var scannerD3D12 = new TestModuleScanner(new[] { "d3d12.dll", "dxgi.dll" });
            var classifierD3D12 = new ApiClassifier(scannerD3D12, parser);
            Assert.Equal(GraphicsApi.D3D12, classifierD3D12.Classify(p));

            var scannerVulkan = new TestModuleScanner(new[] { "vulkan-1.dll" });
            var classifierVulkan = new ApiClassifier(scannerVulkan, parser);
            Assert.Equal(GraphicsApi.Vulkan, classifierVulkan.Classify(p));
        }

        [Fact]
        public void ExistingDxvkDetector_ScopedAdoption_DoesNotAdoptForeignDlls()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            string releaseDir = Path.Combine(sourceDir.RootPath, "2.6", "x64");
            Directory.CreateDirectory(releaseDir);
            File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "official-dxvk-d3d11");
            File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "official-dxvk-dxgi");

            // Game dir contains official DXVK d3d11 and dxgi, but ALSO a native d3d9.dll
            gameDir.CreateFile("d3d11.dll", "official-dxvk-d3d11");
            gameDir.CreateFile("dxgi.dll", "official-dxvk-dxgi");
            gameDir.CreateFile("d3d9.dll", "native-game-d3d9");

            var detector = new ExistingDxvkDetector(sourceDir.RootPath);

            // Assess for D3D11 API
            var assessment = detector.AssessDirectory(gameDir.RootPath, "x64", GraphicsApi.D3D11);

            Assert.Equal(ExistingDxvkStatus.OfficialRelease, assessment.Status);
            Assert.Equal("2.6", assessment.MatchedVersion);

            // Must only detect d3d11 and dxgi — NEVER foreign d3d9.dll!
            Assert.Contains("d3d11.dll", assessment.DetectedDlls);
            Assert.Contains("dxgi.dll", assessment.DetectedDlls);
            Assert.DoesNotContain("d3d9.dll", assessment.DetectedDlls);
        }

        [Fact]
        public async Task Reapply_RemovesDxvkConfWhenDisabled()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            string releaseDir = Path.Combine(sourceDir.RootPath, "2.6", "x64");
            Directory.CreateDirectory(releaseDir);
            File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-d3d11");
            File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-dxgi");

            string exePath = gameDir.CreateFile("Game.exe", "fake-exe");

            var libraryStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            var installer = new DxvkInstaller(new HttpClient(), engine, libraryStore, sourceDir.RootPath);

            var profile = new GameProfile(exePath)
            {
                Api = GraphicsApi.D3D11,
                Architecture = "x64",
                HudEnabled = true // Initially HUD is enabled, so dxvk.conf is created
            };

            bool ok = await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.6" });
            Assert.True(ok);
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxvk.conf")));

            // Now user disables HUD and framerate limit
            profile.HudEnabled = false;
            profile.FrameLimit = 0;

            bool reapplyOk = await installer.ReapplyAsync(profile);
            Assert.True(reapplyOk);

            // dxvk.conf should be cleanly removed because Companion created it and config is now empty
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxvk.conf")));
        }

        [Fact]
        public void MultiFileTransactionEngine_RecoverInterruptedTransactions_RecoversState()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();

            string backupsDir = Path.Combine(storageDir.RootPath, "backups");
            Directory.CreateDirectory(backupsDir);

            string gameFile = gameDir.CreateFile("d3d11.dll", "corrupted-or-partial-deploy");
            string backupFile = Path.Combine(backupsDir, "test-backup", "d3d11.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
            File.WriteAllText(backupFile, "original-clean-baseline");

            string planId = Guid.NewGuid().ToString("N");
            var plan = new SafetyTransactionPlan
            {
                TransactionId = planId,
                Operation = TransactionOperation.Install,
                InstallationRoot = gameDir.RootPath,
                State = TransactionState.Applying,
                Files = new[]
                {
                    new SafetyFilePlan
                    {
                        RelativePath = "d3d11.dll",
                        SourceRelativePath = "source.dll",
                        OriginalState = OriginalFileState.Existing,
                        BackupRelativePath = Path.Combine("test-backup", "d3d11.dll")
                    }
                }
            };

            string planPath = Path.Combine(backupsDir, planId + ".json");
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan));

            var engine = new MultiFileTransactionEngine(backupsDir);

            // Act: recover
            int recovered = engine.RecoverInterruptedTransactions();

            // Assert
            Assert.Equal(1, recovered);
            Assert.False(File.Exists(planPath)); // Plan cleaned up
            Assert.Equal("original-clean-baseline", File.ReadAllText(gameFile));
        }

        [Fact]
        public async Task GithubClient_FetchesSpecificReleaseAndParsesExactWindowsAssetAndDigest()
        {
            var mockHandler = new MockHttpMessageHandler(req =>
            {
                if (req.RequestUri!.ToString().Contains("/tags/v2.6.2") || req.RequestUri.ToString().Contains("/tags/2.6.2"))
                {
                    string json = """
                    {
                        "tag_name": "v2.6.2",
                        "assets": [
                            {
                                "name": "dxvk-native-2.6.2-steamrt-sniper.tar.gz",
                                "browser_download_url": "https://github.com/doitsujin/dxvk/releases/download/v2.6.2/dxvk-native-2.6.2.tar.gz",
                                "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000"
                            },
                            {
                                "name": "dxvk-2.6.2.tar.gz",
                                "browser_download_url": "https://github.com/doitsujin/dxvk/releases/download/v2.6.2/dxvk-2.6.2.tar.gz",
                                "digest": "sha256:aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899"
                            }
                        ]
                    }
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json)
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var httpClient = new HttpClient(mockHandler);
            using var cacheDir = new SyntheticTestDirectory();
            var cacheStore = new CacheStore(Path.Combine(cacheDir.RootPath, "cache.json"));
            var client = new DxvkGithubClient(httpClient, cacheStore);

            var release = await client.FetchReleaseByVersionAsync("2.6.2");
            Assert.NotNull(release);
            Assert.Equal("2.6.2", release!.Version);
            Assert.Equal("https://github.com/doitsujin/dxvk/releases/download/v2.6.2/dxvk-2.6.2.tar.gz", release.DownloadUrl);
            Assert.Equal("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899", release.ArchiveSha256);
        }

        [Fact]
        public async Task PendingAction_ResolvesSpecificVersion_WhenUrlMissing_DoesNotDefaultToLatest()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            string releaseDir25 = Path.Combine(sourceDir.RootPath, "2.5", "x64");
            Directory.CreateDirectory(releaseDir25);
            File.WriteAllText(Path.Combine(releaseDir25, "d3d11.dll"), "dxvk-2.5-d3d11");
            File.WriteAllText(Path.Combine(releaseDir25, "dxgi.dll"), "dxvk-2.5-dxgi");

            string exePath = gameDir.CreateFile("Game.exe", "fake-exe");

            var libraryStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "games.json"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            var installer = new DxvkInstaller(new HttpClient(), engine, libraryStore, sourceDir.RootPath);
            var rollback = new DxvkRollback(engine, libraryStore);

            var mockHandler = new MockHttpMessageHandler(req =>
            {
                // Returns 2.5 when tag is queried, and 2.6.2 when latest is queried
                if (req.RequestUri!.ToString().Contains("/tags/"))
                {
                    string json = """
                    {
                        "tag_name": "v2.5",
                        "assets": [
                            {
                                "name": "dxvk-2.5.tar.gz",
                                "browser_download_url": "https://fake/dxvk-2.5.tar.gz"
                            }
                        ]
                    }
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
                }
                if (req.RequestUri!.ToString().Contains("/latest"))
                {
                    string json = """
                    {
                        "tag_name": "v2.6.2",
                        "assets": [
                            {
                                "name": "dxvk-2.6.2.tar.gz",
                                "browser_download_url": "https://fake/dxvk-2.6.2.tar.gz"
                            }
                        ]
                    }
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var github = new DxvkGithubClient(new HttpClient(mockHandler), new CacheStore(Path.Combine(storageDir.RootPath, "cache.json")));
            var manager = new DxvkManager(installer, rollback, github, profileStore, libraryStore);

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.D3D11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = libraryStore.GetOrCreateInstallation(gameDir.RootPath, "Game");
            // Older pending action has exact version "2.5" but DownloadUrl is empty
            installation.PendingAction = PendingAction.Install("2.5", "Queued from old version");
            libraryStore.Save(installation);

            // Act: ApplyPendingAsync executes
            bool applied = await manager.ApplyPendingAsync(exePath);
            Assert.True(applied);

            // Assert: must NOT have upgraded to latest (2.6.2) — it must remain exact target version 2.5!
            var updated = libraryStore.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(updated);
            Assert.Equal("2.5", updated!.ManagedDxvkVersion);
        }

        [Fact]
        public async Task PendingAction_AbortsWhenArchitectureMismatched()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();

            string exePath = gameDir.CreateFile("Game.exe", "fake-exe");

            var libraryStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "games.json"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            var installer = new DxvkInstaller(new HttpClient(), engine, libraryStore);
            var rollback = new DxvkRollback(engine, libraryStore);
            var github = new DxvkGithubClient(new HttpClient(), new CacheStore());
            var manager = new DxvkManager(installer, rollback, github, profileStore, libraryStore);

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.D3D11;
            profile.Architecture = "x64"; // Game is 64-bit
            profileStore.Save(profile);

            var installation = libraryStore.GetOrCreateInstallation(gameDir.RootPath, "Game");
            // Action was queued for x32
            installation.PendingAction = PendingAction.Install(
                "2.6",
                "Queued for x32",
                "https://fake/dxvk-2.6.tar.gz",
                null,
                "x32",
                new[] { "d3d11.dll", "dxgi.dll" });
            libraryStore.Save(installation);

            // Act
            bool result = await manager.ApplyPendingAsync(exePath);

            // Assert: Refused because of architecture mismatch
            Assert.False(result);
            var updated = libraryStore.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(updated);
            Assert.Equal(InstallationConflictFlags.Architecture, updated!.ConflictFlags & InstallationConflictFlags.Architecture);
            Assert.Equal(RestorationState.AttentionRequired, updated.RestorationState);
        }

        [Fact]
        public async Task Reapply_RemovesDxvkConfTransactionally_RollsBackOnDllFailure()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            string backupsDir = Path.Combine(storageDir.RootPath, "backups");
            string releaseDir = Path.Combine(sourceDir.RootPath, "2.6", "x64");
            Directory.CreateDirectory(releaseDir);
            File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.6-d3d11");
            File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.6-dxgi");

            string exePath = gameDir.CreateFile("Game.exe", "fake-exe");

            var libraryStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                backupsDir);
            var initialEngine = new MultiFileTransactionEngine(backupsDir);
            var initialInstaller = new DxvkInstaller(new HttpClient(), initialEngine, libraryStore, sourceDir.RootPath);

            var profile = new GameProfile(exePath)
            {
                Api = GraphicsApi.D3D11,
                Architecture = "x64",
                HudEnabled = true
            };

            // Initial apply: dxvk.conf is created
            bool ok = await initialInstaller.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.6" });
            Assert.True(ok);
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxvk.conf")));

            // User turns off HUD and frame limiter
            profile.HudEnabled = false;
            profile.FrameLimit = 0;

            // Inject failure during reapply (e.g. while applying DLLs)
            var failingEngine = new MultiFileTransactionEngine(backupsDir, new MultiFileTransactionTestHooks
            {
                AfterApply = (target, idx) =>
                {
                    if (target.EndsWith(".dll"))
                        throw new IOException("Injected DLL write error");
                }
            });
            var failingInstaller = new DxvkInstaller(new HttpClient(), failingEngine, libraryStore, sourceDir.RootPath);

            bool reapplyOk = await failingInstaller.ReapplyAsync(profile);
            Assert.False(reapplyOk);

            // Transactional rollback check: dxvk.conf was NOT deleted outside the transaction!
            // It remains intact because the transaction failed safely and rolled back!
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxvk.conf")));
            var inst = libraryStore.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(inst!.FindManagedFile("dxvk.conf"));
        }

        [Fact]
        public void CrashRecovery_DoesNotRollBackCommittedTransaction()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();

            string backupsDir = Path.Combine(storageDir.RootPath, "backups");
            Directory.CreateDirectory(backupsDir);

            string gameFile = gameDir.CreateFile("d3d11.dll", "newly-committed-dxvk-file");
            string backupFile = Path.Combine(backupsDir, "test-backup", "d3d11.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
            File.WriteAllText(backupFile, "old-original-file");

            string planId = Guid.NewGuid().ToString("N");
            var plan = new SafetyTransactionPlan
            {
                TransactionId = planId,
                Operation = TransactionOperation.Install,
                InstallationRoot = gameDir.RootPath,
                State = TransactionState.Committed, // Already successfully committed
                Files = new[]
                {
                    new SafetyFilePlan
                    {
                        RelativePath = "d3d11.dll",
                        SourceRelativePath = "source.dll",
                        OriginalState = OriginalFileState.Existing,
                        BackupRelativePath = Path.Combine("test-backup", "d3d11.dll")
                    }
                }
            };

            string planPath = Path.Combine(backupsDir, planId + ".json");
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan));

            var engine = new MultiFileTransactionEngine(backupsDir);

            // Act: recover
            int recovered = engine.RecoverInterruptedTransactions();

            // Assert: Committed transaction plan is cleanly deleted without rolling back the game file
            Assert.Equal(0, recovered);
            Assert.False(File.Exists(planPath));
            Assert.Equal("newly-committed-dxvk-file", File.ReadAllText(gameFile));
        }

        [Fact]
        public void TransactionEngine_RestoresDeletedFile_ByteForByte_WhenFailureOccursAfterDeletion()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            string backupsDir = Path.Combine(storageDir.RootPath, "backups");
            string sourceDll = sourceDir.CreateFile("d3d11.dll", "new-dll-bytes");
            string confPath = gameDir.CreateFile("dxvk.conf", "dxvk.hud = full\ndxvk.numCompilerThreads = 4");
            var originalConfIdentity = FileIdentity.Capture(confPath);

            var engine = new MultiFileTransactionEngine(backupsDir, new MultiFileTransactionTestHooks
            {
                AfterApply = (target, idx) =>
                {
                    // Fail on the second file (after dxvk.conf was already deleted from disk)
                    if (idx == 1)
                        throw new IOException("Injected failure after config deletion");
                }
            });

            var request = new MultiFileTransactionRequest
            {
                InstallationRoot = gameDir.RootPath,
                Operation = TransactionOperation.Reapply,
                Files = new[]
                {
                    // File 0: dxvk.conf removed (historical original state: DidNotExist)
                    new MultiFileTransactionFile
                    {
                        RelativePath = "dxvk.conf",
                        Action = FileTransactionAction.RestoreOriginal,
                        OriginalState = OriginalFileState.DidNotExist,
                        ExpectedTargetIdentity = originalConfIdentity
                    },
                    // File 1: d3d11.dll deployed
                    new MultiFileTransactionFile
                    {
                        RelativePath = "d3d11.dll",
                        SourceFilePath = sourceDll,
                        Action = FileTransactionAction.Deploy,
                        OriginalState = OriginalFileState.DidNotExist
                    }
                }
            };

            var result = engine.Execute(request);
            Assert.Equal(TransactionOutcome.SafeFailure, result.Outcome);

            // Assert: dxvk.conf was restored byte-for-byte from rollback snapshot!
            Assert.True(File.Exists(confPath));
            Assert.Equal("dxvk.hud = full\ndxvk.numCompilerThreads = 4", File.ReadAllText(confPath));
            var restoredIdent = FileIdentity.Capture(confPath);
            Assert.Equal(originalConfIdentity, restoredIdent);
        }

        [Fact]
        public void CrashRecovery_CorruptBackup_PreservesJournalAndSetsAttentionRequired()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();

            string backupsDir = Path.Combine(storageDir.RootPath, "backups");
            Directory.CreateDirectory(backupsDir);

            string gameFile = gameDir.CreateFile("d3d11.dll", "partial-write-state");
            string planId = Guid.NewGuid().ToString("N");
            string rollbackDir = Path.Combine(backupsDir, "rollback", planId);
            Directory.CreateDirectory(rollbackDir);
            string rollbackFile = Path.Combine(rollbackDir, "d3d11.dll");
            File.WriteAllText(rollbackFile, "corrupted-content");

            var expectedIdentity = new SafetyFileIdentity("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff", 20);

            var plan = new SafetyTransactionPlan
            {
                TransactionId = planId,
                Operation = TransactionOperation.Update,
                InstallationRoot = gameDir.RootPath,
                State = TransactionState.Applying,
                Files = new[]
                {
                    new SafetyFilePlan
                    {
                        RelativePath = "d3d11.dll",
                        SourceRelativePath = "source.dll",
                        PreTransactionExists = true,
                        RollbackRelativePath = Path.Combine("rollback", planId, "d3d11.dll"),
                        RollbackIdentity = expectedIdentity
                    }
                }
            };

            string planPath = Path.Combine(backupsDir, planId + ".json");
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan));

            var engine = new MultiFileTransactionEngine(backupsDir);

            // Act
            int recovered = engine.RecoverInterruptedTransactions();

            // Assert: recovery must NOT claim success, must NOT delete the plan, and marks AttentionRequired
            Assert.Equal(0, recovered);
            Assert.True(File.Exists(planPath));

            var reloadedPlan = JsonSerializer.Deserialize<SafetyTransactionPlan>(File.ReadAllText(planPath));
            Assert.NotNull(reloadedPlan);
            Assert.Equal(TransactionState.AttentionRequired, reloadedPlan!.State);
        }

        private sealed class TestModuleScanner : ModuleScanner
        {
            private readonly HashSet<string> _modules;
            public TestModuleScanner(IEnumerable<string> modules)
            {
                _modules = new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase);
            }
            public override HashSet<string> GetLoadedGraphicsModules(Process process) => _modules;
        }

        private sealed class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
            public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = handler;
            }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_handler(request));
            }
        }
    }
}
