using System.Windows.Forms;

namespace DXVKCompanion.UI
{
    public class SettingsWindow : Form
    {
        public SettingsWindow()
        {
            Text = "DXVK Companion Settings";
            Width = 400;
            Height = 300;

            var label = new Label
            {
                Text = "Global settings will be added here.",
                AutoSize = true,
                Top = 20,
                Left = 20
            };

            Controls.Add(label);
        }
    }
}
