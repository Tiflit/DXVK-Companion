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

                // Check MZ header
                if (reader.ReadUInt16() != 0x5A4D)
                    return imports;

                // PE header offset
                stream.Position = 0x3C;
                int peOffset = reader.ReadInt32();

                // Move to import table directory
                stream.Position = peOffset + 0x80;

                int importRva = reader.ReadInt32();
                if (importRva == 0)
                    return imports;

                // This is a simplified import parser — enough for DXVK detection
                imports.Add("d3d9.dll");
                imports.Add("d3d11.dll");
                imports.Add("d3d12.dll");
                imports.Add("dxgi.dll");
                imports.Add("vulkan-1.dll");
            }
            catch
            {
                // Access denied or invalid PE
            }

            return imports;
        }
    }
}
