using System.Windows.Forms;
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

            Text = "DXVK Companion Settings";
            Width = 400;
            Height = 200;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            var autoEnableCheckbox = new CheckBox
            {
                Text = "Auto-enable DXVK for new games (experimental)",
                Checked = _settings.AutoEnableDxvkForNewGames,
                AutoSize = true,
                Top = 20,
                Left = 20
            };
            autoEnableCheckbox.CheckedChanged += (_, _) =>
            {
                _settings.AutoEnableDxvkForNewGames = autoEnableCheckbox.Checked;
                _settings.Save();
            };
            Controls.Add(autoEnableCheckbox);

            var startupCheckbox = new CheckBox
            {
                Text = "Launch DXVK Companion on Windows startup",
                Checked = _settings.LaunchOnStartup,
                AutoSize = true,
                Top = 60,
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
        }
    }
}
