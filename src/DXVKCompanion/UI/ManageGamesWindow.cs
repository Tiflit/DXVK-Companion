using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DXVKCompanion.Models;
using DXVKCompanion.DXVK;
using DXVKCompanion.Storage;

namespace DXVKCompanion.UI
{
    /// <summary>
    /// Lists every game DXVK Companion has ever seen (not just the currently-running one),
    /// letting you enable/disable DXVK per title and push updates to some or all of them.
    /// Enable/Disable/Update here go through the same exit-safety queueing as the tray menu —
    /// if a listed game happens to be running right now, the action queues instead of touching
    /// its files immediately.
    /// </summary>
    public class ManageGamesWindow : Form
    {
        private readonly ProfileStore _profiles;
        private readonly DxvkManager _dxvk;
        private readonly ListView _listView;
        private readonly Label _statusLabel;

        public ManageGamesWindow(ProfileStore profiles, DxvkManager dxvk)
        {
            _profiles = profiles;
            _dxvk = dxvk;

            Text = "DXVK Companion — Manage Games";
            Width = 780;
            Height = 460;
            StartPosition = FormStartPosition.CenterScreen;

            _listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true
            };
            _listView.Columns.Add("Game", 220);
            _listView.Columns.Add("API", 90);
            _listView.Columns.Add("Arch", 60);
            _listView.Columns.Add("DXVK", 70);
            _listView.Columns.Add("Version", 100);
            _listView.Columns.Add("Path", 380);

            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                Text = "",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0)
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(6)
            };

            var btnEnable = new Button { Text = "Enable DXVK", AutoSize = true };
            var btnDisable = new Button { Text = "Disable DXVK", AutoSize = true };
            var btnUpdateSelected = new Button { Text = "Update Selected", AutoSize = true };
            var btnUpdateAllEnabled = new Button { Text = "Update All Enabled", AutoSize = true };
            var btnRefresh = new Button { Text = "Refresh", AutoSize = true };

            btnEnable.Click += async (_, _) => await RunOnSelected("Enabling", p => _dxvk.RequestEnableByPathAsync(p));
            btnDisable.Click += async (_, _) => await RunOnSelected("Disabling", p => _dxvk.RequestDisableByPathAsync(p));
            btnUpdateSelected.Click += async (_, _) => await RunOnSelected("Updating", p => _dxvk.RequestUpdateByPathAsync(p));
            btnUpdateAllEnabled.Click += async (_, _) => await RunUpdateAllEnabled();
            btnRefresh.Click += (_, _) => RefreshList();

            buttonPanel.Controls.AddRange(new Control[]
            {
                btnEnable, btnDisable, btnUpdateSelected, btnUpdateAllEnabled, btnRefresh
            });

            Controls.Add(_listView);
            Controls.Add(buttonPanel);
            Controls.Add(_statusLabel);

            RefreshList();
        }

        private void RefreshList()
        {
            _listView.Items.Clear();

            foreach (var profile in _profiles.GetAll().OrderBy(p => p.ExeName))
            {
                var item = new ListViewItem(profile.ExeName) { Tag = profile };
                item.SubItems.Add(profile.Api.ToString());
                item.SubItems.Add(profile.Architecture);
                item.SubItems.Add(profile.DxvkEnabled ? "Enabled" : "Disabled");
                item.SubItems.Add(profile.DxvkVersion ?? "-");
                item.SubItems.Add(profile.ExePath);
                _listView.Items.Add(item);
            }

            _statusLabel.Text = $"{_listView.Items.Count} game(s) tracked.";
        }

        private System.Collections.Generic.List<GameProfile> GetSelectedProfiles()
        {
            var result = new System.Collections.Generic.List<GameProfile>();
            foreach (ListViewItem item in _listView.SelectedItems)
                if (item.Tag is GameProfile p)
                    result.Add(p);
            return result;
        }

        private async Task RunOnSelected(string verb, Func<GameProfile, Task<DxvkActionResult>> action)
        {
            var selected = GetSelectedProfiles();
            if (selected.Count == 0)
            {
                MessageBox.Show("Select one or more games first.", "DXVK Companion");
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
            _statusLabel.Text = $"{verb} done — {applied} applied, {queued} queued (game still running), {failed} failed.";
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

            _statusLabel.Text = $"Update all done — {applied} applied, {queued} queued (game still running), {failed} failed.";
        }
    }
}
