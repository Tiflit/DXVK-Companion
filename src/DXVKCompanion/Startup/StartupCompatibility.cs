using System;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;

namespace DXVKCompanion.Startup
{
    internal enum StartupAction
    {
        Continue = 0,
        RefuseIncompatibleSchema = 1
    }

    internal sealed class StartupPreflightResult
    {
        public StartupAction Action { get; }
        public bool ShouldRefuse => Action == StartupAction.RefuseIncompatibleSchema;
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
    }

    internal static class StartupCompatibility
    {
        internal static Action<string, string>? MessagePresenter { get; set; }

        public static StartupPreflightResult Evaluate(string? libraryFilePath = null)
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

            const string title = "DXVK Companion - Incompatible Library";

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
