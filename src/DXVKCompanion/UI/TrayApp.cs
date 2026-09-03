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
        private readonly ApiClassifier _classifier;

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

            profile.Api = _classifier.Classify(process);
            _profiles.Save(profile);

            _trayIcon.ShowBalloonTip(
                3000,
                "Game Detected",
                $"{process.ProcessName} is running.\nAPI: {profile.Api}",
                ToolTipIcon.Info
            );

            _menu.SetActiveGame(process, profile);
        }
    }
}
