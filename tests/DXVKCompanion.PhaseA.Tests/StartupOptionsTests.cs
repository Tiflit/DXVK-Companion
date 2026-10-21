using DXVKCompanion.Startup;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class StartupOptionsTests
{
    [Fact]
    public void NoArguments_PreservesAutomaticDiscovery()
    {
        Assert.True(StartupOptions.TryParse(Array.Empty<string>(), out var options));
        Assert.NotNull(options);
        Assert.True(options.AutomaticDiscoveryEnabled);
    }

    [Fact]
    public void ExplicitSwitch_IsProcessLocalAndDoesNotChangeNextDefault()
    {
        Assert.True(StartupOptions.TryParse(new[] { "--no-auto-discovery" }, out var paused));
        Assert.NotNull(paused);
        Assert.False(paused.AutomaticDiscoveryEnabled);
        Assert.True(StartupOptions.TryParse(Array.Empty<string>(), out var next));
        Assert.NotNull(next);
        Assert.True(next.AutomaticDiscoveryEnabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--unknown")]
    [InlineData("--no-auto-discovery=true")]
    [InlineData("--NO-AUTO-DISCOVERY")]
    [InlineData(" --no-auto-discovery")]
    [InlineData("--no-auto-discover")]
    public void UnsupportedArgument_RefusesWithoutDefaultFallback(string argument)
    {
        Assert.False(StartupOptions.TryParse(new[] { argument }, out var options));
        Assert.Null(options);
    }

    [Fact]
    public void DuplicateOrExtraArguments_RefuseWithoutDefaultFallback()
    {
        Assert.False(StartupOptions.TryParse(new[] { "--no-auto-discovery", "--no-auto-discovery" }, out var duplicate));
        Assert.Null(duplicate);
        Assert.False(StartupOptions.TryParse(new[] { "--no-auto-discovery", "extra" }, out var extra));
        Assert.Null(extra);
    }
}
