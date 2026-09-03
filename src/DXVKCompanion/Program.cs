using System;
using System.Windows.Forms;
using DXVKCompanion.Monitoring;
using DXVKCompanion.Storage;
using DXVKCompanion.DXVK;
using DXVKCompanion.Utils;
using DXVKCompanion.UI;

namespace DXVKCompanion
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Paths.EnsureDirectories();
            Logger.Log("DXVK Companion started.");

            ApplicationConfiguration.Initialize();

            // --- Monitoring Layer ---
            var detector = new GameDetector();
            var exitHandler = new ProcessExitHandler();
            var moduleScanner = new ModuleScanner();
            var peParser = new PeParser();
            var classifier = new ApiClassifier(moduleScanner, peParser);
            var monitor = new ProcessMonitor(detector, exitHandler);

            // --- Storage Layer ---
            var profiles = new ProfileStore();
            var cacheStore = new CacheStore();

            // --- DXVK Layer ---
            var configManager = new DxvkConfigManager();
            var fileUtils = new FileUtils();
            var installer = new DxvkInstaller(fileUtils, configManager);
            var rollback = new DxvkRollback(fileUtils);
            var github = new DxvkGithubClient();
            var cache = new DxvkReleaseCache(cacheStore);
            var dxvkManager = new DxvkManager(installer, rollback, github, cache, profiles);

            // --- UI Layer ---
            var trayApp = new TrayApp(monitor, profiles, dxvkManager);

            Application.Run();
        }
    }
}
