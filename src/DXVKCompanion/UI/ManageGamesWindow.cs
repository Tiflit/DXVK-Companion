using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;

namespace DXVKCompanion.UI
{
    /// <summary>
    /// Status-oriented management UI displaying comprehensive game health,
    /// restoration state, pending actions, and lifecycle management.
    /// </summary>
    public class ManageGamesWindow : Form
    {
        private readonly ProfileStore _profiles;
        private readonly DxvkManager _dxvk;
        private readonly GameLibraryStore _gameLibraryStore;
        private readonly ManagedFileInspector _inspector;
        private readonly ListView _listView;
        private readonly Label _statusLabel;
        private readonly ComboBox _filterCombo;

        public ManageGamesWindow(ProfileStore profiles, DxvkManager dxvk, GameLibraryStore? gameLibraryStore = null)
        {
            _profiles = profiles;
            _dxvk = dxvk;
            _gameLibraryStore = gameLibraryStore ?? new GameLibraryStore();
            _inspector = new ManagedFileInspector(_gameLibraryStore);

            Text = "DXVK Companion — Manage Games";
            Width = 960;
            Height = 540;
            StartPosition = FormStartPosition.CenterScreen;

            var topPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(6),
                FlowDirection = FlowDirection.LeftToRight
            };

            var filterLabel = new Label
            {
                Text = "View:",
                AutoSize = true,
                Margin = new Padding(4, 6, 4, 0)
            };

            _filterCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 180
            };
            _filterCombo.Items.Add("Active Games");
            _filterCombo.Items.Add("Managed / Active Only");
            _filterCombo.Items.Add("Attention Required");
            _filterCombo.Items.Add("Hidden Games");
            _filterCombo.Items.Add("All Tracked Games");
            _filterCombo.SelectedIndex = 0;
            _filterCombo.SelectedIndexChanged += (_, _) => RefreshList();

            topPanel.Controls.Add(filterLabel);
            topPanel.Controls.Add(_filterCombo);

            _listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true
            };
            _listView.Columns.Add("Game", 180);
            _listView.Columns.Add("Status", 130);
            _listView.Columns.Add("API", 80);
            _listView.Columns.Add("Arch", 55);
            _listView.Columns.Add("DXVK", 70);
            _listView.Columns.Add("Version", 85);
            _listView.Columns.Add("Pending", 110);
            _listView.Columns.Add("Path", 340);

            _listView.DoubleClick += (_, _) => OpenSelectedGameDetails();

            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                Text = "",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0)
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(6)
            };

            var btnEnable = new Button { Text = "Enable DXVK", AutoSize = true };
            var btnDisable = new Button { Text = "Disable DXVK", AutoSize = true };
            var btnReapply = new Button { Text = "Reapply DXVK", AutoSize = true };
            var btnAdopt = new Button { Text = "Adopt Existing", AutoSize = true };
            var btnDetails = new Button { Text = "Game Details...", AutoSize = true };
            var btnUpdateSelected = new Button { Text = "Update Selected", AutoSize = true };
            var btnUpdateAll = new Button { Text = "Update All Enabled", AutoSize = true };
            var btnRestoreAll = new Button { Text = "Restore All", AutoSize = true };
            var btnRefresh = new Button { Text = "Refresh", AutoSize = true };

            btnEnable.Click += async (_, _) => await RunOnSelected("Enabling", p => _dxvk.RequestEnableByPathAsync(p));
            btnDisable.Click += async (_, _) => await RunOnSelected("Disabling", p => _dxvk.RequestDisableByPathAsync(p));
            btnReapply.Click += async (_, _) => await RunOnSelected("Reapplying", p => _dxvk.RequestReapplyByPathAsync(p, updateBaseline: true));
            btnAdopt.Click += async (_, _) => await RunAdoptSelected();
            btnDetails.Click += (_, _) => OpenSelectedGameDetails();
            btnUpdateSelected.Click += async (_, _) => await RunOnSelected("Updating", p => _dxvk.RequestUpdateByPathAsync(p));
            btnUpdateAll.Click += async (_, _) => await RunUpdateAllEnabled();
            btnRestoreAll.Click += async (_, _) => await RunRestoreAll();
            btnRefresh.Click += (_, _) => RefreshList();

            buttonPanel.Controls.AddRange(new Control[]
            {
                btnEnable, btnDisable, btnReapply, btnAdopt, btnDetails, btnUpdateSelected, btnUpdateAll, btnRestoreAll, btnRefresh
            });

            Controls.Add(_listView);
            Controls.Add(topPanel);
            Controls.Add(buttonPanel);
            Controls.Add(_statusLabel);

            RefreshList();
        }

        private void RefreshList()
        {
            _inspector.InspectAll();
            _listView.Items.Clear();

            foreach (var profile in _profiles.GetAll().OrderBy(p => p.ExeName))
            {
                string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                var installation = !string.IsNullOrWhiteSpace(gameDir)
                    ? _gameLibraryStore.FindByInstallationPath(gameDir)
                    : null;

                string filter = _filterCombo?.SelectedItem?.ToString() ?? "Active Games";
                bool isHidden = installation?.IsHidden ?? false;

                if (filter == "Active Games" && isHidden)
                    continue;
                if (filter == "Hidden Games" && !isHidden)
                    continue;
                if (filter == "Managed / Active Only" && (!profile.DxvkEnabled || isHidden))
                    continue;
                if (filter == "Attention Required" &&
                    (installation?.RestorationState != RestorationState.AttentionRequired &&
                     installation?.ConflictFlags == InstallationConflictFlags.None))
                    continue;

                string statusText = "Clean / Native";
                string pendingText = "-";

                if (installation != null)
                {
                    if (installation.ConflictFlags != InstallationConflictFlags.None)
                    {
                        statusText = $"Conflict: {installation.ConflictFlags}";
                    }
                    else if (installation.RestorationState == RestorationState.AttentionRequired)
                    {
                        statusText = "Attention Required";
                    }
                    else if (installation.RestorationState == RestorationState.Managed)
                    {
                        statusText = "Managed";
                    }
                    else if (installation.RestorationState == RestorationState.Restored)
                    {
                        statusText = "Restored";
                    }

                    if (installation.PendingAction != null && installation.PendingAction.IsPending)
                    {
                        pendingText = $"{installation.PendingAction.Type}";
                    }
                }
                else if (profile.DxvkEnabled)
                {
                    statusText = "Enabled";
                }

                var item = new ListViewItem(profile.ExeName) { Tag = profile };
                item.SubItems.Add(statusText);
                item.SubItems.Add(profile.Api.ToString());
                item.SubItems.Add(profile.Architecture);
                item.SubItems.Add(profile.DxvkEnabled ? "Enabled" : "Disabled");
                item.SubItems.Add(profile.DxvkVersion ?? "-");
                item.SubItems.Add(pendingText);
                item.SubItems.Add(profile.ExePath);

                if (statusText.StartsWith("Attention", StringComparison.OrdinalIgnoreCase) ||
                    statusText.StartsWith("Conflict", StringComparison.OrdinalIgnoreCase))
                {
                    item.ForeColor = Color.DarkOrange;
                }
                else if (statusText.Equals("Managed", StringComparison.OrdinalIgnoreCase) ||
                         statusText.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                {
                    item.ForeColor = Color.DarkGreen;
                }

                _listView.Items.Add(item);
            }

            _statusLabel.Text = $"{_listView.Items.Count} game(s) tracked. Status inspected.";
        }

        private List<GameProfile> GetSelectedProfiles()
        {
            var result = new List<GameProfile>();
            foreach (ListViewItem item in _listView.SelectedItems)
            {
                if (item.Tag is GameProfile p)
                    result.Add(p);
            }
            return result;
        }

        private void OpenSelectedGameDetails()
        {
            var selected = GetSelectedProfiles();
            if (selected.Count == 0)
            {
                MessageBox.Show("Select a game first to view details.", "DXVK Companion", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var window = new GameDetailsWindow(selected[0], _profiles, _dxvk, _gameLibraryStore);
            window.FormClosed += (_, _) => RefreshList();
            window.ShowDialog(this);
        }

        private async Task RunOnSelected(string verb, Func<GameProfile, Task<DxvkActionResult>> action)
        {
            var selected = GetSelectedProfiles();
            if (selected.Count == 0)
            {
                MessageBox.Show("Select one or more games first.", "DXVK Companion", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _statusLabel.Text = $"{verb}...";

            int applied = 0, queued = 0, failed = 0;

            foreach (var profile in selected)
            {
                var result = await action(profile);
                switch (result)
                {
                    case DxvkActionResult.Applied: applied++; break;
                    case DxvkActionResult.Queued: queued++; break;
                    case DxvkActionResult.Failed: failed++; break;
                }
            }

            RefreshList();
            _statusLabel.Text = $"{verb} complete — {applied} applied, {queued} queued (running), {failed} failed.";
        }

        private async Task RunAdoptSelected()
        {
            var selected = GetSelectedProfiles();
            if (selected.Count == 0)
            {
                MessageBox.Show("Select one or more games to adopt.", "DXVK Companion", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int adopted = 0, failed = 0;

            foreach (var profile in selected)
            {
                bool ok = await _dxvk.AdoptExistingAsync(profile);
                if (ok) adopted++;
                else failed++;
            }

            RefreshList();

            if (adopted > 0)
            {
                MessageBox.Show($"Successfully adopted existing official DXVK release for {adopted} game(s).",
                    "DXVK Adoption", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("No adoptable official DXVK release was recognized in the selected game directory.",
                    "DXVK Adoption", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task RunUpdateAllEnabled()
        {
            _statusLabel.Text = "Checking for updates...";

            var results = await _dxvk.UpdateAllEnabledAsync();
            RefreshList();

            if (results.Count == 0)
            {
                _statusLabel.Text = "All enabled games are already up to date (or none are enabled).";
                return;
            }

            int applied = results.Values.Count(r => r == DxvkActionResult.Applied);
            int queued = results.Values.Count(r => r == DxvkActionResult.Queued);
            int failed = results.Values.Count(r => r == DxvkActionResult.Failed);

            _statusLabel.Text = $"Update all complete — {applied} applied, {queued} queued (running), {failed} failed.";
        }

        private async Task RunRestoreAll()
        {
            var confirm = MessageBox.Show(
                "Are you sure you want to restore all DXVK Companion managed games back to their original baseline?",
                "Restore All", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes)
                return;

            _statusLabel.Text = "Restoring all managed games...";
            var summary = await _dxvk.RestoreAllAsync();
            RefreshList();

            string msg = $"Restore All completed:\n\n" +
                         $"• Restored: {summary.Restored}\n" +
                         $"• Already clean: {summary.AlreadyRestored}\n" +
                         $"• Queued (games running): {summary.QueuedRunning}\n" +
                         $"• Attention required / failed: {summary.FailedOrAttentionRequired}";

            MessageBox.Show(msg, "Restore All", MessageBoxButtons.OK,
                summary.FailedOrAttentionRequired > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
    }
}
