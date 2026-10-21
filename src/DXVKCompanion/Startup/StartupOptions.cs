using System;

namespace DXVKCompanion.Startup;

internal sealed class StartupOptions
{
    internal const string Usage = "Usage: DXVK-Companion.exe [--no-auto-discovery]";
    internal bool AutomaticDiscoveryEnabled { get; }

    private StartupOptions(bool automaticDiscoveryEnabled)
        => AutomaticDiscoveryEnabled = automaticDiscoveryEnabled;

    internal static bool TryParse(string[] args, out StartupOptions? options)
    {
        options = null;
        if (args.Length == 0)
            options = new StartupOptions(true);
        else if (args.Length == 1 && string.Equals(args[0], "--no-auto-discovery", StringComparison.Ordinal))
            options = new StartupOptions(false);
        return options != null;
    }
}
