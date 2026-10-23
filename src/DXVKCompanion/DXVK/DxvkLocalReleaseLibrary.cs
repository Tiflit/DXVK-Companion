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

            if (name.StartsWith(".", StringComparison.Ordinal))
                return false; // Operation-owned acquisition staging is never selectable.

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

        internal static bool IsRecognizedSourceEligible(string root, string tag, GraphicsApi api, string? architecture, string[]? observedDlls = null)
        {
            try
            {
                if (!IsSafeReleaseDirectoryName(tag)) return false;
                string directory = Path.Combine(root, tag);
                if (DxvkDeploymentPayload.IncludesCore(api, tag, architecture))
                {
                    string[] observed = observedDlls ?? DxvkDeploymentPayload.Resolve(api, tag, architecture);
                    string? safeDirectory = GetVersionDirectory(root, tag);
                    return safeDirectory != null && ValidateReleasePayload(Path.GetFullPath(root), safeDirectory, "x64", observed) &&
                        DxvkDeploymentPayload.TryCaptureQualifiedSource(safeDirectory, tag, observed, out _);
                }
                return !DxvkReleaseAcquirer.HasAcquisitionRecord(directory) || IsVersionAvailable(root, tag, api, architecture);
            }
            catch { return false; }
        }

        private static bool IsPathWithinRoot(string path, string fullSourceRootWithSlash)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                string full = Path.GetFullPath(path);
                string root = fullSourceRootWithSlash.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullTrimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (string.Equals(fullTrimmed, root, StringComparison.OrdinalIgnoreCase))
                    return true;

                string rootWithSlash = root + Path.DirectorySeparatorChar;
                return full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static bool ValidateReleasePayload(string fullSource, string versionDir, string arch, string[] requiredDlls)
        {
            try
            {
                if (!Directory.Exists(versionDir))
                    return false;

                if (DxvkReleaseAcquirer.HasAcquisitionRecord(versionDir) &&
                    !DxvkReleaseAcquirer.ValidateManifest(versionDir, Path.GetFileName(versionDir), out _))
                    return false;

                var vDirInfo = new DirectoryInfo(versionDir);
                if (!IsPathWithinRoot(vDirInfo.FullName, fullSource))
                    return false;

                if ((vDirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var target = vDirInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (target == null || !IsPathWithinRoot(target.FullName, fullSource))
                        return false;
                }

                string archDir = Path.Combine(versionDir, arch);
                if (!Directory.Exists(archDir))
                    return false;

                var archDirInfo = new DirectoryInfo(archDir);
                if (!IsPathWithinRoot(archDirInfo.FullName, fullSource))
                    return false;

                if ((archDirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var target = archDirInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (target == null || !IsPathWithinRoot(target.FullName, fullSource))
                        return false;
                }

                foreach (var dll in requiredDlls)
                {
                    string dllPath = Path.Combine(archDir, dll);
                    if (!File.Exists(dllPath))
                        return false;

                    var fi = new FileInfo(dllPath);
                    if (fi.Length == 0)
                        return false;

                    if (!IsPathWithinRoot(fi.FullName, fullSource))
                        return false;

                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        var target = fi.ResolveLinkTarget(returnFinalTarget: true);
                        if (target == null || !IsPathWithinRoot(target.FullName, fullSource))
                            return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
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

                if (!IsPathWithinRoot(fullCandidate, fullSource))
                    return null;

                if (!Directory.Exists(fullCandidate))
                    return null;

                var dirInfo = new DirectoryInfo(fullCandidate);
                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (target == null || !IsPathWithinRoot(target.FullName, fullSource))
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

            string[] requiredDlls = DxvkDeploymentPayload.Resolve(api, versionTag, arch);
            if (requiredDlls == null || requiredDlls.Length == 0)
                return false;

            string? versionDir = GetVersionDirectory(sourceRoot, versionTag);
            if (versionDir == null)
                return false;

            try
            {
                string fullSource = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return ValidateReleasePayload(fullSource, versionDir, arch, requiredDlls) &&
                    (!DxvkDeploymentPayload.IncludesCore(api, versionTag, arch) ||
                     DxvkDeploymentPayload.TryCaptureQualifiedSource(versionDir, versionTag!, requiredDlls, out _));
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

                    if (!IsVersionAvailable(fullSource, tag, api, arch))
                        continue;

                    results.Add(tag);
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
