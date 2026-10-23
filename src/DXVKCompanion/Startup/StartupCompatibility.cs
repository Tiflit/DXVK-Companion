using System;
using System.IO;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;

namespace DXVKCompanion.Startup
{
    internal enum StartupAction
    {
        Continue = 0,
        RefuseIncompatibleSchema = 1,
        RefuseRecoveryEvidence = 2
    }

    internal sealed class StartupPreflightResult
    {
        public StartupAction Action { get; }
        public bool ShouldRefuse => Action != StartupAction.Continue;
        public string? FoundSchemaVersionText { get; }
        public int SupportedSchemaVersion { get; }
        public string? Message { get; }

        public StartupPreflightResult(
            StartupAction action,
            string? foundSchemaVersionText = null,
            int supportedSchemaVersion = GameLibrary.CurrentSchemaVersion,
            string? message = null)
        {
            Action = action;
            FoundSchemaVersionText = foundSchemaVersionText;
            SupportedSchemaVersion = supportedSchemaVersion;
            Message = message;
        }

        public static StartupPreflightResult Continue() => new(StartupAction.Continue);

        public static StartupPreflightResult Refuse(string foundSchemaVersionText, int supportedSchemaVersion, string message) =>
            new(StartupAction.RefuseIncompatibleSchema, foundSchemaVersionText, supportedSchemaVersion, message);

        internal static StartupPreflightResult RecoveryRefusal() => new(
            StartupAction.RefuseRecoveryEvidence,
            message: "DXVK Companion found unfinished file-management evidence or could not verify its recovery storage. No game management has started.\n\n" +
                "Please preserve the application data, backups and recovery evidence. If another Companion instance is running, close it normally before trying again. " +
                "If this warning remains, the retained evidence needs a reviewed recovery assessment before management can resume.");
    }

    internal static class StartupCompatibility
    {
        internal static Action<string, string>? MessagePresenter { get; set; }

        public static StartupPreflightResult Evaluate(string? libraryFilePath = null) =>
            Evaluate(libraryFilePath, backupsDirectory: null);

        internal static StartupPreflightResult Evaluate(
            string? libraryFilePath, string? backupsDirectory, RetainedTransactionProbe? probe = null)
        {
            var targetFile = libraryFilePath ?? GameLibraryPaths.GameLibraryFile;
            var assessment = GameLibrarySchemaCompatibility.AssessFile(targetFile);

            if (assessment.IsRefused)
            {
                string foundText = assessment.FoundSchemaVersionText ?? "unknown";
                int supported = assessment.SupportedSchemaVersion;
                string message = FormatRefusalMessage(foundText, supported);
                return StartupPreflightResult.Refuse(foundText, supported, message);
            }

            try
            {
                backupsDirectory ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(targetFile))!, "Backups");
                if ((probe ?? new RetainedTransactionProbe()).RequiresAttention(backupsDirectory))
                    return StartupPreflightResult.RecoveryRefusal();
            }
            catch
            {
                return StartupPreflightResult.RecoveryRefusal();
            }

            return StartupPreflightResult.Continue();
        }

        public static string FormatRefusalMessage(string foundSchemaText, int supportedSchema)
        {
            return $"This game library was created with a newer version of DXVK Companion (schema version {foundSchemaText}, supported schema version {supportedSchema}).\n\nA compatible version of DXVK Companion is required to open this library.";
        }

        public static void PresentRefusal(StartupPreflightResult result)
        {
            if (!result.ShouldRefuse || string.IsNullOrWhiteSpace(result.Message))
                return;

            string title = result.Action == StartupAction.RefuseRecoveryEvidence
                ? "DXVK Companion - Recovery Attention Required"
                : "DXVK Companion - Incompatible Library";

            if (MessagePresenter != null)
            {
                MessagePresenter(title, result.Message);
            }
            else
            {
                System.Windows.Forms.MessageBox.Show(
                    result.Message,
                    title,
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
        }
    }
}
