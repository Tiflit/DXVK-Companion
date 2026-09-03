using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using DXVKCompanion.Monitoring;
using DXVKCompanion.Storage;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;

namespace DXVKCompanion.UI
{
    public class TrayApp
    {
        private readonly NotifyIcon _trayIcon;
        private readonly TrayMenu _menu;

        private readonly ProcessMonitor _monitor;
        private readonly ProfileStore _profiles;
        private readonly DxvkManager _dxvk;

        public TrayApp(
            ProcessMonitor monitor,
            ProfileStore profiles,
            DxvkManager dxvk)
        {
            _monitor = monitor;
            _profiles = profiles;
            _dxvk = dxvk;

            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "DXVK Companion"
            };

            _menu = new TrayMenu(_trayIcon, profiles, dxvk);

            _monitor.OnGameDetected += HandleGameDetected;
        }

        private void HandleGameDetected(Process process)
        {
            var profile = _profiles.GetOrCreate(process.MainModule.FileName);

            _trayIcon.ShowBalloonTip(
                3000,
                "Game Detected",
                $"{process.ProcessName} is running.\nDXVK API: {profile.Api}",
                ToolTipIcon.Info
            );

            _menu.SetActiveGame(process, profile);
        }
    }
}
