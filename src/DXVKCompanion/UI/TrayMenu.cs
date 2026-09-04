using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using DXVKCompanion.Models;
using DXVKCompanion.DXVK;
using DXVKCompanion.Storage;

namespace DXVKCompanion.UI
{
    public class TrayMenu
    {
        private readonly NotifyIcon _tray;
        private readonly ProfileStore _profiles;
        private readonly DxvkManager _dxvk;
        private readonly SettingsStore _settings;

        private Process? _activeProcess;
        private GameProfile? _activeProfile;

        public TrayMenu(NotifyIcon tray, ProfileStore profiles, DxvkManager dxvk, SettingsStore settings)
        {
            _tray = tray;
            _profiles = profiles;
            _dxvk = dxvk;
            _settings = settings;

            _tray.ContextMenuStrip = BuildMenu();
        }

        public void SetActiveGame(Process process, GameProfile profile)
        {
            _activeProcess = process;
            _activeProfile = profile;
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();

            menu.Items.Add("Enable DXVK", null, async (_, _) => await EnableDXVK());
            menu.Items.Add("Disable DXVK", null, async (_, _) => await DisableDXVK());
            menu.Items.Add("Update DXVK", null, async (_, _) => await UpdateDXVK());
            menu.Items.Add("Game Details", null, (_, _) => OpenGameDetails());
            menu.Items.Add("Settings", null, (_, _) => OpenSettings());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit DXVK Companion", null, (_, _) => Application.Exit());

            return menu;
        }

        private async Task EnableDXVK()
        {
            if (_activeProfile == null)
                return;

            bool ok = await _dxvk.EnableDxvkAsync(_activeProfile);

            _tray.ShowBalloonTip(
                3000,
                ok ? "DXVK Enabled" : "DXVK Error",
                ok ? "DXVK will apply on next launch." : "Failed to enable DXVK.",
                ok ? ToolTipIcon.Info : ToolTipIcon.Error
            );
        }

        private async Task DisableDXVK()
        {
            if (_activeProfile == null)
                return;

            bool ok = await _dxvk.DisableDxvkAsync(_activeProfile);

            _tray.ShowBalloonTip(
                3000,
                ok ? "DXVK Disabled" : "DXVK Error",
                ok ? "DXVK removed from game folder." : "Failed to disable DXVK.",
                ok ? ToolTipIcon.Info : ToolTipIcon.Error
            );
        }

        private async Task UpdateDXVK()
        {
            if (_activeProfile == null)
                return;

            var latest = await _dxvk.GetLatestReleaseAsync();
            if (latest == null)
            {
                _tray.ShowBalloonTip(3000, "DXVK Error", "Failed to fetch DXVK release.", ToolTipIcon.Error);
                return;
            }

            if (!_dxvk.UpdateAvailable(_activeProfile, latest))
            {
                _tray.ShowBalloonTip(3000, "DXVK Up To Date", "You already have the latest DXVK version.", ToolTipIcon.Info);
                return;
            }

            bool ok = await _dxvk.EnableDxvkAsync(_activeProfile);

            _tray.ShowBalloonTip(
                3000,
                ok ? "DXVK Updated" : "DXVK Error",
                ok ? "DXVK will apply on next launch." : "Failed to update DXVK.",
                ok ? ToolTipIcon.Info : ToolTipIcon.Error
            );
        }

        private void OpenGameDetails()
        {
            if (_activeProfile == null)
                return;

            var window = new GameDetailsWindow(_activeProfile, _profiles);
            window.Show();
        }

        private void OpenSettings()
        {
            var window = new SettingsWindow(_settings);
            window.Show();
        }
    }
}
