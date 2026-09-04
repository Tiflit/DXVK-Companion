using System.Diagnostics;
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
            _scanner = scanner;
            _parser = parser;
        }

        public GraphicsApi Classify(Process process)
        {
            // Single scan instead of up to five separate ones.
            var modules = _scanner.GetLoadedGraphicsModules(process);

            if (modules.Contains("d3d9.dll")) return GraphicsApi.DX9;
            if (modules.Contains("d3d10.dll")) return GraphicsApi.DX10;
            if (modules.Contains("d3d11.dll")) return GraphicsApi.DX11;
            if (modules.Contains("d3d12.dll") || modules.Contains("vulkan-1.dll")) return GraphicsApi.ModernAPI;

            // Nothing matched via live modules (genuinely none loaded, or enumeration was
            // blocked) — fall back to static PE import parsing.
            try
            {
                var imports = _parser.GetImports(process.MainModule.FileName).ToList();

                if (imports.Contains("d3d9.dll")) return GraphicsApi.DX9;
                if (imports.Contains("d3d10.dll")) return GraphicsApi.DX10;
                if (imports.Contains("d3d11.dll")) return GraphicsApi.DX11;
                if (imports.Contains("d3d12.dll") || imports.Contains("vulkan-1.dll")) return GraphicsApi.ModernAPI;
            }
            catch
            {
                Logger.Log($"ApiClassifier: PE import fallback failed for {process.ProcessName}.");
            }

            return GraphicsApi.Unknown;
        }
    }
}
