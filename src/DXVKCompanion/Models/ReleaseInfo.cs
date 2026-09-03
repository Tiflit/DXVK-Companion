using System.Collections.Generic;
using System.IO;

namespace DXVKCompanion.Models
{
    public class ReleaseInfo
    {
        public string Version { get; set; }

        // Maps: "x32" → { dllName → byte[] }, "x64" → { dllName → byte[] }
        public Dictionary<string, Dictionary<string, byte[]>> Dlls { get; set; } =
            new Dictionary<string, Dictionary<string, byte[]>>();

        public ReleaseInfo(string version)
        {
            Version = version;
            Dlls["x32"] = new();
            Dlls["x64"] = new();
        }

        public void AddDllFromTar(string tarPath, Stream data)
        {
            string arch = tarPath.Contains("/x32/") ? "x32" : "x64";
            string dllName = Path.GetFileName(tarPath);

            using var ms = new MemoryStream();
            data.CopyTo(ms);

            Dlls[arch][dllName] = ms.ToArray();
        }

        public string GetDllDirectory(string arch)
        {
            // In-memory extraction: we don't use directories
            return arch;
        }

        public IEnumerable<string> GetDllList(GraphicsApi api)
        {
            if (api == GraphicsApi.DX11)
                return new[] { "d3d11.dll", "dxgi.dll" };

            if (api == GraphicsApi.DX9)
                return new[] { "d3d9.dll" };

            return new string[0];
        }

        public byte[]? GetDllBytes(string arch, string dll)
        {
            if (Dlls.TryGetValue(arch, out var dict))
                if (dict.TryGetValue(dll, out var bytes))
                    return bytes;

            return null;
        }
    }
}
