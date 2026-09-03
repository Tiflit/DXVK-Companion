using System.Diagnostics;
using DXVKCompanion.Utils;

namespace DXVKCompanion.Monitoring
{
    public enum GraphicsApi
    {
        Unknown,
        DX9,
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
            // First try loaded modules
            if (_scanner.UsesDirectX(process))
                return GraphicsApi.DX11;

            // Fallback to PE imports
            var imports = _parser.GetImports(process.MainModule.FileName);

            if (imports.Contains("d3d9.dll"))
                return GraphicsApi.DX9;

            if (imports.Contains("d3d11.dll"))
                return GraphicsApi.DX11;

            if (imports.Contains("d3d12.dll") || imports.Contains("vulkan-1.dll"))
                return GraphicsApi.ModernAPI;

            return GraphicsApi.Unknown;
        }
    }
}
