using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Forms;
using DXVKCompanion.Monitoring;
using DXVKCompanion.Startup;
using DXVKCompanion.Storage;
using DXVKCompanion.DXVK;
using DXVKCompanion.Safety;
using DXVKCompanion.Utils;
using DXVKCompanion.UI;

namespace DXVKCompanion
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            var options = PrepareStartup(args, GameLibraryPaths.GameLibraryFile, out var preflight);
            if (options == null)
            {
                ApplicationConfiguration.Initialize();
                if (preflight.ShouldRefuse)
                    StartupCompatibility.PresentRefusal(preflight);
                else
                    MessageBox.Show("Unsupported startup arguments.\n\n" + StartupOptions.Usage,
                        "DXVK Companion - Startup Arguments", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Paths.EnsureDirectories();
            Logger.Log("DXVK Companion started.");

            ApplicationConfiguration.Initialize();

            // Install and capture the actual Windows Forms owner-thread dispatcher before constructing TrayApp or starting background scheduling.
            var uiContext = SynchronizationContext.Current as WindowsFormsSynchronizationContext 
                ?? new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(uiContext);

            // One shared HttpClient for every GitHub call and download in the app.
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DXVK-Companion", "1.0"));

            // Storage
            var profiles = new ProfileStore();
            var cacheStore = new CacheStore();
            var settings = SettingsStore.Load();
            var gameLibraryStore = new GameLibraryStore();
            var transactionEngine = new MultiFileTransactionEngine(GameLibraryPaths.BackupsDir);

            // Monitoring
            var detector = new GameDetector();
            var exitHandler = new ProcessExitHandler();
            var moduleScanner = new ModuleScanner();
            var peParser = new PeParser();
            var classifier = new ApiClassifier(moduleScanner, peParser);
            using var monitor = CreateMonitor(options, detector, exitHandler, classifier, gameLibraryStore);
            monitor.ProfileStore = profiles;

            // DXVK
            var fileUtils = new FileUtils();
            var installer = new DxvkInstaller(httpClient, transactionEngine, gameLibraryStore, null, fileUtils);
            var rollback = new DxvkRollback(transactionEngine, gameLibraryStore, fileUtils);
            var github = new DxvkGithubClient(httpClient, cacheStore);
            var dxvkManager = new DxvkManager(installer, rollback, github, profiles, gameLibraryStore);

            // UI
            using var trayApp = new TrayApp(monitor, profiles, dxvkManager, classifier, settings, httpClient, gameLibraryStore, githubClient: github, syncContext: uiContext);
            Application.ApplicationExit += (_, _) => trayApp.Dispose();

            Application.Run();
        }

        // Both refusal paths precede directory creation, logging, stores and monitoring.
        internal static StartupOptions? PrepareStartup(string[] args, string libraryPath, out StartupPreflightResult preflight)
        {
            preflight = StartupCompatibility.Evaluate(libraryPath);
            if (preflight.ShouldRefuse) return null;
            return StartupOptions.TryParse(args, out var options) ? options : null;
        }

        internal static ProcessMonitor CreateMonitor(
            StartupOptions options, GameDetector detector, ProcessExitHandler exitHandler,
            ApiClassifier classifier, GameLibraryStore? store,
            Func<System.Collections.Generic.IEnumerable<System.Diagnostics.Process>>? processEnumerator = null)
            => new ProcessMonitor(detector, exitHandler, classifier, store, null, processEnumerator,
                startTimer: true, automaticDiscoveryEnabled: options.AutomaticDiscoveryEnabled);
    }
}
