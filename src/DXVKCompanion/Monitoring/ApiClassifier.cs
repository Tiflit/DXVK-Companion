using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DXVKCompanion.Models;
using DXVKCompanion.Utils;

namespace DXVKCompanion.Monitoring
{
    public class ApiClassifier
    {
        private readonly ModuleScanner _scanner;
        private readonly PeParser _parser;

        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process([In] IntPtr hProcess, [Out] out bool lpSystemInfo);

        public ApiClassifier(ModuleScanner scanner, PeParser parser)
        {
            _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        }

        public virtual bool? Is32BitProcess(Process process)
        {
            try
            {
                if (!Environment.Is64BitOperatingSystem) return true;
                if (process.HasExited) return null;
                if (OperatingSystem.IsWindows() && IsWow64Process(process.Handle, out bool isWow64))
                {
                    return isWow64;
                }
                return null;
            }
            catch
            {
                return null;
            }
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
                bool? is32 = Is32BitProcess(process);
                if (is32.HasValue)
                {
                    architecture = is32.Value ? "x32" : "x64";
                }
            }

            var runtimeModules = _scanner.GetLoadedGraphicsModules(process);
            if (runtimeModules.Count > 0)
            {
                var observed = new List<GraphicsApi>();
                var evidence = new List<string>();

                if (runtimeModules.Contains("d3d12.dll"))
                {
                    observed.Add(GraphicsApi.D3D12);
                    evidence.Add("Loaded runtime module: d3d12.dll");
                }

                if (runtimeModules.Contains("vulkan-1.dll"))
                {
                    observed.Add(GraphicsApi.Vulkan);
                    evidence.Add("Loaded runtime module: vulkan-1.dll");
                }

                if (runtimeModules.Contains("d3d10.dll") || runtimeModules.Contains("d3d10core.dll") || runtimeModules.Contains("d3d10_1.dll"))
                {
                    observed.Add(GraphicsApi.D3D10);
                    evidence.Add("Loaded runtime module: d3d10core / d3d10.dll");
                }

                if (runtimeModules.Contains("d3d11.dll"))
                {
                    observed.Add(GraphicsApi.D3D11);
                    evidence.Add("Loaded runtime module: d3d11.dll");
                }

                if (runtimeModules.Contains("d3d8.dll"))
                {
                    observed.Add(GraphicsApi.D3D8);
                    evidence.Add("Loaded runtime module: d3d8.dll");
                }

                if (runtimeModules.Contains("d3d9.dll"))
                {
                    observed.Add(GraphicsApi.D3D9);
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
                        observed.Add(GraphicsApi.D3D12);
                        evidence.Add("Static PE import: d3d12.dll");
                    }

                    if (imports.Contains("vulkan-1.dll", StringComparer.OrdinalIgnoreCase))
                    {
                        observed.Add(GraphicsApi.Vulkan);
                        evidence.Add("Static PE import: vulkan-1.dll");
                    }

                    if (imports.Contains("d3d10.dll", StringComparer.OrdinalIgnoreCase) ||
                        imports.Contains("d3d10core.dll", StringComparer.OrdinalIgnoreCase) ||
                        imports.Contains("d3d10_1.dll", StringComparer.OrdinalIgnoreCase))
                    {
                        observed.Add(GraphicsApi.D3D10);
                        evidence.Add("Static PE import: d3d10.dll / d3d10core.dll");
                    }

                    if (imports.Contains("d3d11.dll", StringComparer.OrdinalIgnoreCase))
                    {
                        observed.Add(GraphicsApi.D3D11);
                        evidence.Add("Static PE import: d3d11.dll");
                    }

                    if (imports.Contains("d3d8.dll", StringComparer.OrdinalIgnoreCase))
                    {
                        observed.Add(GraphicsApi.D3D8);
                        evidence.Add("Static PE import: d3d8.dll");
                    }

                    if (imports.Contains("d3d9.dll", StringComparer.OrdinalIgnoreCase))
                    {
                        observed.Add(GraphicsApi.D3D9);
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
    }
}
