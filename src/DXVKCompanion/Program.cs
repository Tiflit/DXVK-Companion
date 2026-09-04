using System;
using System.Net.Http;
using System.Net.Http.Headers;
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

            // One shared HttpClient for every GitHub call and download in the app.
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DXVK-Companion", "1.0"));

            // Storage
            var profiles = new ProfileStore();
            var cacheStore = new CacheStore();
            var settings = SettingsStore.Load();

            // Monitoring
            var detector = new GameDetector();
            var exitHandler = new ProcessExitHandler();
            var moduleScanner = new ModuleScanner();
            var peParser = new PeParser();
            var classifier = new ApiClassifier(moduleScanner, peParser);
            var monitor = new ProcessMonitor(detector, exitHandler);

            // DXVK
            var fileUtils = new FileUtils();
            var installer = new DxvkInstaller(fileUtils, httpClient);
            var rollback = new DxvkRollback(fileUtils);
            var github = new DxvkGithubClient(httpClient, cacheStore);
            var dxvkManager = new DxvkManager(installer, rollback, github, profiles);

            // UI
            var trayApp = new TrayApp(monitor, profiles, dxvkManager, classifier, settings, httpClient);

            Application.Run();
        }
    }
}
