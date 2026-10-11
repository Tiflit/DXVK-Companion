using System;
using System.Collections.Generic;
using DXVKCompanion.Models;

namespace DXVKCompanion.Diagnostics
{
    /// <summary>
    /// Pure, stateless generator for privacy-safe minimal diagnostic reports.
    /// Operates exclusively on supplied in-memory snapshot models and trusted app version.
    /// Does not consult stores, files, processes, network, or logs, and performs no mutations.
    /// </summary>
    public static class DiagnosticReportGenerator
    {
        public const string ReportSchemaVersion = "v2";

        public static string Generate(
            GameProfile? profile,
            GameInstallation? installation,
            string? appVersion = null)
        {
            string validAppVersion = IsValidNumericVersion(appVersion) ? appVersion! : "Unavailable";

            string apiText = profile != null ? FormatGraphicsApi(profile.Api) : "Unknown";
            string archText = profile != null ? FormatArchitecture(profile.Architecture) : "Unknown";

            string restorationText;
            string managedVersionText;
            string conflictFlagsText;
            string policyModeText;
            string pendingActionText;
            string cancelledActionText;

            if (installation != null)
            {
                restorationText = FormatRestorationState(installation.RestorationState);
                managedVersionText = IsValidNumericVersion(installation.ManagedDxvkVersion)
                    ? installation.ManagedDxvkVersion!
                    : "Unavailable";
                conflictFlagsText = FormatConflictFlags(installation.ConflictFlags);
                policyModeText = installation.ManagementPolicy != null
                    ? FormatManagementMode(installation.ManagementPolicy.Mode)
                    : "Unavailable";
                pendingActionText = FormatActionType(installation.PendingAction);
                cancelledActionText = FormatActionType(installation.LastCancelledAction);
            }
            else
            {
                restorationText = "Unavailable";
                managedVersionText = "Unavailable";
                conflictFlagsText = "Unavailable";
                policyModeText = "Unavailable";
                pendingActionText = "Unavailable";
                cancelledActionText = "Unavailable";
            }

            return string.Join(Environment.NewLine, new[]
            {
                $"DXVK Companion Diagnostic Report ({ReportSchemaVersion})",
                $"App Version: {validAppVersion}",
                $"Recorded API: {apiText}",
                $"Recorded Arch: {archText}",
                $"Restoration State: {restorationText}",
                $"Managed DXVK: {managedVersionText}",
                $"Conflict Flags: {conflictFlagsText}",
                $"Installation Policy Mode: {policyModeText}",
                $"Recorded Pending Action: {pendingActionText}",
                $"Recorded Last Cancelled Action: {cancelledActionText}",
                "Refusal Details: Unavailable",
                "Evidence Freshness: RecordedSnapshotOnly",
                "Anti-Cheat Assessment: NotAcquired"
            });
        }

        private static bool IsValidNumericVersion(string? input)
        {
            if (string.IsNullOrEmpty(input) || input.Length > 29)
            {
                return false;
            }

            // Must contain only ASCII digits and dots
            foreach (char c in input)
            {
                if (c != '.' && (c < '0' || c > '9'))
                {
                    return false;
                }
            }

            string[] parts = input.Split('.');
            if (parts.Length != 2 && parts.Length != 3)
            {
                return false;
            }

            foreach (string part in parts)
            {
                if (part.Length < 1 || part.Length > 9)
                {
                    return false;
                }
            }

            return true;
        }

        private static string FormatArchitecture(string? arch)
        {
            if (string.IsNullOrEmpty(arch))
            {
                return "Unknown";
            }

            if (string.Equals(arch, "x64", StringComparison.OrdinalIgnoreCase))
            {
                return "x64";
            }
            if (string.Equals(arch, "x86", StringComparison.OrdinalIgnoreCase))
            {
                return "x86";
            }
            if (string.Equals(arch, "x32", StringComparison.OrdinalIgnoreCase))
            {
                return "x32";
            }

            return "Unknown";
        }

        private static string FormatGraphicsApi(GraphicsApi api)
        {
            return api switch
            {
                GraphicsApi.DX9 => "DX9",
                GraphicsApi.DX10 => "DX10",
                GraphicsApi.DX11 => "DX11",
                GraphicsApi.DX12 => "DX12",
                GraphicsApi.Vulkan => "Vulkan",
                GraphicsApi.ModernAPI => "ModernAPI",
                GraphicsApi.Unknown => "Unknown",
                _ => "Unknown"
            };
        }

        private static string FormatRestorationState(RestorationState state)
        {
            return state switch
            {
                RestorationState.None => "None",
                RestorationState.Managed => "Managed",
                RestorationState.Restored => "Restored",
                RestorationState.AttentionRequired => "AttentionRequired",
                _ => "Unavailable"
            };
        }

        private static string FormatManagementMode(ManagementMode mode)
        {
            return mode switch
            {
                ManagementMode.UseGlobal => "UseGlobal",
                ManagementMode.Automatic => "Automatic",
                ManagementMode.PinnedVersion => "PinnedVersion",
                ManagementMode.Disabled => "Disabled",
                _ => "Unavailable"
            };
        }

        private static string FormatConflictFlags(InstallationConflictFlags flags)
        {
            const int knownMask = (int)(InstallationConflictFlags.DxvkVersion
                                      | InstallationConflictFlags.Architecture
                                      | InstallationConflictFlags.FrameLimit
                                      | InstallationConflictFlags.UnknownOriginalFile);

            int raw = (int)flags;
            if ((raw & ~knownMask) != 0)
            {
                return "Unknown";
            }

            if (flags == InstallationConflictFlags.None)
            {
                return "None";
            }

            var list = new List<string>(4);
            if ((flags & InstallationConflictFlags.DxvkVersion) != 0)
            {
                list.Add("DxvkVersion");
            }
            if ((flags & InstallationConflictFlags.Architecture) != 0)
            {
                list.Add("Architecture");
            }
            if ((flags & InstallationConflictFlags.FrameLimit) != 0)
            {
                list.Add("FrameLimit");
            }
            if ((flags & InstallationConflictFlags.UnknownOriginalFile) != 0)
            {
                list.Add("UnknownOriginalFile");
            }

            return string.Join(", ", list);
        }

        private static string FormatActionType(PendingAction? action)
        {
            if (action == null)
            {
                return "None";
            }

            return action.Type switch
            {
                PendingActionType.None => "None",
                PendingActionType.Install => "Install",
                PendingActionType.Update => "Update",
                PendingActionType.Reapply => "Reapply",
                PendingActionType.Restore => "Restore",
                _ => "Unknown"
            };
        }
    }
}
