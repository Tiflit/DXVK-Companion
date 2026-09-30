using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Monitoring;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
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

            // DX12
            var scanner12 = new FakeModuleScanner("d3d12.dll");
            var parser = new FakePeParser(Array.Empty<string>());
            var classifier12 = new ApiClassifier(scanner12, parser);
            var result12 = classifier12.ClassifyDetailed(proc, proc.MainModule?.FileName);

            Assert.Equal(GraphicsApi.DX12, result12.PrimaryApi);
            Assert.Equal(ApiDetectionConfidence.High, result12.Confidence);
            Assert.Contains(result12.Evidence, e => e.Contains("d3d12.dll"));

            // Vulkan
            var scannerVk = new FakeModuleScanner("vulkan-1.dll");
            var classifierVk = new ApiClassifier(scannerVk, parser);
            var resultVk = classifierVk.ClassifyDetailed(proc, proc.MainModule?.FileName);

            Assert.Equal(GraphicsApi.Vulkan, resultVk.PrimaryApi);
            Assert.Equal(ApiDetectionConfidence.High, resultVk.Confidence);
            Assert.Contains(resultVk.Evidence, e => e.Contains("vulkan-1.dll"));
        }

        [Fact]
        public void ApiClassifier_StaticImports_DetectsDX12AndVulkanDistinctly()
        {
            using var tempDir = new SyntheticTestDirectory();
            var fakeExe = tempDir.CreateFile("Game.exe", "synthetic-binary");
            using var proc = Process.GetCurrentProcess();

            // DX12 PE import
            var scanner = new FakeModuleScanner();
            var parser12 = new FakePeParser(new[] { "d3d12.dll", "kernel32.dll" });
            var classifier12 = new ApiClassifier(scanner, parser12);
            var result12 = classifier12.ClassifyDetailed(proc, fakeExe);

            Assert.Equal(GraphicsApi.DX12, result12.PrimaryApi);
            Assert.Equal(ApiDetectionConfidence.Medium, result12.Confidence);
            Assert.Contains(result12.Evidence, e => e.Contains("d3d12.dll"));

            // Vulkan PE import
            var parserVk = new FakePeParser(new[] { "vulkan-1.dll", "kernel32.dll" });
            var classifierVk = new ApiClassifier(scanner, parserVk);
            var resultVk = classifierVk.ClassifyDetailed(proc, fakeExe);

            Assert.Equal(GraphicsApi.Vulkan, resultVk.PrimaryApi);
            Assert.Equal(ApiDetectionConfidence.Medium, resultVk.Confidence);
            Assert.Contains(resultVk.Evidence, e => e.Contains("vulkan-1.dll"));
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
        public async Task DxvkInstaller_ApplyToGameAsync_RefusesDeploymentForUnsupportedApis(GraphicsApi api)
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
            Directory.CreateDirectory(dxvkArchDir);
            File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-d3d11");
            File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-dxgi");

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

        [Theory]
        [InlineData(GraphicsApi.DX12)]
        [InlineData(GraphicsApi.Vulkan)]
        [InlineData(GraphicsApi.ModernAPI)]
        public async Task DxvkInstaller_ReapplyAsync_RefusesReapplyForUnsupportedApis(GraphicsApi api)
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
            Directory.CreateDirectory(dxvkArchDir);
            File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-d3d11");

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
                Architecture = "x64",
                DxvkVersion = "2.5"
            };

            bool result = await installer.ReapplyAsync(profile, updateBaseline: false);

            Assert.False(result);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
        }

        [Theory]
        [InlineData(GraphicsApi.DX12)]
        [InlineData(GraphicsApi.Vulkan)]
        [InlineData(GraphicsApi.ModernAPI)]
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
        public async Task DxvkManager_AdoptExistingAsync_RefusesAdoptionForDX12AndVulkan()
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

            var profile12 = profileStore.GetOrCreate(exePath);
            profile12.Api = GraphicsApi.DX12;
            profile12.Architecture = "x64";
            Assert.False(await manager.AdoptExistingAsync(profile12));

            var profileVk = profileStore.GetOrCreate(exePath);
            profileVk.Api = GraphicsApi.Vulkan;
            profileVk.Architecture = "x64";
            Assert.False(await manager.AdoptExistingAsync(profileVk));
        }
    }
}
