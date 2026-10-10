using System;
using System.Drawing;
using System.Windows.Forms;
using DXVKCompanion.UI;

namespace DXVKCompanion.DiagnosticPreviewRunner
{
    public static class Program
    {
        private const string ExpectedPayload =
            "DXVK Companion Diagnostic Report (v1)\r\n" +
            "App Version: 1.0.0\r\n" +
            "Recorded API: DX11\r\n" +
            "Recorded Arch: x64\r\n" +
            "Restoration State: AttentionRequired\r\n" +
            "Managed DXVK: 2.5\r\n" +
            "Conflict Flags: None\r\n" +
            "Installation Policy Mode: UseGlobal\r\n" +
            "Refusal Details: Unavailable\r\n" +
            "Evidence Freshness: RecordedSnapshotOnly\r\n" +
            "Anti-Cheat Assessment: NotAcquired";

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length != 1 || (args[0] != "no-copy" && args[0] != "copy"))
            {
                Console.WriteLine("Usage: DiagnosticPreviewRunner [no-copy | copy]");
                return 1;
            }

            string mode = args[0];
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (mode == "no-copy")
            {
                Console.WriteLine("MODE: no-copy");
                Console.WriteLine("Action: Launching DiagnosticReportPreviewDialog for visual layout inspection.");
                Console.WriteLine("Instruction: Inspect preview dialog layout and notice. Close dialog without clicking Copy.");

                bool copyAttempted;
                using (var dialog = new DiagnosticReportPreviewDialog(ExpectedPayload))
                {
                    dialog.ShowDialog();
                    copyAttempted = WasCopyAttempted(dialog);
                }

                if (copyAttempted)
                {
                    Console.WriteLine("NO_COPY_INSPECTION: FAILED (copy interaction was attempted during no-copy mode)");
                    return 1;
                }

                Console.WriteLine("DIALOG_RESULT: CLOSED_NORMAL");
                Console.WriteLine("NO_COPY_INSPECTION: COMPLETED");
                return 0;
            }

            // mode == "copy"
            Console.WriteLine("MODE: copy");
            Console.WriteLine("Action: Launching DiagnosticReportPreviewDialog.");
            Console.WriteLine("Instruction: Click 'Copy to Clipboard', verify green success status, then close dialog.");

            bool copySuccessEstablished;
            using (var dialog = new DiagnosticReportPreviewDialog(ExpectedPayload))
            {
                dialog.ShowDialog();
                copySuccessEstablished = HasSuccessfulCopyFeedback(dialog);
            }

            if (!copySuccessEstablished)
            {
                Console.WriteLine("COPY_STATUS: FAILED_OR_NOT_ATTEMPTED (copy success feedback was not established)");
                return 1;
            }

            Console.WriteLine("COPY_STATUS: SUCCESS_CONFIRMED");
            Console.WriteLine("DIALOG_RESULT: CLOSED_NORMAL");
            Console.WriteLine("Proceeding to manual payload paste verification.");

            bool matched = ShowVerificationDialog();
            if (matched)
            {
                Console.WriteLine("EQUALITY_RESULT: MATCH");
                return 0;
            }
            else
            {
                Console.WriteLine("EQUALITY_RESULT: MISMATCH");
                return 1;
            }
        }

        private static bool HasSuccessfulCopyFeedback(DiagnosticReportPreviewDialog dialog)
        {
            foreach (Control control in dialog.Controls)
            {
                if (control is Label label &&
                    label.Text == "Copied to clipboard." &&
                    label.ForeColor == Color.DarkGreen)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool WasCopyAttempted(DiagnosticReportPreviewDialog dialog)
        {
            foreach (Control control in dialog.Controls)
            {
                if (control is Label label &&
                    (label.Visible || !string.IsNullOrEmpty(label.Text)) &&
                    (label.Text == "Copied to clipboard." || label.Text == "Unable to copy to clipboard."))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ShowVerificationDialog()
        {
            using var form = new Form
            {
                Text = "Synthetic Payload Verification",
                Width = 520,
                Height = 420,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterScreen
            };

            var promptLabel = new Label
            {
                Text = "Paste the copied report text into the box below (Ctrl+V) and click Verify:",
                Top = 15,
                Left = 15,
                Width = 475,
                Height = 30
            };
            form.Controls.Add(promptLabel);

            var textBox = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Top = 50,
                Left = 15,
                Width = 475,
                Height = 260,
                Font = new Font(FontFamily.GenericMonospace, 9f)
            };
            form.Controls.Add(textBox);

            bool isMatch = false;

            var btnVerify = new Button
            {
                Text = "Verify",
                Top = 330,
                Left = 310,
                Width = 90,
                Height = 32
            };
            btnVerify.Click += (_, _) =>
            {
                isMatch = string.Equals(textBox.Text, ExpectedPayload, StringComparison.Ordinal);
                form.DialogResult = DialogResult.OK;
                form.Close();
            };
            form.Controls.Add(btnVerify);

            var btnCancel = new Button
            {
                Text = "Cancel",
                Top = 330,
                Left = 410,
                Width = 80,
                Height = 32,
                DialogResult = DialogResult.Cancel
            };
            btnCancel.Click += (_, _) => form.Close();
            form.Controls.Add(btnCancel);

            form.AcceptButton = btnVerify;
            form.CancelButton = btnCancel;

            var result = form.ShowDialog();
            return result == DialogResult.OK && isMatch;
        }
    }
}
