using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DXVKCompanion.Models;

namespace DXVKCompanion.DXVK
{
    public static class DxvkLocalReleaseLibrary
    {
        public static bool IsSafeReleaseDirectoryName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string trimmed = name.Trim();
            if (trimmed == "." || trimmed == "..")
                return false;

            if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains(':'))
                return false;

            if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;

            return true;
        }

        public static string? NormalizeArchitecture(string? architecture)
        {
            if (string.Equals(architecture, "x32", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(architecture, "x86", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(architecture, "win32", StringComparison.OrdinalIgnoreCase))
                return "x32";
            if (string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(architecture, "amd64", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(architecture, "win64", StringComparison.OrdinalIgnoreCase))
                return "x64";
            return null;
        }

        public static string? GetVersionDirectory(string sourceRoot, string? versionTag)
        {
            if (string.IsNullOrWhiteSpace(sourceRoot) || !IsSafeReleaseDirectoryName(versionTag))
                return null;

            try
            {
                string fullSource = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string candidate = Path.Combine(sourceRoot, versionTag!.Trim());
                string fullCandidate = Path.GetFullPath(candidate);

                if (!fullCandidate.StartsWith(fullSource, StringComparison.OrdinalIgnoreCase))
                    return null;

                if (!Directory.Exists(fullCandidate))
                    return null;

                var dirInfo = new DirectoryInfo(fullCandidate);
                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null && !target.FullName.StartsWith(fullSource, StringComparison.OrdinalIgnoreCase))
                        return null;
                }

                return fullCandidate;
            }
            catch
            {
                return null;
            }
        }

        public static bool IsVersionAvailable(string sourceRoot, string? versionTag, GraphicsApi api, string? architecture)
        {
            if (!DxvkCompatibility.IsDxvkSupported(api))
                return false;

            string? arch = NormalizeArchitecture(architecture);
            if (arch == null)
                return false;

            string[] requiredDlls = DxvkCompatibility.GetRequiredDlls(api);
            if (requiredDlls == null || requiredDlls.Length == 0)
                return false;

            string? versionDir = GetVersionDirectory(sourceRoot, versionTag);
            if (versionDir == null)
                return false;

            try
            {
                string fullSource = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string archDir = Path.Combine(versionDir, arch);
                if (!Directory.Exists(archDir))
                    return false;

                foreach (var dll in requiredDlls)
                {
                    string dllPath = Path.Combine(archDir, dll);
                    if (!File.Exists(dllPath))
                        return false;

                    var fi = new FileInfo(dllPath);
                    if (fi.Length == 0)
                        return false;

                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        var target = fi.ResolveLinkTarget(returnFinalTarget: true);
                        if (target != null && !target.FullName.StartsWith(fullSource, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }

                    if (!Path.GetFullPath(dllPath).StartsWith(fullSource, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static IReadOnlyList<string> GetAvailableVersions(string sourceRoot, GraphicsApi api, string? architecture)
        {
            var results = new List<string>();

            if (!DxvkCompatibility.IsDxvkSupported(api))
                return results;

            string? arch = NormalizeArchitecture(architecture);
            if (arch == null)
                return results;

            string[] requiredDlls = DxvkCompatibility.GetRequiredDlls(api);
            if (requiredDlls == null || requiredDlls.Length == 0)
                return results;

            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
                return results;

            try
            {
                string fullSource = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var subDirs = Directory.GetDirectories(sourceRoot);

                foreach (var dir in subDirs)
                {
                    string tag = Path.GetFileName(dir);
                    if (!IsSafeReleaseDirectoryName(tag))
                        continue;

                    string fullDir = Path.GetFullPath(dir);
                    if (!fullDir.StartsWith(fullSource, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dirInfo = new DirectoryInfo(fullDir);
                    if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                        if (target != null && !target.FullName.StartsWith(fullSource, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    string archDir = Path.Combine(fullDir, arch);
                    if (!Directory.Exists(archDir))
                        continue;

                    bool allDllsPresent = true;
                    foreach (var dll in requiredDlls)
                    {
                        string dllPath = Path.Combine(archDir, dll);
                        if (!File.Exists(dllPath))
                        {
                            allDllsPresent = false;
                            break;
                        }

                        var fi = new FileInfo(dllPath);
                        if (fi.Length == 0)
                        {
                            allDllsPresent = false;
                            break;
                        }

                        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            var target = fi.ResolveLinkTarget(returnFinalTarget: true);
                            if (target != null && !target.FullName.StartsWith(fullSource, StringComparison.OrdinalIgnoreCase))
                            {
                                allDllsPresent = false;
                                break;
                            }
                        }

                        if (!Path.GetFullPath(dllPath).StartsWith(fullSource, StringComparison.OrdinalIgnoreCase))
                        {
                            allDllsPresent = false;
                            break;
                        }
                    }

                    if (allDllsPresent)
                    {
                        results.Add(tag);
                    }
                }
            }
            catch
            {
                return results;
            }

            // Deterministic descending sort
            return results.OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
