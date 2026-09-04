using System.Drawing;
using System.Windows.Forms;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;

namespace DXVKCompanion.UI
{
    public class GameDetailsWindow : Form
    {
        private readonly GameProfile _profile;
        private readonly ProfileStore _store;

        public GameDetailsWindow(GameProfile profile, ProfileStore store)
        {
            _profile = profile;
            _store = store;

            Text = $"Game Details - {_profile.ExeName}";
            Width = 500;
            Height = 400;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            var apiLabel = new Label
            {
                Text = $"API: {_profile.Api}",
                AutoSize = true,
                Top = 20,
                Left = 20
            };
            Controls.Add(apiLabel);

            var archLabel = new Label
            {
                Text = $"Architecture: {_profile.Architecture}",
                AutoSize = true,
                Top = 50,
                Left = 20
            };
            Controls.Add(archLabel);

            var dxvkLabel = new Label
            {
                Text = $"DXVK Version: {_profile.DxvkVersion ?? "None"}",
                AutoSize = true,
                Top = 80,
                Left = 20
            };
            Controls.Add(dxvkLabel);

            var hudCheckbox = new CheckBox
            {
                Text = "Enable DXVK HUD",
                Checked = _profile.HudEnabled,
                Top = 120,
                Left = 20
            };
            hudCheckbox.CheckedChanged += (_, _) =>
            {
                _profile.HudEnabled = hudCheckbox.Checked;
                _store.Save(_profile);
            };
            Controls.Add(hudCheckbox);

            var frameLimitLabel = new Label
            {
                Text = "Frame Limit:",
                AutoSize = true,
                Top = 160,
                Left = 20
            };
            Controls.Add(frameLimitLabel);

            var frameLimitBox = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 1000,
                Value = _profile.FrameLimit,
                Top = 180,
                Left = 20
            };
            frameLimitBox.ValueChanged += (_, _) =>
            {
                _profile.FrameLimit = (int)frameLimitBox.Value;
                _store.Save(_profile);
            };
            Controls.Add(frameLimitBox);
        }
    }
}
