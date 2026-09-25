using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DXVKCompanion.Models;
using DXVKCompanion.Utils;

namespace DXVKCompanion.Monitoring
{
    public class ApiClassifier
    {
        private readonly ModuleScanner _scanner;
        private readonly PeParser _parser;

        public ApiClassifier(ModuleScanner scanner, PeParser parser)
        {
            _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        }

        public ApiClassificationResult ClassifyDetailed(Process process, string? exePath = null)
        {
            ArgumentNullException.ThrowIfNull(process);

            string? resolvedExePath = exePath;
            if (string.IsNullOrEmpty(resolvedExePath))
            {
                try { resolvedExePath = process.MainModule?.FileName; }
                catch { }
            }

            string architecture = "Unknown";
            if (!string.IsNullOrEmpty(resolvedExePath) && File.Exists(resolvedExePath))
            {
                architecture = _parser.GetArchitecture(resolvedExePath);
            }
            if (architecture == "Unknown")
            {
                architecture = Environment.Is64BitOperatingSystem && !Is32BitProcess(process) ? "x64" : "x32";
            }

            var runtimeModules = _scanner.GetLoadedGraphicsModules(process);
            if (runtimeModules.Count > 0)
            {
                var observed = new List<GraphicsApi>();
                var evidence = new List<string>();

                if (runtimeModules.Contains("d3d12.dll") || runtimeModules.Contains("vulkan-1.dll"))
                {
                    observed.Add(GraphicsApi.ModernAPI);
                    if (runtimeModules.Contains("d3d12.dll")) evidence.Add("Loaded runtime module: d3d12.dll");
                    if (runtimeModules.Contains("vulkan-1.dll")) evidence.Add("Loaded runtime module: vulkan-1.dll");
                }

                if (runtimeModules.Contains("d3d11.dll"))
                {
                    observed.Add(GraphicsApi.DX11);
                    evidence.Add("Loaded runtime module: d3d11.dll");
                }

                if (runtimeModules.Contains("d3d10.dll") || runtimeModules.Contains("d3d10core.dll"))
                {
                    observed.Add(GraphicsApi.DX10);
                    evidence.Add("Loaded runtime module: d3d10.dll");
                }

                if (runtimeModules.Contains("d3d9.dll"))
                {
                    observed.Add(GraphicsApi.DX9);
                    evidence.Add("Loaded runtime module: d3d9.dll");
                }

                if (observed.Count > 0)
                {
                    return new ApiClassificationResult
                    {
                        PrimaryApi = observed[0],
                        ObservedApis = observed,
                        Confidence = ApiDetectionConfidence.High,
                        Architecture = architecture,
                        Evidence = evidence,
                        EvidenceSource = "RuntimeModules"
                    };
                }
            }

            // Fallback to static PE import inspection
            if (!string.IsNullOrEmpty(resolvedExePath) && File.Exists(resolvedExePath))
            {
                try
                {
                    var imports = _parser.GetImports(resolvedExePath).ToList();
                    var observed = new List<GraphicsApi>();
                    var evidence = new List<string>();

                    if (imports.Contains("d3d12.dll", StringComparer.OrdinalIgnoreCase) ||
                        imports.Contains("vulkan-1.dll", StringComparer.OrdinalIgnoreCase))
                    {
                        observed.Add(GraphicsApi.ModernAPI);
                        evidence.Add("Static PE import: Direct3D 12 / Vulkan");
                    }

                    if (imports.Contains("d3d11.dll", StringComparer.OrdinalIgnoreCase))
                    {
                        observed.Add(GraphicsApi.DX11);
                        evidence.Add("Static PE import: d3d11.dll");
                    }

                    if (imports.Contains("d3d10.dll", StringComparer.OrdinalIgnoreCase) ||
                        imports.Contains("d3d10core.dll", StringComparer.OrdinalIgnoreCase))
                    {
                        observed.Add(GraphicsApi.DX10);
                        evidence.Add("Static PE import: d3d10.dll");
                    }

                    if (imports.Contains("d3d9.dll", StringComparer.OrdinalIgnoreCase))
                    {
                        observed.Add(GraphicsApi.DX9);
                        evidence.Add("Static PE import: d3d9.dll");
                    }

                    if (observed.Count > 0)
                    {
                        return new ApiClassificationResult
                        {
                            PrimaryApi = observed[0],
                            ObservedApis = observed,
                            Confidence = ApiDetectionConfidence.Medium,
                            Architecture = architecture,
                            Evidence = evidence,
                            EvidenceSource = "StaticPEImports"
                        };
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"ApiClassifier: PE import inspection failed for {resolvedExePath}: {ex.Message}");
                }
            }

            return new ApiClassificationResult
            {
                PrimaryApi = GraphicsApi.Unknown,
                ObservedApis = Array.Empty<GraphicsApi>(),
                Confidence = ApiDetectionConfidence.Unknown,
                Architecture = architecture,
                Evidence = Array.Empty<string>(),
                EvidenceSource = "None"
            };
        }

        public GraphicsApi Classify(Process process)
        {
            return ClassifyDetailed(process).PrimaryApi;
        }

        private static bool Is32BitProcess(Process process)
        {
            try
            {
                if (!Environment.Is64BitOperatingSystem) return true;
                // If 64-bit OS and we cannot determine, default to 64-bit
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
