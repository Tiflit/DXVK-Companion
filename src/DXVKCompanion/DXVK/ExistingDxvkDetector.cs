using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;

namespace DXVKCompanion.DXVK
{
    public class ExistingDxvkDetector
    {
        private static readonly string[] RelevantDlls = { "d3d9.dll", "d3d11.dll", "dxgi.dll" };
        private readonly string _dxvkSourceDir;

        public ExistingDxvkDetector(string? dxvkSourceDir = null)
        {
            _dxvkSourceDir = dxvkSourceDir ?? Paths.DxvkDir;
        }

        public virtual ExistingDxvkAssessment AssessDirectory(string gameDir, string? architecture = null)
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            {
                return new ExistingDxvkAssessment { Status = ExistingDxvkStatus.None };
            }

            var detectedDlls = new List<string>();
            var evidence = new List<string>();
            bool anyDxvkSignature = false;
            string? matchedVersion = null;
            bool matchedAllToOfficial = true;

            foreach (var dll in RelevantDlls)
            {
                var fullPath = Path.Combine(gameDir, dll);
                if (!File.Exists(fullPath)) continue;

                detectedDlls.Add(dll);
                var identity = FileIdentity.Capture(fullPath);

                bool isDxvkMetadata = CheckDxvkMetadata(fullPath);
                if (isDxvkMetadata)
                {
                    anyDxvkSignature = true;
                    evidence.Add($"{dll}: PE metadata indicates DXVK");
                }

                // Check hash against known local release archives
                string? version = FindMatchingOfficialVersion(dll, identity.Sha256, architecture);
                if (version != null)
                {
                    evidence.Add($"{dll}: matches official release {version} (SHA-256: {identity.Sha256[..8]}...)");
                    if (matchedVersion == null)
                    {
                        matchedVersion = version;
                    }
                    else if (!string.Equals(matchedVersion, version, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedAllToOfficial = false; // Conflicting versions
                    }
                }
                else
                {
                    if (isDxvkMetadata)
                    {
                        matchedAllToOfficial = false; // DXVK, but unknown build/hash
                        evidence.Add($"{dll}: DXVK signature present, but hash does not match an official release");
                    }
                    else
                    {
                        evidence.Add($"{dll}: standard native or third-party DLL");
                    }
                }
            }

            if (detectedDlls.Count == 0)
            {
                return new ExistingDxvkAssessment { Status = ExistingDxvkStatus.None };
            }

            if (matchedVersion != null && matchedAllToOfficial)
            {
                return new ExistingDxvkAssessment
                {
                    Status = ExistingDxvkStatus.OfficialRelease,
                    MatchedVersion = matchedVersion,
                    DetectedDlls = detectedDlls,
                    Evidence = evidence
                };
            }

            if (anyDxvkSignature)
            {
                return new ExistingDxvkAssessment
                {
                    Status = ExistingDxvkStatus.RecognizedUnknownVersion,
                    DetectedDlls = detectedDlls,
                    Evidence = evidence
                };
            }

            return new ExistingDxvkAssessment
            {
                Status = ExistingDxvkStatus.NativeOrNonDxvk,
                DetectedDlls = detectedDlls,
                Evidence = evidence
            };
        }

        private static bool CheckDxvkMetadata(string filePath)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(filePath);
                if (string.Equals(info.ProductName, "DXVK", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (info.FileDescription != null && info.FileDescription.Contains("DXVK", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (info.Comments != null && info.Comments.Contains("dxvk", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // Unreadable version resource
            }

            return false;
        }

        private string? FindMatchingOfficialVersion(string dllName, string targetSha256, string? architecture)
        {
            var catalogVersion = DxvkReleaseCatalog.FindVersionBySha256(dllName, targetSha256, architecture);
            if (catalogVersion != null)
                return catalogVersion;

            if (!Directory.Exists(_dxvkSourceDir)) return null;

            try
            {
                foreach (var versionDir in Directory.EnumerateDirectories(_dxvkSourceDir))
                {
                    string versionName = Path.GetFileName(versionDir);

                    var archsToCheck = !string.IsNullOrEmpty(architecture)
                        ? new[] { architecture }
                        : new[] { "x64", "x32" };

                    foreach (var arch in archsToCheck)
                    {
                        var candidate = Path.Combine(versionDir, arch, dllName);
                        if (File.Exists(candidate))
                        {
                            var candidateIdentity = FileIdentity.Capture(candidate);
                            if (string.Equals(candidateIdentity.Sha256, targetSha256, StringComparison.OrdinalIgnoreCase))
                            {
                                return versionName;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Directory enumeration error
            }

            return null;
        }
    }
}
