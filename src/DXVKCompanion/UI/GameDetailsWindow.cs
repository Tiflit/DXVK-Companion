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
            Height = 650;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            string gameDir = Path.GetDirectoryName(_profile.ExePath) ?? string.Empty;
            var installation = _gameLibraryStore.FindInstallationForExecutable(_profile.ExePath)
                ?? (!string.IsNullOrWhiteSpace(gameDir) ? _gameLibraryStore.FindByInstallationPath(gameDir) : null);
            bool isPreexistingInstallation = installation != null;
            if (installation == null && !string.IsNullOrWhiteSpace(gameDir))
            {
                installation = _gameLibraryStore.GetOrCreateInstallation(gameDir, _profile.ExeName);
            }

            var initialConfigDisplay = GameDetailsConfigurationSelection.GetInitialDisplay(
                installation,
                isPreexistingInstallation,
                _profile);

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

            string recordedVersion = GameVersionPresentation.GetRecordedManagedVersion(installation, isPreexistingInstallation);
            var dxvkLabel = new Label
            {
                Text = $"Recorded Managed Version: {recordedVersion}",
                AutoSize = true,
                Top = top,
                Left = 20
            };
            Controls.Add(dxvkLabel);
            top += 25;

            var historicalVersionLabel = GameVersionPresentation.FormatHistoricalProfileVersionLabel(_profile);
            if (historicalVersionLabel != null)
            {
                var profileVersionLabel = new Label
                {
                    Text = historicalVersionLabel,
                    AutoSize = true,
                    Top = top,
                    Left = 20,
                    ForeColor = Color.DimGray
                };
                Controls.Add(profileVersionLabel);
                top += 25;
            }

            var pendingActionLabel = new Label
            {
                Text = PendingActionPresentation.FormatRecordedPendingActionLabel(installation, isPreexistingInstallation),
                AutoSize = true,
                Top = top,
                Left = 20
            };
            Controls.Add(pendingActionLabel);
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
                    var presentation = ReapplyResultPresentation.GetPresentation(res, _dxvk.LastRefusalReason);
                    MessageBox.Show(presentation.Message, presentation.Title, MessageBoxButtons.OK, presentation.Icon);
                    if (presentation.ShouldCloseWindow)
                    {
                        Close();
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
            foreach (var label in GameDetailsPolicySelection.GetAvailableLabels(installation?.ManagementPolicy))
            {
                policyCombo.Items.Add(label);
            }
            policyCombo.SelectedIndex = GameDetailsPolicySelection.GetInitialSelectedIndex(installation?.ManagementPolicy);
            Controls.Add(policyCombo);
            top += 35;

            // Version selection controls
            var versionLabel = new Label
            {
                Text = "DXVK Version:",
                AutoSize = true,
                Top = top,
                Left = 20
            };
            Controls.Add(versionLabel);
            top += 25;

            var versionCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Top = top,
                Left = 20,
                Width = 240
            };

            var localVersions = DxvkLocalReleaseLibrary.GetAvailableVersions(
                _dxvk.DxvkSourceDir,
                _profile.Api,
                _profile.Architecture);

            string? currentPinnedVersion = installation?.ManagementPolicy?.PinnedDxvkVersion;
            var versionLabels = GameDetailsVersionSelection.GetAvailableLabels(localVersions, currentPinnedVersion);
            foreach (var label in versionLabels)
            {
                versionCombo.Items.Add(label);
            }
            versionCombo.SelectedIndex = GameDetailsVersionSelection.GetInitialSelectedIndex(versionLabels, currentPinnedVersion);

            bool hasPendingWork = installation?.PendingAction != null && installation.PendingAction.IsPending;
            if (hasPendingWork)
            {
                versionCombo.Enabled = false;

                var pendingLockLabel = new Label
                {
                    Text = "Version selection is locked while pending work is queued.",
                    AutoSize = true,
                    Top = top + 4,
                    Left = 270,
                    ForeColor = Color.DimGray,
                    Font = new Font(FontFamily.GenericSansSerif, 8, FontStyle.Italic)
                };
                Controls.Add(pendingLockLabel);
            }
            Controls.Add(versionCombo);
            top += 35;

            bool PendingNow()
            {
                var latest = _gameLibraryStore.FindInstallationForExecutable(_profile.ExePath);
                return latest?.PendingAction?.IsPending == true || _dxvk.HasTransientPendingAction(_profile.ExePath);
            }
            var browseVersions = new Button
            {
                Text = "Browse / Download official versions", Left = 20, Top = top, AutoSize = true
            };
            browseVersions.Click += (_, _) =>
            {
                string? selected = GameDetailsVersionSelection.ResolveSelectedVersion(
                    versionCombo.SelectedIndex, versionLabels, currentPinnedVersion);
                using var browser = new GameDetailsReleaseDownloadWindow(_dxvk, _profile, PendingNow);
                browser.ShowDialog(this);
                if (IsDisposed) return;
                versionCombo.Enabled = !PendingNow();
                if (!browser.LibraryChanged) return;
                versionLabels = GameDetailsVersionSelection.GetAvailableLabels(
                    DxvkLocalReleaseLibrary.GetAvailableVersions(_dxvk.DxvkSourceDir, _profile.Api, _profile.Architecture), selected);
                versionCombo.Items.Clear();
                foreach (var label in versionLabels) versionCombo.Items.Add(label);
                versionCombo.SelectedIndex = GameDetailsVersionSelection.GetInitialSelectedIndex(versionLabels, selected);
                versionCombo.Enabled = !PendingNow();
            };
            Controls.Add(browseVersions);
            top += 40;
            AutoScroll = true;

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
                Checked = initialConfigDisplay.HudEnabled,
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
                Minimum = GameDetailsConfigurationSelection.MinFrameLimit,
                Maximum = GameDetailsConfigurationSelection.MaxFrameLimit,
                Value = initialConfigDisplay.FrameLimit,
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
                GameDetailsConfigurationSelection.ApplyConfiguration(
                    hudCheckbox.Checked,
                    (int)frameLimitBox.Value,
                    initialConfigDisplay,
                    installation,
                    isPreexistingInstallation,
                    _profile);

                _profileStore.Save(_profile);

                if (installation != null)
                {
                    installation.IsHidden = hideCheckbox.Checked;

                    var resolvedPolicy = GameDetailsPolicySelection.ResolvePolicy(
                        policyCombo.SelectedIndex,
                        installation.ManagementPolicy);

                    if (versionCombo.Enabled && !PendingNow())
                    {
                        string? selectedVer = GameDetailsVersionSelection.ResolveSelectedVersion(
                            versionCombo.SelectedIndex,
                            versionLabels,
                            installation.ManagementPolicy?.PinnedDxvkVersion);

                        GameDetailsVersionSelection.ApplyVersionSelection(resolvedPolicy, selectedVer);
                    }
                    else
                    {
                        // Retain saved choice when Save Settings is used while pending
                        resolvedPolicy.PinnedDxvkVersion = installation.ManagementPolicy?.PinnedDxvkVersion;
                    }

                    installation.ManagementPolicy = resolvedPolicy;

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
                string appVersion = GetAppVersion();
                string report = DiagnosticReportGenerator.Generate(_profile, installation, appVersion);
                using var dialog = new DiagnosticReportPreviewDialog(report);
                dialog.ShowDialog(this);
            };
            Controls.Add(btnReport);
        }

        private static string GetAppVersion()
        {
            var version = typeof(GameDetailsWindow).Assembly.GetName().Version;
            if (version == null)
            {
                return "Unavailable";
            }

            return version.Build >= 0
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : $"{version.Major}.{version.Minor}";
        }
    }
}
