using System.Diagnostics;
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

        private Process? _activeProcess;
        private GameProfile? _activeProfile;

        public TrayMenu(NotifyIcon tray, ProfileStore profiles, DxvkManager dxvk)
        {
            _tray = tray;
            _profiles = profiles;
            _dxvk = dxvk;

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

            menu.Items.Add("Enable DXVK", null, (_, _) => EnableDXVK());
            menu.Items.Add("Disable DXVK", null, (_, _) => DisableDXVK());
            menu.Items.Add("Game Details", null, (_, _) => OpenGameDetails());
            menu.Items.Add("Settings", null, (_, _) => OpenSettings());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => Application.Exit());

            return menu;
        }

        private void EnableDXVK()
        {
            if (_activeProcess == null || _activeProfile == null)
                return;

            _dxvk.EnableDXVK(_activeProcess, _activeProfile);

            _tray.ShowBalloonTip(2000, "DXVK Enabled", "DXVK will apply on next launch.", ToolTipIcon.Info);
        }

        private void DisableDXVK()
        {
            if (_activeProcess == null || _activeProfile == null)
                return;

            _dxvk.DisableDXVK(_activeProcess, _activeProfile);

            _tray.ShowBalloonTip(2000, "DXVK Disabled", "DXVK removed from game folder.", ToolTipIcon.Info);
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
            var window = new SettingsWindow();
            window.Show();
        }
    }
}
