using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using DXVKCompanion.Diagnostics;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;

namespace DXVKCompanion.UI
{
    public class GameDetailsWindow : Form
    {
        private readonly GameProfile _profile;
        private readonly ProfileStore _profileStore;
        private readonly DxvkManager _dxvk;
        private readonly GameLibraryStore _gameLibraryStore;

        public GameDetailsWindow(
            GameProfile profile,
            ProfileStore profileStore,
            DxvkManager dxvk,
            GameLibraryStore? gameLibraryStore = null)
        {
            _profile = profile;
            _profileStore = profileStore;
            _dxvk = dxvk;
            _gameLibraryStore = gameLibraryStore ?? new GameLibraryStore();

            Text = $"Game Details — {_profile.ExeName}";
            Width = 560;
            Height = 580;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            string gameDir = Path.GetDirectoryName(_profile.ExePath) ?? string.Empty;
            var installation = _gameLibraryStore.FindInstallationForExecutable(_profile.ExePath)
                ?? (!string.IsNullOrWhiteSpace(gameDir) ? _gameLibraryStore.FindByInstallationPath(gameDir) : null);
            if (installation == null && !string.IsNullOrWhiteSpace(gameDir))
            {
                installation = _gameLibraryStore.GetOrCreateInstallation(gameDir, _profile.ExeName);
            }

            int top = 20;

            var titleLabel = new Label
            {
                Text = _profile.ExeName,
                Font = new Font(FontFamily.GenericSansSerif, 12, FontStyle.Bold),
                AutoSize = true,
                Top = top,
                Left = 20
            };
            Controls.Add(titleLabel);
            top += 35;

            var pathLabel = new Label
            {
                Text = $"Path: {_profile.ExePath}",
                AutoSize = true,
                Top = top,
                Left = 20,
                ForeColor = Color.DimGray
            };
            Controls.Add(pathLabel);
            top += 30;

            var apiLabel = new Label
            {
                Text = $"Graphics API: {_profile.Api} ({_profile.Architecture})",
                AutoSize = true,
                Top = top,
                Left = 20
            };
            Controls.Add(apiLabel);
            top += 25;

            string versionText = string.IsNullOrWhiteSpace(_profile.DxvkVersion) ? "None (Native)" : _profile.DxvkVersion;
            var dxvkLabel = new Label
            {
                Text = $"DXVK Version: {versionText}",
                AutoSize = true,
                Top = top,
                Left = 20
            };
            Controls.Add(dxvkLabel);
            top += 25;

            // Restoration & Health status
            string healthStatus = "Clean / Baseline";
            Color healthColor = Color.DarkGreen;

            if (installation != null)
            {
                if (installation.ConflictFlags != InstallationConflictFlags.None)
                {
                    healthStatus = $"Conflict Detected: {installation.ConflictFlags}";
                    healthColor = Color.Red;
                }
                else if (installation.RestorationState == RestorationState.AttentionRequired)
                {
                    healthStatus = "Attention Required: External file modifications or deletions detected.";
                    healthColor = Color.DarkOrange;
                }
                else if (installation.RestorationState == RestorationState.Managed)
                {
                    healthStatus = "Managed & Consistent with DXVK";
                    healthColor = Color.DarkGreen;
                }
                else if (installation.RestorationState == RestorationState.Restored)
                {
                    healthStatus = "Restored to original baseline";
                    healthColor = Color.DarkBlue;
                }
            }

            var statusLabel = new Label
            {
                Text = $"Health: {healthStatus}",
                AutoSize = true,
                Top = top,
                Left = 20,
                ForeColor = healthColor,
                Font = new Font(FontFamily.GenericSansSerif, 9, FontStyle.Bold)
            };
            Controls.Add(statusLabel);
            top += 25;

            if (installation != null && !string.IsNullOrEmpty(installation.LastRefusalReason))
            {
                var refusalLabel = new Label
                {
                    Text = $"Deployment Refused: {installation.LastRefusalReason}",
                    AutoSize = true,
                    Top = top,
                    Left = 20,
                    ForeColor = Color.DarkOrange,
                    Font = new Font(FontFamily.GenericSansSerif, 8, FontStyle.Regular),
                    MaximumSize = new Size(500, 0)
                };
                Controls.Add(refusalLabel);
                top += refusalLabel.PreferredHeight + 10;
            }
            else
            {
                top += 10;
            }

            // Existing DXVK assessment & actions
            var assessment = _dxvk.AssessExistingDxvk(_profile);
            if (assessment.CanBeAdopted && !_profile.DxvkEnabled)
            {
                var adoptPanel = new Panel
                {
                    Top = top,
                    Left = 20,
                    Width = 500,
                    Height = 40,
                    BackColor = Color.FromArgb(240, 248, 255)
                };
                var adoptLabel = new Label
                {
                    Text = $"Found official DXVK {assessment.MatchedVersion} in folder.",
                    AutoSize = true,
                    Top = 10,
                    Left = 10
                };
                var btnAdopt = new Button
                {
                    Text = "Adopt DXVK",
                    Top = 6,
                    Left = 380,
                    AutoSize = true
                };
                btnAdopt.Click += async (_, _) =>
                {
                    bool ok = await _dxvk.AdoptExistingAsync(_profile);
                    if (ok)
                    {
                        MessageBox.Show("DXVK adopted successfully.", "Adoption", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Close();
                    }
                    else
                    {
                        string reason = _dxvk.LastRefusalReason ?? "Adoption failed.";
                        MessageBox.Show($"DXVK adoption was refused or failed:\n\n{reason}", "Adoption Refused", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                adoptPanel.Controls.Add(adoptLabel);
                adoptPanel.Controls.Add(btnAdopt);
                Controls.Add(adoptPanel);
                top += 45;
            }
            else if (installation?.RestorationState == RestorationState.AttentionRequired && DxvkCompatibility.IsDxvkSupported(_profile.Api))
            {
                var reapplyPanel = new Panel
                {
                    Top = top,
                    Left = 20,
                    Width = 500,
                    Height = 40,
                    BackColor = Color.FromArgb(255, 250, 240)
                };
                var reapplyLabel = new Label
                {
                    Text = "External change detected. Reapply DXVK to restore consistency?",
                    AutoSize = true,
                    Top = 10,
                    Left = 10
                };
                var btnReapply = new Button
                {
                    Text = "Reapply DXVK",
                    Top = 6,
                    Left = 380,
                    AutoSize = true
                };
                btnReapply.Click += async (_, _) =>
                {
                    var res = await _dxvk.RequestReapplyByPathAsync(_profile, updateBaseline: true);
                    if (res == DxvkActionResult.Applied)
                    {
                        MessageBox.Show("DXVK reapplied successfully and baseline updated.", "Reapply", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Close();
                    }
                    else
                    {
                        string reason = _dxvk.LastRefusalReason ?? "Reapply operation failed.";
                        MessageBox.Show($"DXVK reapply was refused or failed:\n\n{reason}", "Reapply Refused", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                reapplyPanel.Controls.Add(reapplyLabel);
                reapplyPanel.Controls.Add(btnReapply);
                Controls.Add(reapplyPanel);
                top += 45;
            }

            // Policy & Visibility controls
            var policyLabel = new Label
            {
                Text = "Management Policy:",
                AutoSize = true,
                Top = top,
                Left = 20
            };
            Controls.Add(policyLabel);
            top += 25;

            var policyCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Top = top,
                Left = 20,
                Width = 240
            };
            policyCombo.Items.Add("Use Global Policy");
            policyCombo.Items.Add("Automatic Management");
            policyCombo.Items.Add("Disabled");

            int policyIndex = 0;
            if (installation?.ManagementPolicy != null)
            {
                policyIndex = installation.ManagementPolicy.Mode switch
                {
                    ManagementMode.Automatic => 1,
                    ManagementMode.Disabled => 2,
                    _ => 0
                };
            }
            policyCombo.SelectedIndex = policyIndex;
            Controls.Add(policyCombo);
            top += 35;

            var hideCheckbox = new CheckBox
            {
                Text = "Hide game from standard library view",
                Checked = installation?.IsHidden ?? false,
                Top = top,
                Left = 20,
                AutoSize = true
            };
            Controls.Add(hideCheckbox);
            top += 30;

            // Configuration controls
            var hudCheckbox = new CheckBox
            {
                Text = "Enable DXVK HUD (fps, devinfo)",
                Checked = _profile.HudEnabled,
                Top = top,
                Left = 20,
                AutoSize = true
            };
            Controls.Add(hudCheckbox);
            top += 35;

            var frameLimitLabel = new Label
            {
                Text = "Frame Limit (0 for unlimited):",
                AutoSize = true,
                Top = top,
                Left = 20
            };
            Controls.Add(frameLimitLabel);
            top += 25;

            var frameLimitBox = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 1000,
                Value = _profile.FrameLimit,
                Top = top,
                Left = 20,
                Width = 120
            };
            Controls.Add(frameLimitBox);
            top += 45;

            var btnSave = new Button
            {
                Text = "Save Settings",
                Top = top,
                Left = 20,
                Width = 120,
                Height = 32
            };
            btnSave.Click += (_, _) =>
            {
                _profile.HudEnabled = hudCheckbox.Checked;
                _profile.FrameLimit = (int)frameLimitBox.Value;
                _profileStore.Save(_profile);

                if (installation != null)
                {
                    installation.Configuration.HudEnabled = hudCheckbox.Checked;
                    installation.Configuration.FrameLimit = (int)frameLimitBox.Value;
                    installation.Configuration.FrameLimitEnabled = frameLimitBox.Value > 0;
                    installation.IsHidden = hideCheckbox.Checked;

                    installation.ManagementPolicy = policyCombo.SelectedIndex switch
                    {
                        1 => ManagementPolicy.Automatic(),
                        2 => ManagementPolicy.Disabled(),
                        _ => ManagementPolicy.UseGlobal()
                    };

                    _gameLibraryStore.Save(installation);
                }

                MessageBox.Show("Settings saved.", "DXVK Companion", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            };
            Controls.Add(btnSave);

            var btnClose = new Button
            {
                Text = "Close",
                Top = top,
                Left = 160,
                Width = 100,
                Height = 32
            };
            btnClose.Click += (_, _) => Close();
            Controls.Add(btnClose);

            var btnReport = new Button
            {
                Text = "Diagnostic Report...",
                Top = top,
                Left = 270,
                Width = 145,
                Height = 32
            };
            btnReport.Click += (_, _) =>
            {
                string report = DiagnosticReportGenerator.Generate(_profile, installation);
                using var dialog = new DiagnosticReportPreviewDialog(report);
                dialog.ShowDialog(this);
            };
            Controls.Add(btnReport);
        }
    }
}
