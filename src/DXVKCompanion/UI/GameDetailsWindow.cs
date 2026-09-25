using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
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
            Height = 480;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            string gameDir = Path.GetDirectoryName(_profile.ExePath) ?? string.Empty;
            var installation = !string.IsNullOrWhiteSpace(gameDir)
                ? _gameLibraryStore.FindByInstallationPath(gameDir)
                : null;

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
            top += 35;

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
                };
                adoptPanel.Controls.Add(adoptLabel);
                adoptPanel.Controls.Add(btnAdopt);
                Controls.Add(adoptPanel);
                top += 45;
            }
            else if (installation?.RestorationState == RestorationState.AttentionRequired)
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
                };
                reapplyPanel.Controls.Add(reapplyLabel);
                reapplyPanel.Controls.Add(btnReapply);
                Controls.Add(reapplyPanel);
                top += 45;
            }

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
        }
    }
}
