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

            Text = $"Game Details - {profile.ExeName}";
            Width = 450;
            Height = 350;

            var hudCheckbox = new CheckBox
            {
                Text = "Enable DXVK HUD",
                Checked = profile.ShowHud,
                Top = 20,
                Left = 20
            };

            hudCheckbox.CheckedChanged += (_, _) =>
            {
                profile.ShowHud = hudCheckbox.Checked;
                _store.Save(profile);
            };

            Controls.Add(hudCheckbox);

            var frameLimitBox = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 1000,
                Value = profile.FrameLimit,
                Top = 60,
                Left = 20
            };

            frameLimitBox.ValueChanged += (_, _) =>
            {
                profile.FrameLimit = (int)frameLimitBox.Value;
                _store.Save(profile);
            };

            Controls.Add(frameLimitBox);
        }
    }
}
