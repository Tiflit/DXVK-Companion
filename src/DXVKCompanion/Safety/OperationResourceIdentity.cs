using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DXVKCompanion.Safety
{
    public static class OperationResourceIdentity
    {
        public static string ResolveCanonicalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path cannot be null or whitespace.", nameof(path));
            }

            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (fullPath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith("//", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException($"Network shares and UNC paths are not supported for operation exclusion: '{path}'");
            }

            try
            {
                string? root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrEmpty(root))
                {
                    return fullPath;
                }

                string relative = fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string currentResolved = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!string.IsNullOrEmpty(relative))
                {
                    string[] segments = relative.Split(
                        new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                        StringSplitOptions.RemoveEmptyEntries);

                    foreach (var seg in segments)
                    {
                        currentResolved = Path.Combine(currentResolved, seg);
                        if (Directory.Exists(currentResolved))
                        {
                            var dirInfo = new DirectoryInfo(currentResolved);
                            var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                            if (target != null)
                            {
                                currentResolved = target.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            }
                        }
                        else if (File.Exists(currentResolved))
                        {
                            var fileInfo = new FileInfo(currentResolved);
                            var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                            if (target != null)
                            {
                                currentResolved = target.FullName;
                            }
                        }
                    }
                }

                fullPath = currentResolved;
            }
            catch (Exception ex) when (ex is not NotSupportedException)
            {
                throw new NotSupportedException($"Installation path utilizes an unsupported directory alias or junction that cannot be verified: '{path}'", ex);
            }

            return fullPath;
        }

        public static string ComputeSha256(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // Normalize case invariants so Windows letter casing differences produce identical hashes
            string normalized = text.ToUpperInvariant();
            byte[] bytes = Encoding.UTF8.GetBytes(normalized);
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string GetTargetLockFileName(string targetInstallationRoot)
        {
            string canonical = ResolveCanonicalPath(targetInstallationRoot);
            return $"target_{ComputeSha256(canonical)}.lock";
        }

        public static string GetStoreLockFileName(string storeFilePath)
        {
            string canonical = ResolveCanonicalPath(storeFilePath);
            return $"store_{ComputeSha256(canonical)}.lock";
        }

        public static string GetDefaultCoordinationDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DXVKCompanion",
                "locks");
        }

        public static bool PathEquals(string pathA, string pathB)
        {
            if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
            {
                return false;
            }

            try
            {
                string canA = ResolveCanonicalPath(pathA);
                string canB = ResolveCanonicalPath(pathB);
                return string.Equals(canA, canB, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsPathInside(string childPath, string parentDirectory)
        {
            if (string.IsNullOrWhiteSpace(childPath) || string.IsNullOrWhiteSpace(parentDirectory))
            {
                return false;
            }

            try
            {
                string canChild = ResolveCanonicalPath(childPath);
                string canParent = ResolveCanonicalPath(parentDirectory);

                if (string.Equals(canChild, canParent, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string parentWithSlash = canParent.EndsWith(Path.DirectorySeparatorChar)
                    ? canParent
                    : canParent + Path.DirectorySeparatorChar;

                return canChild.StartsWith(parentWithSlash, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
