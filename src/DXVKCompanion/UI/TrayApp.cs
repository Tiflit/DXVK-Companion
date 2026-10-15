using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DXVKCompanion.Monitoring;
using DXVKCompanion.Storage;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Utils;

namespace DXVKCompanion.UI
{
    public class TrayApp
    {
        private readonly NotifyIcon _trayIcon;
        private readonly TrayMenu _menu;

        private readonly ProcessMonitor _monitor;
        private readonly ProfileStore _profiles;
        private readonly DxvkManager _dxvk;
        private readonly ApiClassifier _classifier;
        private readonly GameDetector _detector;
        private readonly SettingsStore _settings;
        private readonly HttpClient _httpClient;
        private readonly GameLibraryStore _gameLibraryStore;
        private readonly ManagedFileInspector _inspector;
        private readonly SynchronizationContext _syncContext;

        private string? _pendingUpdateUrl;

        public TrayApp(
            ProcessMonitor monitor,
            ProfileStore profiles,
            DxvkManager dxvk,
            ApiClassifier classifier,
            SettingsStore settings,
            HttpClient httpClient,
            GameLibraryStore? gameLibraryStore = null)
        {
            _monitor = monitor;
            _profiles = profiles;
            _dxvk = dxvk;
            _classifier = classifier;
            _settings = settings;
            _httpClient = httpClient;
            _gameLibraryStore = gameLibraryStore ?? new GameLibraryStore();
            _inspector = new ManagedFileInspector(_gameLibraryStore);

            _detector = new GameDetector();
            _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();

            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "DXVK Companion"
            };

            _trayIcon.BalloonTipClicked += (_, _) =>
            {
                if (_pendingUpdateUrl == null) return;
                Process.Start(new ProcessStartInfo { FileName = _pendingUpdateUrl, UseShellExecute = true });
                _pendingUpdateUrl = null;
            };

            _menu = new TrayMenu(_trayIcon, profiles, dxvk, settings, _gameLibraryStore);

            _dxvk.OnPendingActionCancelled += (installation, action, reason) =>
            {
                string name = !string.IsNullOrEmpty(installation.DisplayName)
                    ? installation.DisplayName
                    : Path.GetFileName(installation.InstallationPath);
                _syncContext.Post(_ =>
                {
                    _trayIcon.ShowBalloonTip(5000, "DXVK Operation Cancelled",
                        $"Pending {action.Type} for {name} was cancelled as incompatible:\n{reason}", ToolTipIcon.Warning);
                }, null);
            };

            _monitor.OnQualifiedGameDetected += (process, snapshot) => HandleGameDetected(process, snapshot);
            _monitor.OnGameExited += async exePath =>
            {
                _syncContext.Post(_ => _menu.ClearActiveGame(), null);
                bool applied = await _dxvk.ApplyPendingAsync(exePath);
                if (applied)
                {
                    _syncContext.Post(_ =>
                    {
                        _trayIcon.ShowBalloonTip(4000, "DXVK Companion",
                            $"Operation completed safely after game exit for {Path.GetFileName(exePath)}.", ToolTipIcon.Info);
                    }, null);
                }
            };

            // Startup tasks: inspect all installations and process pending actions
            _ = InitializeStartupStateAsync();
            _ = CheckDxvkUpdateOnStartup();
            _ = CheckCompanionUpdateOnStartup();
        }

        private async Task InitializeStartupStateAsync()
        {
            try
            {
                _inspector.InspectAll();
                int processed = await _dxvk.ProcessAllPendingActionsAsync();
                if (processed > 0)
                {
                    Logger.Log($"TrayApp: successfully processed {processed} pending actions on startup.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TrayApp: startup inspection error: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private async Task CheckDxvkUpdateOnStartup()
        {
            var latest = await _dxvk.GetLatestReleaseAsync();
            if (latest == null) return;

            bool anyOutdated = false;
            foreach (var profile in _profiles.GetAll())
            {
                if (profile.DxvkEnabled && _dxvk.UpdateAvailable(profile, latest))
                {
                    anyOutdated = true;
                    break;
                }
            }

            if (anyOutdated)
            {
                _syncContext.Post(_ =>
                {
                    _trayIcon.ShowBalloonTip(5000, "DXVK Update Available",
                        $"A new DXVK version ({latest.Version}) is available. Open Manage Games to update.", ToolTipIcon.Info);
                }, null);
            }
        }

        private async Task CheckCompanionUpdateOnStartup()
        {
            var checker = new CompanionUpdateChecker(_httpClient);
            var latest = await checker.GetLatestVersionAsync();
            if (latest == null) return;

            if (CompanionVersion.IsOutdatedComparedTo(CompanionVersion.Current, latest))
            {
                _syncContext.Post(_ =>
                {
                    _pendingUpdateUrl = "https://github.com/Tiflit/DXVK-Companion/releases";
                    _trayIcon.ShowBalloonTip(5000, "DXVK Companion Update Available",
                        $"A new DXVK Companion version ({latest}) is available.\nClick to open releases.", ToolTipIcon.Info);
                }, null);
            }
        }

        internal static async Task<bool> ProcessGameAdmissionAsync(
            DetectionSnapshot snapshot,
            Process? process,
            ProfileStore profiles,
            GameLibraryStore gameLibraryStore,
            DxvkManager dxvk,
            ManagedFileInspector inspector,
            SettingsStore settings,
            Action<Process?, GameProfile, string>? onNotification = null,
            Func<GameProfile, Process, Task>? autoEnableDispatcher = null,
            Func<GameProfile, Process, Task>? autoReapplyDispatcher = null)
        {
            if (snapshot == null || !ProcessQualification.IsRecognizedGraphicsApi(snapshot.Classification.PrimaryApi))
            {
                return false;
            }

            string exePath = snapshot.ExecutablePath;
            if (string.IsNullOrEmpty(exePath)) return false;

            var profile = profiles.GetOrCreate(exePath);
            profile.Api = snapshot.Classification.PrimaryApi;
            profile.Architecture = snapshot.Classification.Architecture;

            string gameDir = snapshot.InstallationRoot;
            var antiCheatAssessment = snapshot.AntiCheat;
            bool antiCheat = antiCheatAssessment.Risk != AntiCheatRisk.None;

            if (!string.IsNullOrWhiteSpace(gameDir))
            {
                gameLibraryStore.RecordDetectionSnapshot(snapshot);
            }

            var latest = await dxvk.GetLatestReleaseAsync();
            string localVersion = string.IsNullOrWhiteSpace(profile.DxvkVersion) ? "None" : profile.DxvkVersion;

            var installation = gameLibraryStore.FindInstallationForExecutable(exePath)
                ?? (!string.IsNullOrWhiteSpace(gameDir) ? gameLibraryStore.FindByInstallationPath(gameDir) : null);

            bool dxvkCompatible = DxvkCompatibility.IsInstallationSupported(installation, profile.Api, profile.ExeName, out var refusalReason);
            bool updateAvailable = latest != null && profile.DxvkEnabled && dxvk.UpdateAvailable(profile, latest);

            bool externalChange = false;
            if (installation != null)
            {
                externalChange = inspector.InspectInstallation(installation);
            }

            var effectivePolicy = installation?.ManagementPolicy ?? ManagementPolicy.UseGlobal();
            bool isAutomated = effectivePolicy.IsAutomated(settings.GlobalPolicy);
            bool canAutoDeploy = dxvk.CanAutomaticallyDeploy(installation);

            if (isAutomated && canAutoDeploy &&
                dxvkCompatible && !antiCheat && !profile.DxvkEnabled &&
                string.IsNullOrWhiteSpace(profile.DxvkVersion) && process != null)
            {
                if (autoEnableDispatcher != null)
                {
                    await autoEnableDispatcher(profile, process);
                }
                else
                {
                    await dxvk.RequestEnableAsync(profile, process);
                }
            }
            else if (isAutomated && canAutoDeploy && dxvkCompatible && externalChange && !antiCheat && profile.DxvkEnabled && process != null)
            {
                if (autoReapplyDispatcher != null)
                {
                    await autoReapplyDispatcher(profile, process);
                }
                else
                {
                    await dxvk.RequestReapplyAsync(profile, process, updateBaseline: true);
                }
            }

            profiles.Save(profile);

            string message = BuildNotificationMessage(process, profile, antiCheat, latest, localVersion, dxvkCompatible, refusalReason, updateAvailable, externalChange);
            onNotification?.Invoke(process, profile, message);

            return true;
        }

        private async void HandleGameDetected(Process process, DetectionSnapshot snapshot)
        {
            try
            {
                await ProcessGameAdmissionAsync(
                    snapshot,
                    process,
                    _profiles,
                    _gameLibraryStore,
                    _dxvk,
                    _inspector,
                    _settings,
                    (proc, prof, message) =>
                    {
                        bool antiCheat = snapshot.AntiCheat.Risk != AntiCheatRisk.None;
                        _syncContext.Post(_ =>
                        {
                            _trayIcon.ShowBalloonTip(5000, "Game Detected", message,
                                antiCheat ? ToolTipIcon.Warning : ToolTipIcon.Info);
                            if (proc != null)
                            {
                                _menu.SetActiveGame(proc, prof);
                            }
                        }, null);
                    });
            }
            catch (Exception ex)
            {
                Logger.Log($"TrayApp.HandleGameDetected failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        internal static string BuildNotificationMessage(
            Process? process, GameProfile profile, bool antiCheat, ReleaseInfo? latest,
            string localVersion, bool dxvkCompatible, string? refusalReason, bool updateAvailable, bool externalChange)
        {
            string procName = process?.ProcessName ?? Path.GetFileNameWithoutExtension(profile.ExePath);
            var msg = $"{procName} is running.\n" +
                      $"API: {profile.Api} ({profile.Architecture})\n" +
                      $"DXVK enabled: {profile.DxvkEnabled}\n" +
                      $"Local DXVK version: {localVersion}\n";

            if (latest != null)
                msg += $"Latest DXVK release: {latest.Version}\n";

            if (externalChange)
            {
                msg += "⚠ External game file changes detected. Click Reapply DXVK in the tray menu once the game closes.\n";
            }
            else if (!dxvkCompatible)
            {
                if (!string.IsNullOrEmpty(refusalReason))
                    msg += $"DXVK deployment is not supported: {refusalReason}\n";
                else
                    msg += "DXVK is not compatible with this game's API.\n";
            }
            else
            {
                if (!profile.DxvkEnabled)
                    msg += "DXVK is available for this game. Enable it from the tray menu (applies once the game closes).\n";
                else if (updateAvailable)
                    msg += "A newer DXVK version is available. You can update it from the tray menu.\n";
                else
                    msg += "DXVK is up to date for this game.\n";
            }

            if (antiCheat)
                msg += "⚠ Anti-cheat components detected. Do NOT use DXVK in online/multiplayer modes.\n";

            return msg;
        }
    }
}
