using System;
using System.Collections.Generic;
using System.IO;

namespace DXVKCompanion.Utils
{
    public class PeParser
    {
        public List<string> GetImports(string exePath)
        {
            var imports = new List<string>();

            try
            {
                using var stream = File.OpenRead(exePath);
                using var reader = new BinaryReader(stream);

                if (reader.ReadUInt16() != 0x5A4D) // 'MZ'
                    return imports;

                // This is a stub: we just return known names for detection purposes.
                imports.Add("d3d9.dll");
                imports.Add("d3d11.dll");
                imports.Add("d3d12.dll");
                imports.Add("dxgi.dll");
                imports.Add("vulkan-1.dll");
            }
            catch
            {
                // Ignore errors, return empty list
            }

            return imports;
        }

        public string GetArchitecture(string exePath)
        {
            try
            {
                using var stream = File.OpenRead(exePath);
                using var reader = new BinaryReader(stream);

                if (reader.ReadUInt16() != 0x5A4D) // 'MZ'
                    return "x64";

                stream.Position = 0x3C;
                int peOffset = reader.ReadInt32();

                stream.Position = peOffset + 4;
                ushort machine = reader.ReadUInt16();

                return machine switch
                {
                    0x014C => "x32", // 32-bit
                    0x8664 => "x64", // 64-bit
                    _ => "x64"
                };
            }
            catch
            {
                return "x64";
            }
        }
    }
}
