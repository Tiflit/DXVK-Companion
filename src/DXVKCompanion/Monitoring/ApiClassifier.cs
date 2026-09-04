using System.Diagnostics;
using DXVKCompanion.Utils;

namespace DXVKCompanion.Monitoring
{
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
            // 1. Check loaded modules first
            if (_scanner.UsesDx11(process))
                return GraphicsApi.DX11;

            if (_scanner.UsesDx9(process))
                return GraphicsApi.DX9;

            if (_scanner.UsesDx12(process))
                return GraphicsApi.ModernAPI;

            if (_scanner.UsesVulkan(process))
                return GraphicsApi.ModernAPI;

            // 2. Fallback to PE imports
            try
            {
                var imports = _parser.GetImports(process.MainModule?.FileName ?? string.Empty);

                foreach (var imp in imports)
                {
                    if (imp == "d3d11.dll" || imp == "dxgi.dll")
                        return GraphicsApi.DX11;

                    if (imp == "d3d9.dll")
                        return GraphicsApi.DX9;

                    if (imp == "d3d12.dll" || imp == "vulkan-1.dll")
                        return GraphicsApi.ModernAPI;
                }
            }
            catch { }

            return GraphicsApi.Unknown;
        }
    }
}
