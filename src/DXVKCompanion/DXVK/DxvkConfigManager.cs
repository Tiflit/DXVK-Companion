using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DXVKCompanion.Models;

namespace DXVKCompanion.DXVK
{
    public class DxvkConfigManager
    {
        public const string ConfigFileName = "dxvk.conf";

        public static bool RequiresConfigFile(DxvkConfiguration config)
        {
            if (config == null) return false;
            return config.HudEnabled || (config.FrameLimitEnabled && config.FrameLimit > 0);
        }

        public static string? GenerateConfigContent(DxvkConfiguration config, string? existingContent = null)
        {
            if (!RequiresConfigFile(config))
            {
                // Invariant (Section 36): If no Companion-managed configuration is needed, do not create dxvk.conf
                return null;
            }

            var lines = new List<string>();
            bool hudHandled = false;
            bool frameLimitHandled = false;

            if (!string.IsNullOrWhiteSpace(existingContent))
            {
                using var reader = new StringReader(existingContent);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("dxvk.hud", StringComparison.OrdinalIgnoreCase))
                    {
                        if (config.HudEnabled)
                        {
                            lines.Add("dxvk.hud = fps,devinfo");
                        }
                        hudHandled = true;
                        continue;
                    }

                    if (trimmed.StartsWith("dxvk.maxFrameRate", StringComparison.OrdinalIgnoreCase))
                    {
                        if (config.FrameLimitEnabled && config.FrameLimit > 0)
                        {
                            lines.Add($"dxvk.maxFrameRate = {config.FrameLimit}");
                        }
                        frameLimitHandled = true;
                        continue;
                    }

                    // Preserve all other pre-existing user settings
                    lines.Add(line);
                }
            }

            if (config.HudEnabled && !hudHandled)
            {
                lines.Add("dxvk.hud = fps,devinfo");
            }

            if (config.FrameLimitEnabled && config.FrameLimit > 0 && !frameLimitHandled)
            {
                lines.Add($"dxvk.maxFrameRate = {config.FrameLimit}");
            }

            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        public void WriteConfig(GameProfile profile)
        {
            try
            {
                string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                    return;

                var config = new DxvkConfiguration
                {
                    HudEnabled = profile.HudEnabled,
                    FrameLimit = profile.FrameLimit,
                    FrameLimitEnabled = profile.FrameLimit > 0
                };

                string configPath = Path.Combine(gameDir, ConfigFileName);
                string? existing = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
                string? content = GenerateConfigContent(config, existing);

                if (content == null)
                {
                    // No config needed - do not create or overwrite with empty
                    return;
                }

                File.WriteAllText(configPath, content);
            }
            catch
            {
                // Non-fatal
            }
        }
    }
}
