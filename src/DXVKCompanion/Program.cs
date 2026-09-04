using System;
using System.Net.Http;
using System.Windows.Forms;
using DXVKCompanion.DXVK;
using DXVKCompanion.Monitoring;
using DXVKCompanion.Storage;
using DXVKCompanion.UI;
using DXVKCompanion.Utils;

namespace DXVKCompanion
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            // Shared singletons
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DXVK-Companion/1.0");

            var cacheStore = new CacheStore();
            var settings = SettingsStore.Load();
            var fileUtils = new FileUtils();
            var configManager = new DxvkConfigManager();

            // DXVK layer
            var githubClient = new DxvkGithubClient(httpClient, cacheStore);
            var releaseCache = new DxvkReleaseCache(cacheStore);
            var installer = new DxvkInstaller(fileUtils, httpClient);
            var rollback = new DxvkRollback(fileUtils);
            var dxvkManager = new DxvkManager(installer, rollback, githubClient, releaseCache, configManager);

            // Storage
            var profiles = new ProfileStore();

            // Monitoring
            var detector = new GameDetector();
            var exitHandler = new ProcessExitHandler(profiles);
            var monitor = new ProcessMonitor(detector, exitHandler);

            // API detection
            var scanner = new ModuleScanner();
            var parser = new PeParser();
            var classifier = new ApiClassifier(scanner, parser);

            // UI
            var trayApp = new TrayApp(monitor, profiles, dxvkManager, classifier, settings);

            Application.Run();
        }
    }
}
