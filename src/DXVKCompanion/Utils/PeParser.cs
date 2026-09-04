using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DXVKCompanion.Utils
{
    public class PeParser
    {
        public IEnumerable<string> GetImports(string path)
        {
            var imports = new List<string>();

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs);

                // DOS header
                fs.Seek(0x3C, SeekOrigin.Begin);
                int peOffset = br.ReadInt32();

                // PE signature
                fs.Seek(peOffset, SeekOrigin.Begin);
                uint signature = br.ReadUInt32();
                if (signature != 0x4550) // "PE\0\0"
                    return imports;

                // COFF header
                fs.Seek(20, SeekOrigin.Current);

                // Optional header magic
                ushort magic = br.ReadUInt16();
                bool isPE32Plus = magic == 0x20b;

                // Skip to Data Directory
                fs.Seek(isPE32Plus ? 108 : 92, SeekOrigin.Current);

                // Import Directory RVA + size
                uint importRva = br.ReadUInt32();
                uint importSize = br.ReadUInt32();

                if (importRva == 0)
                    return imports;

                // Read section headers to find the import table offset
                fs.Seek(peOffset + 4 + 20, SeekOrigin.Begin);
                ushort numSections = br.ReadUInt16();
                fs.Seek(14, SeekOrigin.Current);

                uint importOffset = 0;

                for (int i = 0; i < numSections; i++)
                {
                    byte[] nameBytes = br.ReadBytes(8);
                    uint virtualSize = br.ReadUInt32();
                    uint virtualAddress = br.ReadUInt32();
                    uint sizeOfRawData = br.ReadUInt32();
                    uint pointerToRawData = br.ReadUInt32();

                    fs.Seek(16, SeekOrigin.Current);

                    if (importRva >= virtualAddress &&
                        importRva < virtualAddress + virtualSize)
                    {
                        importOffset = pointerToRawData + (importRva - virtualAddress);
                        break;
                    }
                }

                if (importOffset == 0)
                    return imports;

                fs.Seek(importOffset, SeekOrigin.Begin);

                while (true)
                {
                    uint originalFirstThunk = br.ReadUInt32();
                    uint timeDateStamp = br.ReadUInt32();
                    uint forwarderChain = br.ReadUInt32();
                    uint nameRva = br.ReadUInt32();
                    uint firstThunk = br.ReadUInt32();

                    if (originalFirstThunk == 0)
                        break;

                    long nameOffset = RvaToOffset(nameRva, fs, br, peOffset);
                    if (nameOffset == 0)
                        continue;

                    fs.Seek(nameOffset, SeekOrigin.Begin);

                    var dllNameBytes = new List<byte>();
                    byte b;
                    while ((b = br.ReadByte()) != 0)
                        dllNameBytes.Add(b);

                    string dllName = System.Text.Encoding.ASCII.GetString(dllNameBytes.ToArray());
                    imports.Add(dllName.ToLowerInvariant());
                }
            }
            catch
            {
                // Safe fallback: return empty list
            }

            return imports;
        }

        private long RvaToOffset(uint rva, FileStream fs, BinaryReader br, int peOffset)
        {
            try
            {
                fs.Seek(peOffset + 4 + 20, SeekOrigin.Begin);
                ushort numSections = br.ReadUInt16();
                fs.Seek(14, SeekOrigin.Current);

                for (int i = 0; i < numSections; i++)
                {
                    byte[] nameBytes = br.ReadBytes(8);
                    uint virtualSize = br.ReadUInt32();
                    uint virtualAddress = br.ReadUInt32();
                    uint sizeOfRawData = br.ReadUInt32();
                    uint pointerToRawData = br.ReadUInt32();

                    fs.Seek(16, SeekOrigin.Current);

                    if (rva >= virtualAddress &&
                        rva < virtualAddress + virtualSize)
                    {
                        return pointerToRawData + (rva - virtualAddress);
                    }
                }
            }
            catch { }

            return 0;
        }

        public string GetArchitecture(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs);

                fs.Seek(0x3C, SeekOrigin.Begin);
                int peOffset = br.ReadInt32();

                fs.Seek(peOffset + 4, SeekOrigin.Begin);
                ushort machine = br.ReadUInt16();

                return machine switch
                {
                    0x014C => "x32",
                    0x8664 => "x64",
                    _ => "Unknown"
                };
            }
            catch
            {
                return "Unknown";
            }
        }
    }
}
