using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Monitoring;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseATests
{
    public sealed class DxvkModernApiCompatibilityTests
    {
        private sealed class FakeModuleScanner : ModuleScanner
        {
            private readonly HashSet<string> _modules;

            public FakeModuleScanner(params string[] modules)
            {
                _modules = new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase);
            }

            public override HashSet<string> GetLoadedGraphicsModules(Process process) => _modules;
        }

        private sealed class FakePeParser : PeParser
        {
            private readonly IEnumerable<string> _imports;
            private readonly string _arch;

            public FakePeParser(IEnumerable<string> imports, string arch = "x64")
            {
                _imports = imports;
                _arch = arch;
            }

            public override IEnumerable<string> GetImports(string path) => _imports;
            public override string GetArchitecture(string path) => _arch;
        }

        [Fact]
        public void ApiClassifier_RuntimeModules_DetectsDX12AndVulkanDistinctly()
        {
            using var proc = Process.GetCurrentProcess();
            var parser = new FakePeParser(Array.Empty<string>());

            // 1. DX12 alone
            var scanner12 = new FakeModuleScanner("d3d12.dll");
            var classifier12 = new ApiClassifier(scanner12, parser);
            var result12 = classifier12.ClassifyDetailed(proc, proc.MainModule?.FileName);

            Assert.Equal(GraphicsApi.DX12, result12.PrimaryApi);
            Assert.Equal(ApiDetectionConfidence.High, result12.Confidence);
            Assert.Contains(result12.Evidence, e => e.Contains("d3d12.dll"));
            Assert.Single(result12.ObservedApis);
            Assert.Contains(GraphicsApi.DX12, result12.ObservedApis);

            // 2. Vulkan alone
            var scannerVk = new FakeModuleScanner("vulkan-1.dll");
            var classifierVk = new ApiClassifier(scannerVk, parser);
            var resultVk = classifierVk.ClassifyDetailed(proc, proc.MainModule?.FileName);

            Assert.Equal(GraphicsApi.Vulkan, resultVk.PrimaryApi);
            Assert.Equal(ApiDetectionConfidence.High, resultVk.Confidence);
            Assert.Contains(resultVk.Evidence, e => e.Contains("vulkan-1.dll"));
            Assert.Single(resultVk.ObservedApis);
            Assert.Contains(GraphicsApi.Vulkan, resultVk.ObservedApis);

            // 3. DX12 + Vulkan combination
            var scannerBoth = new FakeModuleScanner("d3d12.dll", "vulkan-1.dll");
            var classifierBoth = new ApiClassifier(scannerBoth, parser);
            var resultBoth = classifierBoth.ClassifyDetailed(proc, proc.MainModule?.FileName);

            Assert.Equal(GraphicsApi.DX12, resultBoth.PrimaryApi);
            Assert.Equal(ApiDetectionConfidence.High, resultBoth.Confidence);
            Assert.Contains(GraphicsApi.DX12, resultBoth.ObservedApis);
            Assert.Contains(GraphicsApi.Vulkan, resultBoth.ObservedApis);

            // 4. DX11 + Vulkan combination
            var scanner11Vk = new FakeModuleScanner("d3d11.dll", "vulkan-1.dll");
            var classifier11Vk = new ApiClassifier(scanner11Vk, parser);
            var result11Vk = classifier11Vk.ClassifyDetailed(proc, proc.MainModule?.FileName);

            Assert.Equal(GraphicsApi.Vulkan, result11Vk.PrimaryApi);
            Assert.Equal(ApiDetectionConfidence.High, result11Vk.Confidence);
            Assert.Contains(GraphicsApi.Vulkan, result11Vk.ObservedApis);
            Assert.Contains(GraphicsApi.DX11, result11Vk.ObservedApis);
        }

        [Fact]
        public void ApiClassifier_StaticImports_DetectsDX12AndVulkanDistinctly()
        {
            using var tempDir = new SyntheticTestDirectory();
            var fakeExe = tempDir.CreateFile("Game.exe", "synthetic-binary");
            using var proc = Process.GetCurrentProcess();
            var scanner = new FakeModuleScanner();

            // 1. DX12 PE import
            var parser12 = new FakePeParser(new[] { "d3d12.dll", "kernel32.dll" });
            var classifier12 = new ApiClassifier(scanner, parser12);
            var result12 = classifier12.ClassifyDetailed(proc, fakeExe);

            Assert.Equal(GraphicsApi.DX12, result12.PrimaryApi);
            Assert.Equal(ApiDetectionConfidence.Medium, result12.Confidence);
            Assert.Contains(result12.Evidence, e => e.Contains("d3d12.dll"));
            Assert.Single(result12.ObservedApis);
            Assert.Contains(GraphicsApi.DX12, result12.ObservedApis);

            // 2. Vulkan PE import
            var parserVk = new FakePeParser(new[] { "vulkan-1.dll", "kernel32.dll" });
            var classifierVk = new ApiClassifier(scanner, parserVk);
            var resultVk = classifierVk.ClassifyDetailed(proc, fakeExe);

            Assert.Equal(GraphicsApi.Vulkan, resultVk.PrimaryApi);
            Assert.Equal(ApiDetectionConfidence.Medium, resultVk.Confidence);
            Assert.Contains(resultVk.Evidence, e => e.Contains("vulkan-1.dll"));
            Assert.Single(resultVk.ObservedApis);
            Assert.Contains(GraphicsApi.Vulkan, resultVk.ObservedApis);

            // 3. DX12 + Vulkan PE imports
            var parserBoth = new FakePeParser(new[] { "d3d12.dll", "vulkan-1.dll" });
            var classifierBoth = new ApiClassifier(scanner, parserBoth);
            var resultBoth = classifierBoth.ClassifyDetailed(proc, fakeExe);

            Assert.Equal(GraphicsApi.DX12, resultBoth.PrimaryApi);
            Assert.Equal(ApiDetectionConfidence.Medium, resultBoth.Confidence);
            Assert.Contains(GraphicsApi.DX12, resultBoth.ObservedApis);
            Assert.Contains(GraphicsApi.Vulkan, resultBoth.ObservedApis);

            // 4. DX11 + Vulkan PE imports
            var parser11Vk = new FakePeParser(new[] { "d3d11.dll", "vulkan-1.dll" });
            var classifier11Vk = new ApiClassifier(scanner, parser11Vk);
            var result11Vk = classifier11Vk.ClassifyDetailed(proc, fakeExe);

            Assert.Equal(GraphicsApi.Vulkan, result11Vk.PrimaryApi);
            Assert.Equal(ApiDetectionConfidence.Medium, result11Vk.Confidence);
            Assert.Contains(GraphicsApi.Vulkan, result11Vk.ObservedApis);
            Assert.Contains(GraphicsApi.DX11, result11Vk.ObservedApis);
        }

        [Theory]
        [InlineData(GraphicsApi.DX12)]
        [InlineData(GraphicsApi.Vulkan)]
        [InlineData(GraphicsApi.ModernAPI)]
        [InlineData(GraphicsApi.Unknown)]
        public void DxvkCompatibility_UnsupportedApis_ReportNotSupportedAndEmptyDlls(GraphicsApi api)
        {
            Assert.False(DxvkCompatibility.IsDxvkSupported(api));
            Assert.Empty(DxvkCompatibility.GetRequiredDlls(api));
        }

        [Theory]
        [InlineData(GraphicsApi.DX9, "d3d9.dll")]
        [InlineData(GraphicsApi.DX10, "d3d11.dll", "dxgi.dll")]
        [InlineData(GraphicsApi.DX11, "d3d11.dll", "dxgi.dll")]
        public void DxvkCompatibility_SupportedApis_ReportSupportedAndExpectedDlls(GraphicsApi api, params string[] expectedDlls)
        {
            Assert.True(DxvkCompatibility.IsDxvkSupported(api));
            var dlls = DxvkCompatibility.GetRequiredDlls(api);
            Assert.Equal(expectedDlls.Length, dlls.Length);
            foreach (var expected in expectedDlls)
            {
                Assert.Contains(expected, dlls);
            }
        }

        [Theory]
        [InlineData(GraphicsApi.DX12)]
        [InlineData(GraphicsApi.Vulkan)]
        [InlineData(GraphicsApi.ModernAPI)]
        [InlineData(GraphicsApi.Unknown)]
        public async Task DxvkInstaller_ApplyToGameAsync_RefusesDeploymentForUnsupportedApis(GraphicsApi api)
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
            Directory.CreateDirectory(dxvkArchDir);
            File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-d3d11");
            File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-dxgi");
            File.WriteAllText(Path.Combine(dxvkArchDir, "d3d9.dll"), "dxvk-d3d9");

            var exePath = gameDir.CreateFile("Game.exe", "binary");
            var store = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            using var http = new HttpClient();
            var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);

            var profile = new GameProfile(exePath)
            {
                Api = api,
                Architecture = "x64"
            };
            var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

            bool result = await installer.ApplyToGameAsync(profile, release);

            Assert.False(result);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d9.dll")));
        }

        [Fact]
        public async Task DxvkInstaller_ApplyToGameAsync_DeploysExpectedDllsForSupportedApis()
        {
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
            Directory.CreateDirectory(dxvkArchDir);
            File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-d3d11");
            File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-dxgi");
            File.WriteAllText(Path.Combine(dxvkArchDir, "d3d9.dll"), "dxvk-d3d9");

            var store = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            using var http = new HttpClient();
            var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
            var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

            // 1. Positive DX9 deployment: deploys d3d9.dll only
            using (var gameDir9 = new SyntheticTestDirectory())
            {
                var exePath9 = gameDir9.CreateFile("Game9.exe", "binary");
                var profile9 = new GameProfile(exePath9) { Api = GraphicsApi.DX9, Architecture = "x64" };
                bool ok9 = await installer.ApplyToGameAsync(profile9, release);
                Assert.True(ok9);
                Assert.True(File.Exists(Path.Combine(gameDir9.RootPath, "d3d9.dll")));
                Assert.False(File.Exists(Path.Combine(gameDir9.RootPath, "d3d11.dll")));
                Assert.False(File.Exists(Path.Combine(gameDir9.RootPath, "dxgi.dll")));
            }

            // 2. Positive DX10 deployment: deploys d3d11.dll and dxgi.dll
            using (var gameDir10 = new SyntheticTestDirectory())
            {
                var exePath10 = gameDir10.CreateFile("Game10.exe", "binary");
                var profile10 = new GameProfile(exePath10) { Api = GraphicsApi.DX10, Architecture = "x64" };
                bool ok10 = await installer.ApplyToGameAsync(profile10, release);
                Assert.True(ok10);
                Assert.True(File.Exists(Path.Combine(gameDir10.RootPath, "d3d11.dll")));
                Assert.True(File.Exists(Path.Combine(gameDir10.RootPath, "dxgi.dll")));
                Assert.False(File.Exists(Path.Combine(gameDir10.RootPath, "d3d9.dll")));
            }

            // 3. Positive DX11 deployment: deploys d3d11.dll and dxgi.dll
            using (var gameDir11 = new SyntheticTestDirectory())
            {
                var exePath11 = gameDir11.CreateFile("Game11.exe", "binary");
                var profile11 = new GameProfile(exePath11) { Api = GraphicsApi.DX11, Architecture = "x64" };
                bool ok11 = await installer.ApplyToGameAsync(profile11, release);
                Assert.True(ok11);
                Assert.True(File.Exists(Path.Combine(gameDir11.RootPath, "d3d11.dll")));
                Assert.True(File.Exists(Path.Combine(gameDir11.RootPath, "dxgi.dll")));
                Assert.False(File.Exists(Path.Combine(gameDir11.RootPath, "d3d9.dll")));
            }
        }

        [Theory]
        [InlineData(GraphicsApi.DX12)]
        [InlineData(GraphicsApi.Vulkan)]
        [InlineData(GraphicsApi.ModernAPI)]
        [InlineData(GraphicsApi.Unknown)]
        public async Task DxvkInstaller_ReapplyAsync_RefusesReapplyOnManagedGameWhenApiUnsupported_AndPreservesGameFiles(GraphicsApi unsupportedApi)
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
            Directory.CreateDirectory(dxvkArchDir);
            File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-content");
            File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-content");

            var exePath = gameDir.CreateFile("Game.exe", "binary");
            var store = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            using var http = new HttpClient();
            var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);

            var profile = new GameProfile(exePath)
            {
                Api = GraphicsApi.DX11,
                Architecture = "x64"
            };
            var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

            // Step 1: Establish genuine managed installation under supported DX11
            bool initialInstall = await installer.ApplyToGameAsync(profile, release);
            Assert.True(initialInstall);

            var installation = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(installation);
            Assert.Equal("2.5", installation!.ManagedDxvkVersion);

            string d3d11Path = Path.Combine(gameDir.RootPath, "d3d11.dll");
            string dxgiPath = Path.Combine(gameDir.RootPath, "dxgi.dll");
            Assert.True(File.Exists(d3d11Path));
            Assert.True(File.Exists(dxgiPath));
            string initialD3D11Content = File.ReadAllText(d3d11Path);
            string initialDxgiContent = File.ReadAllText(dxgiPath);

            // Step 2: Attempt reapply when API is reclassified or set to unsupported API
            profile.Api = unsupportedApi;
            bool reapplyResult = await installer.ReapplyAsync(profile, updateBaseline: false);

            Assert.False(reapplyResult);
            // Verify existing game files are completely preserved
            Assert.Equal(initialD3D11Content, File.ReadAllText(d3d11Path));
            Assert.Equal(initialDxgiContent, File.ReadAllText(dxgiPath));

            // Step 3: Positive control using the exact same fixture under DX11
            profile.Api = GraphicsApi.DX11;
            bool positiveReapply = await installer.ReapplyAsync(profile, updateBaseline: false);
            Assert.True(positiveReapply);
        }

        [Theory]
        [InlineData(GraphicsApi.DX12)]
        [InlineData(GraphicsApi.Vulkan)]
        [InlineData(GraphicsApi.ModernAPI)]
        [InlineData(GraphicsApi.Unknown)]
        public async Task DxvkManager_RequestEnableAndReapply_FailsAndDoesNotQueueForUnsupportedApis(GraphicsApi api)
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var exePath = gameDir.CreateFile("Game.exe", "binary");
            var store = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            using var http = new HttpClient();
            var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
            var rollback = new DxvkRollback(engine, store);
            var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "games.json"));
            var cacheStore = new CacheStore(Path.Combine(storageDir.RootPath, "cache.json"));
            var github = new DxvkGithubClient(http, cacheStore);
            var manager = new DxvkManager(installer, rollback, github, profileStore, store);

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = api;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            // Request Enable via path
            var enableResult = await manager.RequestEnableByPathAsync(profile);
            Assert.Equal(DxvkActionResult.Failed, enableResult);

            // Request Reapply via path
            var reapplyResult = await manager.RequestReapplyByPathAsync(profile);
            Assert.Equal(DxvkActionResult.Failed, reapplyResult);

            // Verify with running process handle
            using var proc = Process.GetCurrentProcess();
            var liveEnableResult = await manager.RequestEnableAsync(profile, proc);
            Assert.Equal(DxvkActionResult.Failed, liveEnableResult);

            var liveReapplyResult = await manager.RequestReapplyAsync(profile, proc);
            Assert.Equal(DxvkActionResult.Failed, liveReapplyResult);

            // Ensure no pending action was queued
            var installation = store.FindByInstallationPath(gameDir.RootPath);
            Assert.True(installation == null || installation.PendingAction == null);

            // UpdateAvailable must also be false
            var release = new ReleaseInfo { Version = "2.5" };
            Assert.False(manager.UpdateAvailable(profile, release));
        }

        [Fact]
        public async Task DxvkManager_ApplyPendingAsync_And_ProcessAllPendingActionsAsync_BlocksExecutionForUnsupportedApis()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
            Directory.CreateDirectory(releaseDir);
            File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.5-d3d11");
            File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.5-dxgi");

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

            var store = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "games.json"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            using var http = new HttpClient();
            var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
            var rollback = new DxvkRollback(engine, store);
            var github = new DxvkGithubClient(http, new CacheStore(Path.Combine(storageDir.RootPath, "cache.json")));
            var manager = new DxvkManager(installer, rollback, github, profileStore, store);

            // 1. Unsupported API (DX12) with Pending Install
            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX12;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "Game");
            installation.GetOrAddExecutable("Game.exe", "Game");
            installation.PendingAction = PendingAction.Install("2.5", "Queued offline");
            store.Save(installation);

            // ApplyPendingAsync must return false and NOT clear pending action
            bool directApplyOk = await manager.ApplyPendingAsync(exePath);
            Assert.False(directApplyOk);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));

            var instAfterDirect = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfterDirect?.PendingAction);
            Assert.True(instAfterDirect!.PendingAction!.IsPending);

            // ProcessAllPendingActionsAsync must return 0 and not execute
            int processed = await manager.ProcessAllPendingActionsAsync();
            Assert.Equal(0, processed);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));

            var instAfterBatch = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfterBatch?.PendingAction);
            Assert.True(instAfterBatch!.PendingAction!.IsPending);

            // 2. Unsupported API (Vulkan) with Pending Reapply
            profile.Api = GraphicsApi.Vulkan;
            profileStore.Save(profile);
            installation.PendingAction = PendingAction.Reapply("2.5", "Queued reapply");
            store.Save(installation);

            bool reapplyPendingOk = await manager.ApplyPendingAsync(exePath);
            Assert.False(reapplyPendingOk);

            int processedVk = await manager.ProcessAllPendingActionsAsync();
            Assert.Equal(0, processedVk);

            // 3. Positive control: supported DX11 API executes pending action cleanly
            profile.Api = GraphicsApi.DX11;
            profileStore.Save(profile);
            installation.PendingAction = PendingAction.Install("2.5", "Queued for DX11");
            store.Save(installation);

            int executedPositive = await manager.ProcessAllPendingActionsAsync();
            Assert.Equal(1, executedPositive);

            var instAfterPositive = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfterPositive);
            Assert.Null(instAfterPositive!.PendingAction);
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
        }

        [Fact]
        public async Task DxvkManager_DisableDxvkAsync_PreservedWhenGameReclassifiedAsDX12OrVulkan()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
            Directory.CreateDirectory(dxvkArchDir);
            File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
            File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
            // Native original game DLL
            var d3d11Path = gameDir.CreateFile("d3d11.dll", "original-native-d3d11");

            var store = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "games.json"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            using var http = new HttpClient();
            var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
            var rollback = new DxvkRollback(engine, store);
            var github = new DxvkGithubClient(http, new CacheStore(Path.Combine(storageDir.RootPath, "cache.json")));
            var manager = new DxvkManager(installer, rollback, github, profileStore, store);

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            // Deploy DXVK under DX11
            var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
            bool enabled = await manager.EnableDxvkAsync(profile, "2.5");
            Assert.True(enabled);
            Assert.True(profile.DxvkEnabled);
            Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));

            // Later, game is reclassified to DX12
            profile.Api = GraphicsApi.DX12;
            profileStore.Save(profile);

            // Safety-critical invariant: DisableDxvkAsync MUST succeed and restore original DLLs!
            bool disabled = await manager.DisableDxvkAsync(profile);
            Assert.True(disabled);
            Assert.False(profile.DxvkEnabled);
            Assert.Equal("original-native-d3d11", File.ReadAllText(d3d11Path));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
        }

        [Fact]
        public async Task DxvkManager_AdoptExistingAsync_RefusesAdoptionForDX12AndVulkan_AndAllowsSupportedApi()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
            Directory.CreateDirectory(releaseDir);
            File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "official-dxvk-2.5-d3d11");
            File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "official-dxvk-2.5-dxgi");

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
            gameDir.CreateFile("d3d11.dll", "official-dxvk-2.5-d3d11");
            gameDir.CreateFile("dxgi.dll", "official-dxvk-2.5-dxgi");

            var store = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            using var http = new HttpClient();
            var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
            var rollback = new DxvkRollback(engine, store);
            var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "games.json"));
            var cacheStore = new CacheStore(Path.Combine(storageDir.RootPath, "cache.json"));
            var github = new DxvkGithubClient(http, cacheStore);
            var manager = new DxvkManager(installer, rollback, github, profileStore, store);

            // Refuses adoption for DX12
            var profile12 = profileStore.GetOrCreate(exePath);
            profile12.Api = GraphicsApi.DX12;
            profile12.Architecture = "x64";
            Assert.False(await manager.AdoptExistingAsync(profile12));
            Assert.False(profile12.DxvkEnabled);

            // Refuses adoption for Vulkan
            var profileVk = profileStore.GetOrCreate(exePath);
            profileVk.Api = GraphicsApi.Vulkan;
            profileVk.Architecture = "x64";
            Assert.False(await manager.AdoptExistingAsync(profileVk));
            Assert.False(profileVk.DxvkEnabled);

            // Refuses adoption for ModernAPI
            var profileMod = profileStore.GetOrCreate(exePath);
            profileMod.Api = GraphicsApi.ModernAPI;
            profileMod.Architecture = "x64";
            Assert.False(await manager.AdoptExistingAsync(profileMod));

            // Positive control: allows adoption for DX11
            var profile11 = profileStore.GetOrCreate(exePath);
            profile11.Api = GraphicsApi.DX11;
            profile11.Architecture = "x64";
            Assert.True(await manager.AdoptExistingAsync(profile11));
            Assert.True(profile11.DxvkEnabled);
            Assert.Equal("2.5", profile11.DxvkVersion);
        }

        [Theory]
        [InlineData(GraphicsApi.DX12)]
        [InlineData(GraphicsApi.Vulkan)]
        [InlineData(GraphicsApi.ModernAPI)]
        [InlineData(GraphicsApi.Unknown)]
        public void DxvkInstaller_AdoptExisting_DirectCall_RefusesUnsupportedApis(GraphicsApi api)
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
            var store = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            using var http = new HttpClient();
            var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);

            var profile = new GameProfile(exePath)
            {
                Api = api,
                Architecture = "x64"
            };

            var assessment = new ExistingDxvkAssessment
            {
                Status = ExistingDxvkStatus.OfficialRelease,
                MatchedVersion = "2.5",
                DetectedDlls = new List<string> { "d3d11.dll", "dxgi.dll" }
            };

            Assert.False(installer.AdoptExisting(profile, assessment));
        }
    }
}
