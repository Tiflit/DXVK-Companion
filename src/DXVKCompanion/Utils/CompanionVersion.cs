using System;
using System.Globalization;
using System.Reflection;

namespace DXVKCompanion.Utils
{
    public static class CompanionVersion
    {
        /// <summary>
        /// This app's own version, read from the assembly's InformationalVersion —
        /// set automatically from the &lt;Version&gt; property in the .csproj.
        /// </summary>
        public static string Current =>
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "0.0.0";

        /// <summary>
        /// Compares the local Companion version against a GitHub release tag to determine
        /// whether the local version is strictly older.
        /// Supported versions are stable three-component values (MAJOR.MINOR.PATCH) with an
        /// optional single leading 'v' or 'V'.
        /// Comparison is numeric by major, minor, and patch component.
        /// Malformed or unsupported input returns false quietly.
        /// </summary>
        public static bool IsOutdatedComparedTo(string? localVersion, string? githubTag)
        {
            if (!TryParseThreePartVersion(localVersion, out var local) ||
                !TryParseThreePartVersion(githubTag, out var github))
            {
                return false;
            }

            if (local.Major != github.Major)
            {
                return local.Major < github.Major;
            }

            if (local.Minor != github.Minor)
            {
                return local.Minor < github.Minor;
            }

            return local.Patch < github.Patch;
        }

        private static bool TryParseThreePartVersion(string? input, out (int Major, int Minor, int Patch) version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            string trimmed = input.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(1);
            }

            string[] parts = trimmed.Split('.');
            if (parts.Length != 3)
            {
                return false;
            }

            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor) ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
            {
                return false;
            }

            version = (major, minor, patch);
            return true;
        }
    }
}

