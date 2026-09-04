using System.Diagnostics;
using DXVKCompanion.Utils;

namespace DXVKCompanion.Monitoring
{
    public enum GraphicsApi
    {
        Unknown,
        DX9,
        DX10,
        DX11,
        ModernAPI
    }

    public class ApiClassifier
    {
        private readonly ModuleScanner _scanner;
        private readonly PeParser _parser;

        public ApiClassifier(ModuleScanner scanner, PeParser parser)
        {
            _scanner = scanner;
            _parser = parser;
        }

        public GraphicsApi Classify(Process process)
        {
            // First try loaded modules (fast, runtime)
            // Check specific DirectX versions explicitly
            try
            {
                if (_scanner.UsesDx9(process))
                    return GraphicsApi.DX9;

                if (_scanner.UsesDx10(process))
                    return GraphicsApi.DX10;

                if (_scanner.UsesDx11(process))
                    return GraphicsApi.DX11;

                if (_scanner.UsesDx12(process) || _scanner.UsesVulkan(process))
                    return GraphicsApi.ModernAPI;
            }
            catch
            {
                // If module enumeration fails (access denied etc.) we'll fallback to PE import parsing below.
                // Caller should treat inability to inspect as potentially risky (handled at higher layers).
            }

            // Fallback to PE imports (static)
            try
            {
                var imports = _parser.GetImports(process.MainModule.FileName);

                if (imports.Contains("d3d9.dll"))
                    return GraphicsApi.DX9;

                if (imports.Contains("d3d10.dll"))
                    return GraphicsApi.DX10;

                if (imports.Contains("d3d11.dll"))
                    return GraphicsApi.DX11;

                if (imports.Contains("d3d12.dll") || imports.Contains("vulkan-1.dll"))
                    return GraphicsApi.ModernAPI;
            }
            catch
            {
                // Ignore parse failures; fall through to Unknown
            }

            return GraphicsApi.Unknown;
        }
    }
}
