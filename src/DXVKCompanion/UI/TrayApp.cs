using System.Diagnostics;
using System.Drawing;
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
        private readonly Logger _logger;

        public TrayApp(
            ProcessMonitor monitor,
            ProfileStore profiles,
            DxvkManager dxvk,
            ApiClassifier classifier)
        {
            _monitor = monitor;
            _profiles = profiles;
            _dxvk = dxvk;
            _classifier = classifier;

            _detector = new GameDetector();
            _settings = new SettingsStore();
            _logger = new Logger();

            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "DXVK Companion"
            };

            _menu = new TrayMenu(_trayIcon, profiles, dxvk, _settings);

            _monitor.OnGameDetected += HandleGameDetected;

            _ = CheckDxvkUpdateOnStartup();
            _ = CheckCompanionUpdateOnStartup();
        }

        private async Task CheckDxvkUpdateOnStartup()
        {
            var latest = await _dxvk.GetLatestReleaseAsync();
            if (latest == null)
                return;

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
                _trayIcon.ShowBalloonTip(
                    5000,
                    "DXVK Update Available",
                    $"A new DXVK version ({latest.Version}) is available.",
                    ToolTipIcon.Info
                );
            }
        }

        private async Task CheckCompanionUpdateOnStartup()
        {
            var checker = new CompanionUpdateChecker();
            var latest = await checker.GetLatestVersionAsync();
            if (latest == null)
                return;

            // For now, assume local version "1.0.0" – replace with real versioning later
            var localVersion = "1.0.0";

            if (localVersion != latest)
            {
                _trayIcon.BalloonTipClicked += (_, _) =>
                {
                    System.Diagnostics.Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://github.com/Tiflit/DXVK-Companion/releases",
                        UseShellExecute = true
                    });
                };

                _trayIcon.ShowBalloonTip(
                    5000,
                    "DXVK Companion Update Available",
                    $"A new DXVK Companion version ({latest}) is available.\nClick to open releases.",
                    ToolTipIcon.Info
                );
            }
        }

        private async void HandleGameDetected(Process process)
        {
            try
            {
                string exePath = process.MainModule.FileName;
                var profile = _profiles.GetOrCreate(exePath);

                profile.Api = _classifier.Classify(process);

                var parser = new PeParser();
                profile.Architecture = parser.GetArchitecture(exePath);

                bool antiCheat = _detector.HasAntiCheatRisk(process);

                var latest = await _dxvk.GetLatestReleaseAsync();

                string localVersion = string.IsNullOrWhiteSpace(profile.DxvkVersion)
                    ? "None"
                    : profile.DxvkVersion;

                bool dxvkCompatible =
                    profile.Api == GraphicsApi.DX9 ||
                    profile.Api == GraphicsApi.DX11 ||
                    profile.Api == GraphicsApi.ModernAPI;

                bool updateAvailable = latest != null &&
                                       profile.DxvkEnabled &&
                                       _dxvk.UpdateAvailable(profile, latest);

                if (_settings.AutoEnableDxvkForNewGames &&
                    dxvkCompatible &&
                    !antiCheat &&
                    !profile.DxvkEnabled &&
                    string.IsNullOrWhiteSpace(profile.DxvkVersion))
                {
                    await _dxvk.EnableDxvkAsync(profile);
                }

                _profiles.Save(profile);

                string message = BuildNotificationMessage(process, profile, antiCheat, latest, localVersion, dxvkCompatible, updateAvailable);

                _trayIcon.ShowBalloonTip(
                    5000,
                    "Game Detected",
                    message,
                    antiCheat ? ToolTipIcon.Warning : ToolTipIcon.Info
                );

                _menu.SetActiveGame(process, profile);
            }
            catch
            {
            }
        }

        private string BuildNotificationMessage(
            Process process,
            GameProfile profile,
            bool antiCheat,
            ReleaseInfo? latest,
            string localVersion,
            bool dxvkCompatible,
            bool updateAvailable)
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
                    msg += "DXVK is available for this game. Enable it from the tray menu (applies on next launch).\n";
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
