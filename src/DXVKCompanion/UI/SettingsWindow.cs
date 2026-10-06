using System;
using System.Drawing;
using System.Windows.Forms;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;

namespace DXVKCompanion.UI
{
    public class SettingsWindow : Form
    {
        private readonly SettingsChangeCoordinator _coordinator;
        private readonly Label _statusLabel;
        private bool _isUpdatingUI;

        public SettingsWindow(SettingsStore settings)
            : this(new SettingsChangeCoordinator(settings))
        {
        }

        public SettingsWindow(SettingsStore settings, StartupManager startup)
            : this(new SettingsChangeCoordinator(settings, startup))
        {
        }

        public SettingsWindow(SettingsChangeCoordinator coordinator)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

            Text = "DXVK Companion — Settings";
            Width = 460;
            Height = 300;
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
                Checked = _coordinator.ActivePolicy == GlobalManagementPolicy.Manual,
                Top = 25,
                Left = 15,
                Width = 385,
                AutoSize = true
            };

            var rbAutomated = new RadioButton
            {
                Text = "Automated (Experimental: Auto-install & maintain DXVK on exit)",
                Checked = _coordinator.ActivePolicy == GlobalManagementPolicy.Automated,
                Top = 60,
                Left = 15,
                Width = 385,
                AutoSize = true
            };

            rbManual.CheckedChanged += (_, _) =>
            {
                if (_isUpdatingUI) return;
                if (!rbManual.Checked) return;

                var result = _coordinator.ChangePolicy(GlobalManagementPolicy.Manual);
                DisplayResult(result);
                if (result.ShouldRevertUI)
                {
                    _isUpdatingUI = true;
                    try
                    {
                        rbManual.Checked = result.ActivePolicy == GlobalManagementPolicy.Manual;
                        rbAutomated.Checked = result.ActivePolicy == GlobalManagementPolicy.Automated;
                    }
                    finally
                    {
                        _isUpdatingUI = false;
                    }
                }
            };

            rbAutomated.CheckedChanged += (_, _) =>
            {
                if (_isUpdatingUI) return;
                if (!rbAutomated.Checked) return;

                var result = _coordinator.ChangePolicy(GlobalManagementPolicy.Automated);
                DisplayResult(result);
                if (result.ShouldRevertUI)
                {
                    _isUpdatingUI = true;
                    try
                    {
                        rbManual.Checked = result.ActivePolicy == GlobalManagementPolicy.Manual;
                        rbAutomated.Checked = result.ActivePolicy == GlobalManagementPolicy.Automated;
                    }
                    finally
                    {
                        _isUpdatingUI = false;
                    }
                }
            };

            policyGroup.Controls.Add(rbManual);
            policyGroup.Controls.Add(rbAutomated);
            Controls.Add(policyGroup);

            var startupCheckbox = new CheckBox
            {
                Text = "Launch DXVK Companion on Windows startup",
                Checked = _coordinator.ActiveLaunchOnStartup,
                AutoSize = true,
                Top = 135,
                Left = 20
            };
            startupCheckbox.CheckedChanged += (_, _) =>
            {
                if (_isUpdatingUI) return;

                var result = _coordinator.ChangeLaunchOnStartup(startupCheckbox.Checked);
                DisplayResult(result);
                if (result.ShouldRevertUI)
                {
                    _isUpdatingUI = true;
                    try
                    {
                        startupCheckbox.Checked = result.ActiveLaunchOnStartup;
                    }
                    finally
                    {
                        _isUpdatingUI = false;
                    }
                }
            };
            Controls.Add(startupCheckbox);

            _statusLabel = new Label
            {
                Top = 165,
                Left = 20,
                Width = 415,
                Height = 45,
                AutoSize = false,
                ForeColor = Color.Red,
                Visible = false
            };
            Controls.Add(_statusLabel);

            var btnClose = new Button
            {
                Text = "Close",
                Top = 220,
                Left = 330,
                Width = 100,
                Height = 32
            };
            btnClose.Click += (_, _) => Close();
            Controls.Add(btnClose);
        }

        private void DisplayResult(SettingsOperationResult result)
        {
            if (result.Status == SettingsOperationStatus.Success)
            {
                _statusLabel.Text = string.Empty;
                _statusLabel.Visible = false;
            }
            else if (result.Status == SettingsOperationStatus.Warning)
            {
                _statusLabel.ForeColor = Color.DarkOrange;
                _statusLabel.Text = result.Message ?? "Warning: settings update partially succeeded.";
                _statusLabel.Visible = true;
            }
            else // Failure
            {
                _statusLabel.ForeColor = Color.Red;
                _statusLabel.Text = result.Message ?? "Error: settings update failed.";
                _statusLabel.Visible = true;
            }
        }
    }
}
