using System;
using System.Drawing;
using System.Windows.Forms;

namespace DXVKCompanion.UI
{
    /// <summary>
    /// Modal dialog providing a read-only preview of a diagnostic report
    /// and explicit user-initiated clipboard copy with history/sync disclosure.
    /// </summary>
    public sealed class DiagnosticReportPreviewDialog : Form
    {
        private readonly string _reportText;
        private readonly Label _statusLabel;

        public DiagnosticReportPreviewDialog(string reportText)
        {
            _reportText = reportText ?? string.Empty;

            Text = "Diagnostic Report";
            Width = 520;
            Height = 490;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var warningLabel = new Label
            {
                Text = "Notice: Copying this report will place it on your clipboard. " +
                       "Be aware that clipboard history or cloud clipboard synchronization " +
                       "may record or synchronize copied text across devices.",
                Font = new Font(FontFamily.GenericSansSerif, 8.5f, FontStyle.Regular),
                ForeColor = Color.DarkSlateGray,
                AutoSize = false,
                Top = 15,
                Left = 15,
                Width = 475,
                Height = 45
            };
            Controls.Add(warningLabel);

            var textBox = new TextBox
            {
                Text = _reportText,
                ReadOnly = true,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Top = 65,
                Left = 15,
                Width = 475,
                Height = 310,
                Font = new Font(FontFamily.GenericMonospace, 9f),
                BackColor = SystemColors.Window
            };
            Controls.Add(textBox);

            _statusLabel = new Label
            {
                Text = string.Empty,
                Top = 390,
                Left = 15,
                Width = 240,
                Height = 32,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DarkGreen,
                Visible = false
            };
            Controls.Add(_statusLabel);

            var btnCopy = new Button
            {
                Text = "Copy to Clipboard",
                Top = 390,
                Left = 265,
                Width = 135,
                Height = 32
            };
            btnCopy.Click += (_, _) => CopyToClipboard();
            Controls.Add(btnCopy);

            var btnClose = new Button
            {
                Text = "Close",
                Top = 390,
                Left = 410,
                Width = 80,
                Height = 32,
                DialogResult = DialogResult.OK
            };
            btnClose.Click += (_, _) => Close();
            Controls.Add(btnClose);
        }

        private void CopyToClipboard()
        {
            try
            {
                Clipboard.SetText(_reportText);
                _statusLabel.Text = "Copied to clipboard.";
                _statusLabel.ForeColor = Color.DarkGreen;
                _statusLabel.Visible = true;
            }
            catch
            {
                _statusLabel.Text = "Unable to copy to clipboard.";
                _statusLabel.ForeColor = Color.Red;
                _statusLabel.Visible = true;
                MessageBox.Show(
                    "Unable to copy diagnostic report to clipboard.",
                    "Copy Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }
}
