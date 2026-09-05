using System;

namespace DXVKCompanion.Models
{
    public sealed class ManagementPolicy
    {
        public ManagementMode Mode { get; set; } = ManagementMode.UseGlobal;
        public string? PinnedDxvkVersion { get; set; }

        public static ManagementPolicy UseGlobal() => new();
        public static ManagementPolicy Automatic() => new() { Mode = ManagementMode.Automatic };
        public static ManagementPolicy Disabled() => new() { Mode = ManagementMode.Disabled };

        public static ManagementPolicy PinVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                throw new ArgumentException("A DXVK version is required.", nameof(version));
            return new ManagementPolicy
            {
                Mode = ManagementMode.PinnedVersion,
                PinnedDxvkVersion = version.Trim()
            };
        }
    }
}
