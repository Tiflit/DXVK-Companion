using System;
using System.Diagnostics;
using System.IO;
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
        private readonly GameLibraryStore _gameLibraryStore;

        private Process? _activeProcess;
        private GameProfile? _activeProfile;

        public TrayMenu(
            NotifyIcon tray,
            ProfileStore profiles,
            DxvkManager dxvk,
            SettingsStore settings,
            GameLibraryStore? gameLibraryStore = null)
        {
            _tray = tray;
            _profiles = profiles;
            _dxvk = dxvk;
            _settings = settings;
            _gameLibraryStore = gameLibraryStore ?? new GameLibraryStore();

            _tray.ContextMenuStrip = BuildMenu();
        }

        public void SetActiveGame(Process process, GameProfile profile)
        {
            _activeProcess = process;
            _activeProfile = profile;
            _tray.ContextMenuStrip = BuildMenu();
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();

            var itemEnable = new ToolStripMenuItem("Enable DXVK", null, async (_, _) => await EnableDXVK());
            var itemDisable = new ToolStripMenuItem("Disable DXVK", null, async (_, _) => await DisableDXVK());
            var itemUpdate = new ToolStripMenuItem("Update DXVK", null, async (_, _) => await UpdateDXVK());
            var itemReapply = new ToolStripMenuItem("Reapply DXVK", null, async (_, _) => await ReapplyDXVK());
            var itemAdopt = new ToolStripMenuItem("Adopt Existing DXVK", null, async (_, _) => await AdoptDXVK());
            var itemDetails = new ToolStripMenuItem("Game Details...", null, (_, _) => OpenGameDetails());

            if (_activeProfile != null)
            {
                itemEnable.Enabled = !_activeProfile.DxvkEnabled;
                itemDisable.Enabled = _activeProfile.DxvkEnabled;
                itemUpdate.Enabled = _activeProfile.DxvkEnabled;

                string gameDir = Path.GetDirectoryName(_activeProfile.ExePath) ?? string.Empty;
                var installation = !string.IsNullOrWhiteSpace(gameDir)
                    ? _gameLibraryStore.FindByInstallationPath(gameDir)
                    : null;

                bool needsReapply = installation?.RestorationState == RestorationState.AttentionRequired;
                itemReapply.Visible = needsReapply;

                var assessment = _dxvk.AssessExistingDxvk(_activeProfile);
                itemAdopt.Visible = assessment.CanBeAdopted && !_activeProfile.DxvkEnabled;
            }
            else
            {
                itemEnable.Enabled = false;
                itemDisable.Enabled = false;
                itemUpdate.Enabled = false;
                itemReapply.Visible = false;
                itemAdopt.Visible = false;
                itemDetails.Enabled = false;
            }

            menu.Items.Add(itemEnable);
            menu.Items.Add(itemDisable);
            menu.Items.Add(itemUpdate);
            if (itemReapply.Visible) menu.Items.Add(itemReapply);
            if (itemAdopt.Visible) menu.Items.Add(itemAdopt);
            menu.Items.Add(itemDetails);

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
                    _tray.ShowBalloonTip(3000, appliedTitle, "Operation completed successfully.", ToolTipIcon.Info);
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
            _tray.ContextMenuStrip = BuildMenu();
        }

        private async Task DisableDXVK()
        {
            if (_activeProfile == null || _activeProcess == null) return;
            var result = await _dxvk.RequestDisableAsync(_activeProfile, _activeProcess);
            ShowResult(result, "DXVK Disabled", "Disabling DXVK");
            _tray.ContextMenuStrip = BuildMenu();
        }

        private async Task ReapplyDXVK()
        {
            if (_activeProfile == null || _activeProcess == null) return;
            var result = await _dxvk.RequestReapplyAsync(_activeProfile, _activeProcess, updateBaseline: true);
            ShowResult(result, "DXVK Reapplied", "Reapplying DXVK");
            _tray.ContextMenuStrip = BuildMenu();
        }

        private async Task AdoptDXVK()
        {
            if (_activeProfile == null) return;
            bool ok = await _dxvk.AdoptExistingAsync(_activeProfile);
            if (ok)
            {
                _tray.ShowBalloonTip(3000, "DXVK Adopted", "Existing official DXVK release was successfully adopted.", ToolTipIcon.Info);
                _tray.ContextMenuStrip = BuildMenu();
            }
            else
            {
                _tray.ShowBalloonTip(3000, "Adoption Failed", "Could not adopt existing DXVK.", ToolTipIcon.Warning);
            }
        }

        private async Task UpdateDXVK()
        {
            if (_activeProfile == null || _activeProcess == null) return;

            var latest = await _dxvk.GetLatestReleaseAsync();
            if (latest == null)
            {
                _tray.ShowBalloonTip(3000, "DXVK Error", "Failed to fetch DXVK release from GitHub.", ToolTipIcon.Error);
                return;
            }

            if (!_dxvk.UpdateAvailable(_activeProfile, latest))
            {
                _tray.ShowBalloonTip(3000, "DXVK Up To Date", "You already have the latest DXVK version.", ToolTipIcon.Info);
                return;
            }

            var result = await _dxvk.RequestUpdateAsync(_activeProfile, _activeProcess);
            ShowResult(result, "DXVK Updated", "Updating DXVK");
            _tray.ContextMenuStrip = BuildMenu();
        }

        private void OpenGameDetails()
        {
            if (_activeProfile == null) return;
            new GameDetailsWindow(_activeProfile, _profiles, _dxvk, _gameLibraryStore).Show();
        }

        private void OpenManageGames()
        {
            new ManageGamesWindow(_profiles, _dxvk, _gameLibraryStore).Show();
        }

        private void OpenSettings() => new SettingsWindow(_settings).Show();
    }
}
