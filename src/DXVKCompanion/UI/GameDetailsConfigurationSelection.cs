using System;
using DXVKCompanion.Models;

namespace DXVKCompanion.UI;

public readonly record struct InitialConfigurationDisplay(bool HudEnabled, int FrameLimit);

public static class GameDetailsConfigurationSelection
{
    public const int MinFrameLimit = 0;
    public const int MaxFrameLimit = 1000;

    public static InitialConfigurationDisplay GetInitialDisplay(
        GameInstallation? installation,
        bool isPreexistingInstallation,
        GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (isPreexistingInstallation && installation != null)
        {
            var config = installation.Configuration ??= new();
            bool hud = config.HudEnabled;
            int frameLimit = 0;
            if (config.FrameLimitEnabled)
            {
                frameLimit = Math.Clamp(config.FrameLimit, MinFrameLimit, MaxFrameLimit);
            }

            return new InitialConfigurationDisplay(hud, frameLimit);
        }

        bool profileHud = profile.HudEnabled;
        int profileLimit = Math.Clamp(profile.FrameLimit, MinFrameLimit, MaxFrameLimit);
        return new InitialConfigurationDisplay(profileHud, profileLimit);
    }

    public static void ApplyConfiguration(
        bool finalHud,
        int finalFrameLimit,
        InitialConfigurationDisplay baseline,
        GameInstallation? installation,
        bool isPreexistingInstallation,
        GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        bool hudChanged = finalHud != baseline.HudEnabled;
        bool frameLimitChanged = finalFrameLimit != baseline.FrameLimit;

        if (isPreexistingInstallation && installation != null)
        {
            var config = installation.Configuration ??= new();
            if (hudChanged)
            {
                config.HudEnabled = finalHud;
                profile.HudEnabled = finalHud;
            }

            if (frameLimitChanged)
            {
                config.FrameLimit = finalFrameLimit;
                config.FrameLimitEnabled = finalFrameLimit > 0;
                profile.FrameLimit = finalFrameLimit;
            }
        }
        else if (installation != null)
        {
            // Newly created installation during window opening
            var config = installation.Configuration ??= new();
            config.HudEnabled = finalHud;
            if (hudChanged)
            {
                profile.HudEnabled = finalHud;
            }

            if (frameLimitChanged)
            {
                config.FrameLimit = finalFrameLimit;
                config.FrameLimitEnabled = finalFrameLimit > 0;
                profile.FrameLimit = finalFrameLimit;
            }
            else
            {
                if (finalFrameLimit > 0)
                {
                    config.FrameLimit = finalFrameLimit;
                    config.FrameLimitEnabled = true;
                }
                else
                {
                    config.FrameLimitEnabled = false;
                }
            }
        }
        else
        {
            // Absent installation (installation == null)
            if (hudChanged)
            {
                profile.HudEnabled = finalHud;
            }

            if (frameLimitChanged)
            {
                profile.FrameLimit = finalFrameLimit;
            }
        }
    }
}
