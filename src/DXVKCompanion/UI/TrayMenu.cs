using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using DXVKCompanion.Models;
using DXVKCompanion.DXVK;
using DXVKCompanion.Storage;

namespace DXVKCompanion.UI
{
    /// <summary>
    /// Static and minimal tray menu implementation adhering strictly to Section 39.
    /// Controls remain consistent without dynamic resizing or cluttering.
    /// </summary>
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
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();

            var header = new ToolStripMenuItem("DXVK Companion")
            {
                Enabled = false,
                Font = new Font(FontFamily.GenericSansSerif, 9, FontStyle.Bold)
            };
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add("Manage Games...", null, (_, _) => OpenManageGames());
            menu.Items.Add("Game Details...", null, (_, _) => OpenGameDetails());
            menu.Items.Add("Settings...", null, (_, _) => OpenSettings());

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit DXVK Companion", null, (_, _) => Application.Exit());

            return menu;
        }

        private void OpenGameDetails()
        {
            if (_activeProfile != null)
            {
                new GameDetailsWindow(_activeProfile, _profiles, _dxvk, _gameLibraryStore).Show();
            }
            else
            {
                OpenManageGames();
            }
        }

        private void OpenManageGames()
        {
            new ManageGamesWindow(_profiles, _dxvk, _gameLibraryStore).Show();
        }

        private void OpenSettings() => new SettingsWindow(_settings).Show();
    }
}
