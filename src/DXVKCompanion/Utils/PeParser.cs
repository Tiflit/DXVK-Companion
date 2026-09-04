using System;
using System.Collections.Generic;
using System.IO;

namespace DXVKCompanion.Utils
{
    public class PeParser
    {
        public IEnumerable<string> GetImports(string path)
        {
            var imports = new List<string>();

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var br = new BinaryReader(fs);

                // DOS header: e_lfanew at 0x3C points to the start of the PE header.
                fs.Seek(0x3C, SeekOrigin.Begin);
                int peOffset = br.ReadInt32();

                fs.Seek(peOffset, SeekOrigin.Begin);
                uint signature = br.ReadUInt32();
                if (signature != 0x00004550) // "PE\0\0"
                    return imports;

                // COFF header: Machine(2), NumberOfSections(2), TimeDateStamp(4),
                // PointerToSymbolTable(4), NumberOfSymbols(4), SizeOfOptionalHeader(2), Characteristics(2)
                fs.Seek(peOffset + 4, SeekOrigin.Begin);
                ushort machine = br.ReadUInt16();
                ushort numSections = br.ReadUInt16();
                fs.Seek(12, SeekOrigin.Current);
                ushort sizeOfOptionalHeader = br.ReadUInt16();
                fs.Seek(2, SeekOrigin.Current);

                long optionalHeaderStart = fs.Position;
                ushort magic = br.ReadUInt16();
                bool isPE32Plus = magic == 0x20b;

                // Data Directory index 1 is the Import Table (index 0 is Export). Each entry is 8 bytes.
                int dataDirectoryOffset = isPE32Plus ? 112 : 96;
                fs.Seek(optionalHeaderStart + dataDirectoryOffset + (1 * 8), SeekOrigin.Begin);
                uint importRva = br.ReadUInt32();
                uint importSize = br.ReadUInt32();

                if (importRva == 0)
                    return imports;

                long sectionTableStart = optionalHeaderStart + sizeOfOptionalHeader;
                var sections = new List<(uint VirtualAddress, uint VirtualSize, uint RawAddress)>();
                fs.Seek(sectionTableStart, SeekOrigin.Begin);

                for (int i = 0; i < numSections; i++)
                {
                    fs.Seek(8, SeekOrigin.Current); // section name
                    uint virtualSize = br.ReadUInt32();
                    uint virtualAddress = br.ReadUInt32();
                    uint sizeOfRawData = br.ReadUInt32();
                    uint pointerToRawData = br.ReadUInt32();
                    fs.Seek(16, SeekOrigin.Current);
                    sections.Add((virtualAddress, Math.Max(virtualSize, sizeOfRawData), pointerToRawData));
                }

                long RvaToOffset(uint rva)
                {
                    foreach (var s in sections)
                    {
                        if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, 1))
                            return s.RawAddress + (rva - s.VirtualAddress);
                    }
                    return -1;
                }

                long importTableOffset = RvaToOffset(importRva);
                if (importTableOffset < 0)
                    return imports;

                fs.Seek(importTableOffset, SeekOrigin.Begin);

                while (true)
                {
                    uint originalFirstThunk = br.ReadUInt32();
                    uint timeDateStamp = br.ReadUInt32();
                    uint forwarderChain = br.ReadUInt32();
                    uint nameRva = br.ReadUInt32();
                    uint firstThunk = br.ReadUInt32();

                    if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0)
                        break;

                    long nameOffset = RvaToOffset(nameRva);
                    if (nameOffset < 0)
                        continue;

                    long savedPos = fs.Position;
                    fs.Seek(nameOffset, SeekOrigin.Begin);

                    var dllNameBytes = new List<byte>();
                    byte b;
                    while ((b = br.ReadByte()) != 0)
                        dllNameBytes.Add(b);

                    imports.Add(System.Text.Encoding.ASCII.GetString(dllNameBytes.ToArray()).ToLowerInvariant());

                    fs.Seek(savedPos, SeekOrigin.Begin);
                }
            }
            catch
            {
                // Locked, inaccessible, or malformed file — return whatever was read (often empty).
            }

            return imports;
        }

        public string GetArchitecture(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
