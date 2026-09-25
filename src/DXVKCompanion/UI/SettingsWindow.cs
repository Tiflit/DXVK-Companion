using System.Drawing;
using System.Windows.Forms;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;

namespace DXVKCompanion.UI
{
    public class SettingsWindow : Form
    {
        private readonly SettingsStore _settings;
        private readonly StartupManager _startup;

        public SettingsWindow(SettingsStore settings)
        {
            _settings = settings;
            _startup = new StartupManager();

            Text = "DXVK Companion — Settings";
            Width = 460;
            Height = 280;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var policyGroup = new GroupBox
            {
                Text = "Global Management Policy",
                Top = 15,
                Left = 15,
                Width = 415,
                Height = 110
            };

            var rbManual = new RadioButton
            {
                Text = "Manual (Recommended: Observe & report only; explicit user action)",
                Checked = _settings.GlobalPolicy == GlobalManagementPolicy.Manual,
                Top = 25,
                Left = 15,
                Width = 385,
                AutoSize = true
            };

            var rbAutomated = new RadioButton
            {
                Text = "Automated (Experimental: Auto-install & maintain DXVK on exit)",
                Checked = _settings.GlobalPolicy == GlobalManagementPolicy.Automated,
                Top = 60,
                Left = 15,
                Width = 385,
                AutoSize = true
            };

            rbManual.CheckedChanged += (_, _) =>
            {
                if (rbManual.Checked)
                {
                    _settings.GlobalPolicy = GlobalManagementPolicy.Manual;
                    _settings.Save();
                }
            };

            rbAutomated.CheckedChanged += (_, _) =>
            {
                if (rbAutomated.Checked)
                {
                    _settings.GlobalPolicy = GlobalManagementPolicy.Automated;
                    _settings.Save();
                }
            };

            policyGroup.Controls.Add(rbManual);
            policyGroup.Controls.Add(rbAutomated);
            Controls.Add(policyGroup);

            var startupCheckbox = new CheckBox
            {
                Text = "Launch DXVK Companion on Windows startup",
                Checked = _settings.LaunchOnStartup,
                AutoSize = true,
                Top = 140,
                Left = 20
            };
            startupCheckbox.CheckedChanged += (_, _) =>
            {
                _settings.LaunchOnStartup = startupCheckbox.Checked;
                _settings.Save();

                if (startupCheckbox.Checked)
                    _startup.EnableStartup();
                else
                    _startup.DisableStartup();
            };
            Controls.Add(startupCheckbox);

            var btnClose = new Button
            {
                Text = "Close",
                Top = 190,
                Left = 330,
                Width = 100,
                Height = 32
            };
            btnClose.Click += (_, _) => Close();
            Controls.Add(btnClose);
        }
    }
}
