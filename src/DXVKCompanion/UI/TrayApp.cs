using System;
using System.Diagnostics;
using System.Drawing;
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
        private readonly SynchronizationContext _syncContext;

        private string? _pendingUpdateUrl;

        public TrayApp(
            ProcessMonitor monitor,
            ProfileStore profiles,
            DxvkManager dxvk,
            ApiClassifier classifier,
            SettingsStore settings,
            HttpClient httpClient)
        {
            _monitor = monitor;
            _profiles = profiles;
            _dxvk = dxvk;
            _classifier = classifier;
            _settings = settings;
            _httpClient = httpClient;

            // TrayApp's constructor has no GameDetector parameter — this is the only place
            // _detector is ever assigned, not a redundant duplicate of one passed in.
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

            _menu = new TrayMenu(_trayIcon, profiles, dxvk, settings);

            _monitor.OnGameDetected += HandleGameDetected;
            _monitor.OnGameExited += async exePath => await _dxvk.ApplyPendingAsync(exePath);

            _ = CheckDxvkUpdateOnStartup();
            _ = CheckCompanionUpdateOnStartup();
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

        private async void HandleGameDetected(Process process)
        {
            string exePath;
            try
            {
                exePath = process.MainModule.FileName;
            }
            catch (Exception ex)
            {
                Logger.Log($"TrayApp: failed to read MainModule.FileName: {ex.GetType().Name} - {ex.Message}");
                return;
            }

            try
            {
                var profile = _profiles.GetOrCreate(exePath);

                profile.Api = _classifier.Classify(process);

                var parser = new PeParser();
                profile.Architecture = parser.GetArchitecture(exePath);

                bool antiCheat = _detector.HasAntiCheatRisk(process);
                var latest = await _dxvk.GetLatestReleaseAsync();

                string localVersion = string.IsNullOrWhiteSpace(profile.DxvkVersion) ? "None" : profile.DxvkVersion;

                bool dxvkCompatible = profile.Api == GraphicsApi.DX9 ||
                                      profile.Api == GraphicsApi.DX11 ||
                                      profile.Api == GraphicsApi.ModernAPI;

                bool updateAvailable = latest != null && profile.DxvkEnabled && _dxvk.UpdateAvailable(profile, latest);

                if (_settings.AutoEnableDxvkForNewGames &&
                    dxvkCompatible && !antiCheat && !profile.DxvkEnabled &&
                    string.IsNullOrWhiteSpace(profile.DxvkVersion))
                {
                    await _dxvk.RequestEnableAsync(profile, process);
                }

                _profiles.Save(profile);

                string message = BuildNotificationMessage(process, profile, antiCheat, latest, localVersion, dxvkCompatible, updateAvailable);

                _syncContext.Post(_ =>
                {
                    _trayIcon.ShowBalloonTip(5000, "Game Detected", message,
                        antiCheat ? ToolTipIcon.Warning : ToolTipIcon.Info);
                    _menu.SetActiveGame(process, profile);
                }, null);
            }
            catch (Exception ex)
            {
                Logger.Log($"TrayApp.HandleGameDetected failed for {exePath}: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private string BuildNotificationMessage(
            Process process, GameProfile profile, bool antiCheat, ReleaseInfo? latest,
            string localVersion, bool dxvkCompatible, bool updateAvailable)
        {
            var msg = $"{process.ProcessName} is running.\n" +
                      $"API: {profile.Api} ({profile.Architecture})\n" +
                      $"DXVK enabled: {profile.DxvkEnabled}\n" +
                      $"Local DXVK version: {localVersion}\n";

            if (latest != null)
                msg += $"Latest DXVK release: {latest.Version}\n";

            if (!dxvkCompatible)
            {
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
