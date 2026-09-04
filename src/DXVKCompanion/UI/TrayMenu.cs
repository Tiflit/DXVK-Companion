// FILE: UI/TrayMenu.cs
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
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Manage Games...", null, (_, _) => OpenManageGames());
            menu.Items.Add("Settings", null, (_, _) => OpenSettings());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit DXVK Companion", null, (_, _) => Application.Exit());

            return menu;
        }

        private void ShowResult(DxvkActionResult result, string appliedTitle, string actionDescription)
        {
            switch (result)
            {
                case DxvkActionResult.Applied:
                    _tray.ShowBalloonTip(3000, appliedTitle, "Done.", ToolTipIcon.Info);
                    break;
                case DxvkActionResult.Queued:
                    _tray.ShowBalloonTip(3000, "Queued", $"{actionDescription} will apply automatically once the game closes.", ToolTipIcon.Info);
                    break;
                case DxvkActionResult.Failed:
                    _tray.ShowBalloonTip(3000, "DXVK Error", $"Failed: {actionDescription}.", ToolTipIcon.Error);
                    break;
            }
        }

        private async Task EnableDXVK()
        {
            if (_activeProfile == null || _activeProcess == null) return;
            var result = await _dxvk.RequestEnableAsync(_activeProfile, _activeProcess);
            ShowResult(result, "DXVK Enabled", "Enabling DXVK");
        }

        private async Task DisableDXVK()
        {
            if (_activeProfile == null || _activeProcess == null) return;
            var result = await _dxvk.RequestDisableAsync(_activeProfile, _activeProcess);
            ShowResult(result, "DXVK Disabled", "Disabling DXVK");
        }

        private async Task UpdateDXVK()
        {
            if (_activeProfile == null || _activeProcess == null) return;

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

            var result = await _dxvk.RequestUpdateAsync(_activeProfile, _activeProcess);
            ShowResult(result, "DXVK Updated", "Updating DXVK");
        }

        private void OpenGameDetails()
        {
            if (_activeProfile == null) return;
            new GameDetailsWindow(_activeProfile, _profiles).Show();
        }

        private void OpenManageGames()
        {
            new ManageGamesWindow(_profiles, _dxvk).Show();
        }

        private void OpenSettings() => new SettingsWindow(_settings).Show();
    }
}
