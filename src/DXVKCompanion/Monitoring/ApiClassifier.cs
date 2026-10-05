using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DXVKCompanion.Models;
using DXVKCompanion.Utils;

namespace DXVKCompanion.Monitoring
{
    /// <summary>
    /// Classifies the graphics API used by an executable process using runtime loaded modules
    /// and static PE import headers.
    /// <para>
    /// <b>Mixed-Module Precedence Policy:</b> When both modern native APIs (DX12, Vulkan) and
    /// translatable APIs (D3D11, D3D10, D3D9) are detected within the same module set,
    /// modern unsupported APIs unconditionally take precedence as <see cref="ApiClassificationResult.PrimaryApi"/>.
    /// </para>
    /// <para>
    /// <b>False-Negative Trade-off:</b> This conservative precedence intentionally accepts potential
    /// false negatives (e.g., hybrid engines or launchers that might have run on Direct3D 11) in order
    /// to avoid catastrophic false positives (deploying DXVK DLLs into native DX12 or Vulkan engines,
    /// causing crashes, anti-cheat bans, or rendering corruption).
    /// </para>
    /// <para>
    /// <b>Detection Limitations:</b> Classification reflects observed modules at the time of detection;
    /// late-loading modules or dynamic runtime API selection by the game executable cannot be detected
    /// without continuous module polling or late injection hooks, which are out of scope.
    /// </para>
    /// </summary>
    public class ApiClassifier
    {
        private readonly ModuleScanner _scanner;
        private readonly PeParser _parser;

        public ApiClassifier(ModuleScanner scanner, PeParser parser)
        {
            _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        }

        /// <summary>
        /// Performs detailed API classification of a running process, inspecting runtime loaded graphics
        /// modules followed by static PE import inspection if runtime modules are inconclusive.
        /// Evaluates mixed modules according to unsupported API precedence (DX12 -> Vulkan -> DX11 -> DX10 -> DX9).
        /// </summary>
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

                if (runtimeModules.Contains("d3d12.dll"))
                {
                    observed.Add(GraphicsApi.DX12);
                    evidence.Add("Loaded runtime module: d3d12.dll");
                }

                if (runtimeModules.Contains("vulkan-1.dll"))
                {
                    observed.Add(GraphicsApi.Vulkan);
                    evidence.Add("Loaded runtime module: vulkan-1.dll");
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

                    if (imports.Contains("d3d12.dll", StringComparer.OrdinalIgnoreCase))
                    {
                        observed.Add(GraphicsApi.DX12);
                        evidence.Add("Static PE import: d3d12.dll");
                    }

                    if (imports.Contains("vulkan-1.dll", StringComparer.OrdinalIgnoreCase))
                    {
                        observed.Add(GraphicsApi.Vulkan);
                        evidence.Add("Static PE import: vulkan-1.dll");
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
