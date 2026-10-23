using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.PortableExecutable;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;

namespace DXVKCompanion.DXVK;

// Desired payload, observed files and persisted ownership are deliberately separate.
internal static class DxvkDeploymentPayload
{
    internal const string Core = "d3d10core.dll";

    internal static bool IncludesCore(GraphicsApi api, string? version, string? architecture) =>
        api == GraphicsApi.DX10 && version == "v3.1.1" &&
        DxvkLocalReleaseLibrary.NormalizeArchitecture(architecture) == "x64";

    internal static string[] Resolve(GraphicsApi api, string? version, string? architecture) =>
        IncludesCore(api, version, architecture)
            ? new[] { Core, "d3d11.dll", "dxgi.dll" }
            : DxvkCompatibility.GetRequiredDlls(api);

    // An exact acquisition record establishes the selected context. These are local
    // integrity checks, not a new signature/authentication claim for the manifest.
    internal static bool TryCaptureQualifiedSource(string directory, string version, string[] files,
        out Dictionary<string, SafetyFileIdentity> identities)
    {
        identities = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var manifest = FileIdentity.Capture(Path.Combine(directory, DxvkReleaseAcquirer.ManifestName));
            if (!DxvkReleaseAcquirer.ValidateManifest(directory, version, out _)) return false;
            foreach (string file in files)
            {
                string path = Path.Combine(directory, "x64", file);
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus ||
                    pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 ||
                    (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) == 0) return false;
                identities.Add(file, FileIdentity.Capture(path));
            }
            if (!DxvkReleaseAcquirer.ValidateManifest(directory, version, out _) ||
                manifest != FileIdentity.Capture(Path.Combine(directory, DxvkReleaseAcquirer.ManifestName))) return false;
            foreach (var pair in identities)
                if (pair.Value != FileIdentity.Capture(Path.Combine(directory, "x64", pair.Key))) return false;
            return true;
        }
        catch { return false; }
    }
}
