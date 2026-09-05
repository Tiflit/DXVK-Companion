using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DXVKCompanion.Models;

namespace DXVKCompanion.Storage
{
    public sealed class GameLibraryStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, GameInstallation> _installations =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private const string RecoverySuffix = ".recovery";

        public GameLibraryStore() => Load();

        public IReadOnlyCollection<GameInstallation> GetAll()
        {
            lock (_sync)
            {
                return _installations.Values.ToList().AsReadOnly();
            }
        }

        public GameInstallation? FindByInstallationPath(string installationPath)
        {
            var normalized = GameInstallation.NormalizeInstallationPath(installationPath);
            lock (_sync)
            {
                return _installations.TryGetValue(normalized, out var installation)
                    ? installation
                    : null;
            }
        }

        public GameInstallation GetOrCreateInstallation(string installationPath, string? displayName = null)
        {
            var normalized = GameInstallation.NormalizeInstallationPath(installationPath);

            lock (_sync)
            {
                if (_installations.TryGetValue(normalized, out var existing))
                    return existing;

                var installation = new GameInstallation
                {
                    InstallationPath = normalized,
                    DisplayName = string.IsNullOrWhiteSpace(displayName)
                        ? new DirectoryInfo(normalized).Name
                        : displayName
                };

                _installations[normalized] = installation;
                WriteAllLocked();
                return installation;
            }
        }

        public void Save(GameInstallation installation)
        {
            if (installation == null) throw new ArgumentNullException(nameof(installation));

            lock (_sync)
            {
                installation.InstallationPath =
                    GameInstallation.NormalizeInstallationPath(installation.InstallationPath);

                if (string.IsNullOrWhiteSpace(installation.Id))
                    installation.Id = Guid.NewGuid().ToString("N");

                _installations[installation.InstallationPath] = installation;
                WriteAllLocked();
            }
        }

        public void SaveAll()
        {
            lock (_sync)
            {
                WriteAllLocked();
            }
        }

        private void Load()
        {
            Paths.EnsureDirectories();
            Directory.CreateDirectory(GameLibraryPaths.BackupsDir);

            if (File.Exists(GameLibraryPaths.GameLibraryFile))
            {
                var result = TryLoadCurrentFormat();
                if (result == LoadResult.Success || result == LoadResult.FutureSchema)
                    return;

                // A current-format file exists but is unreadable. Never silently
                // fall back to stale legacy data. Preserve it first.
                TryPreserveBrokenCurrentFile();
                Log($"GameLibraryStore: current library could not be loaded. Preserved a recovery copy; legacy migration was not used automatically.");
                return;
            }

            TryMigrateLegacyProfiles();
        }

        private LoadResult TryLoadCurrentFormat()
        {
            try
            {
                var json = File.ReadAllText(GameLibraryPaths.GameLibraryFile);
                var library = JsonSerializer.Deserialize<GameLibrary>(json, JsonOptions);
                if (library == null)
                    return LoadResult.Invalid;

                if (library.SchemaVersion > GameLibrary.CurrentSchemaVersion)
                {
                    Log($"GameLibraryStore: library schema {library.SchemaVersion} is newer than supported schema {GameLibrary.CurrentSchemaVersion}. No migration was attempted.");
                    return LoadResult.FutureSchema;
                }

                lock (_sync)
                {
                    _installations.Clear();
                    foreach (var installation in library.Installations ?? new List<GameInstallation>())
                    {
                        if (string.IsNullOrWhiteSpace(installation.InstallationPath))
                            continue;

                        var normalized = GameInstallation.NormalizeInstallationPath(installation.InstallationPath);
                        installation.InstallationPath = normalized;
                        installation.ManagementPolicy ??= ManagementPolicy.UseGlobal();
                        installation.Configuration ??= new DxvkConfiguration();
                        installation.Executables ??= new List<ExecutableProfile>();
                        installation.ManagedFiles ??= new List<ManagedFileRecord>();
                        _installations[normalized] = installation;
                    }
                }

                return LoadResult.Success;
            }
            catch (Exception ex)
            {
                Log($"GameLibraryStore: failed to load current library: {ex.GetType().Name} - {ex.Message}");
                return LoadResult.Invalid;
            }
        }

        private void TryMigrateLegacyProfiles()
        {
            if (!File.Exists(Paths.ProfilesFile))
                return;

            try
            {
                var json = File.ReadAllText(Paths.ProfilesFile);
                var legacyProfiles = JsonSerializer.Deserialize<List<LegacyGameProfile>>(json, JsonOptions);
                if (legacyProfiles == null || legacyProfiles.Count == 0)
                    return;

                lock (_sync)
                {
                    _installations.Clear();

                    foreach (var legacy in legacyProfiles)
                    {
                        if (string.IsNullOrWhiteSpace(legacy.ExePath))
                            continue;

                        var exePath = Path.GetFullPath(legacy.ExePath);
                        var installationPath = Path.GetDirectoryName(exePath);
                        if (string.IsNullOrWhiteSpace(installationPath))
                            continue;

                        var normalizedInstallation =
                            GameInstallation.NormalizeInstallationPath(installationPath);

                        if (!_installations.TryGetValue(normalizedInstallation, out var installation))
                        {
                            installation = new GameInstallation
                            {
                                InstallationPath = normalizedInstallation,
                                DisplayName = new DirectoryInfo(normalizedInstallation).Name
                            };
                            _installations[normalizedInstallation] = installation;
                        }

                        var relativeExePath = Path.GetRelativePath(normalizedInstallation, exePath);
                        var executable = installation.GetOrAddExecutable(relativeExePath, legacy.ExeName);
                        executable.LastKnownApi = legacy.Api;
                        executable.LastKnownArchitecture = legacy.Architecture;

                        ReconcileLegacyFrameLimit(installation, legacy);

                        if (legacy.DxvkEnabled)
                        {
                            ReconcileLegacyManagedVersion(installation, legacy);
                            ReconcileLegacyManagedArchitecture(installation, legacy);
                            CreateLegacyManagedFileRecords(installation, legacy);
                        }
                    }

                    UpdateRestorationStateLocked();
                    WriteAllLocked();
                }

                Log("GameLibraryStore: legacy profiles migrated to the new game-library format. Legacy games.json was not modified.");
            }
            catch (Exception ex)
            {
                Log($"GameLibraryStore: legacy migration failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private static void ReconcileLegacyManagedVersion(GameInstallation installation, LegacyGameProfile legacy)
        {
            if (string.IsNullOrWhiteSpace(legacy.DxvkVersion))
                return;

            if (string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion))
            {
                installation.ManagedDxvkVersion = legacy.DxvkVersion;
                return;
            }

            if (!string.Equals(installation.ManagedDxvkVersion, legacy.DxvkVersion, StringComparison.OrdinalIgnoreCase))
            {
                installation.ManagedDxvkVersion = null;
                installation.ConflictFlags |= InstallationConflictFlags.DxvkVersion;
            }
        }

        private static void ReconcileLegacyManagedArchitecture(GameInstallation installation, LegacyGameProfile legacy)
        {
            if (string.IsNullOrWhiteSpace(legacy.Architecture) ||
                string.Equals(legacy.Architecture, "Unknown", StringComparison.OrdinalIgnoreCase))
                return;

            if (string.IsNullOrWhiteSpace(installation.ManagedDxvkArchitecture))
            {
                installation.ManagedDxvkArchitecture = legacy.Architecture;
                return;
            }

            if (!string.Equals(installation.ManagedDxvkArchitecture, legacy.Architecture, StringComparison.OrdinalIgnoreCase))
            {
                installation.ManagedDxvkArchitecture = null;
                installation.ConflictFlags |= InstallationConflictFlags.Architecture;
            }
        }

        private static void ReconcileLegacyFrameLimit(GameInstallation installation, LegacyGameProfile legacy)
        {
            if (legacy.FrameLimit <= 0)
                return;

            if (!installation.Configuration.FrameLimitEnabled)
            {
                installation.Configuration.FrameLimit = legacy.FrameLimit;
                installation.Configuration.FrameLimitEnabled = true;
                return;
            }

            if (installation.Configuration.FrameLimit != legacy.FrameLimit)
            {
                // Do not let an arbitrary legacy profile win. Disable the
                // installation-level value and leave a diagnostic trail.
                installation.Configuration.FrameLimit = 120;
                installation.Configuration.FrameLimitEnabled = false;
                installation.ConflictFlags |= InstallationConflictFlags.FrameLimit;
            }
        }

        private static void CreateLegacyManagedFileRecords(
            GameInstallation installation,
            LegacyGameProfile legacy)
        {
            foreach (var fileName in GetLegacyDxvkFileSet(legacy.Api))
            {
                var record = installation.GetOrAddManagedFile(fileName);
                var backupPath = Path.Combine(
                    installation.InstallationPath,
                    fileName + ".bak");

                // Legacy Companion used .bak files in the game directory.
                // Presence is useful evidence, but the new safety engine will
                // still verify the file before using the backup.
                if (File.Exists(backupPath))
                {
                    record.OriginalState = FileOriginalState.Existing;
                    record.BackupRelativePath = null; // copied later by the new file engine
                    record.CurrentState = ManagedFileState.Unknown;
                }
                else
                {
                    // We know legacy Companion claims this file was managed,
                    // but we cannot safely infer the original state.
                    record.OriginalState = FileOriginalState.Unknown;
                    record.CurrentState = ManagedFileState.Unknown;
                    installation.ConflictFlags |= InstallationConflictFlags.UnknownOriginalFile;
                }

                record.ManagedDxvkVersion = installation.ManagedDxvkVersion;
            }
        }

        private static IReadOnlyList<string> GetLegacyDxvkFileSet(GraphicsApi api)
        {
            return api switch
            {
                GraphicsApi.DX9 => new[] { "d3d9.dll" },
                GraphicsApi.DX10 => new[] { "d3d11.dll", "dxgi.dll" },
                GraphicsApi.DX11 => new[] { "d3d11.dll", "dxgi.dll" },
                _ => Array.Empty<string>()
            };
        }

        private void UpdateRestorationStateLocked()
        {
            foreach (var installation in _installations.Values)
            {
                if (installation.ConflictFlags != InstallationConflictFlags.None)
                {
                    installation.RestorationState = RestorationState.AttentionRequired;
                    continue;
                }

                if (installation.ManagedFiles.Count == 0)
                {
                    installation.RestorationState = RestorationState.None;
                    continue;
                }

                if (installation.ManagedFiles.Any(x => x.CurrentState == ManagedFileState.ExternallyChanged))
                {
                    installation.RestorationState = RestorationState.AttentionRequired;
                    continue;
                }

                if (installation.ManagedFiles.Any(x => x.OriginalState == FileOriginalState.Unknown))
                {
                    installation.RestorationState = RestorationState.AttentionRequired;
                    continue;
                }

                installation.RestorationState =
                    installation.ManagedDxvkVersion == null
                        ? RestorationState.Restored
                        : RestorationState.Managed;
            }
        }

        private void TryPreserveBrokenCurrentFile()
        {
            try
            {
                if (!File.Exists(GameLibraryPaths.GameLibraryFile))
                    return;

                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
                var recoveryPath = GameLibraryPaths.GameLibraryFile + RecoverySuffix + "." + stamp + ".json";
                File.Copy(GameLibraryPaths.GameLibraryFile, recoveryPath, false);
                Log($"GameLibraryStore: preserved unreadable library as {Path.GetFileName(recoveryPath)}.");
            }
            catch (Exception ex)
            {
                Log($"GameLibraryStore: could not preserve unreadable library: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private void WriteAllLocked()
        {
            Paths.EnsureDirectories();
            Directory.CreateDirectory(GameLibraryPaths.BackupsDir);

            UpdateRestorationStateLocked();

            var library = new GameLibrary
            {
                SchemaVersion = GameLibrary.CurrentSchemaVersion,
                Installations = _installations.Values
                    .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.InstallationPath, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            var json = JsonSerializer.Serialize(library, JsonOptions);
            var tempPath = GameLibraryPaths.GameLibraryFile + ".tmp";

            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, GameLibraryPaths.GameLibraryFile, true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // Cleanup failure must not mask the primary operation.
                }
            }
        }

        private static void Log(string message)
        {
            try { Utils.Logger.Log(message); } catch { }
        }

        private enum LoadResult
        {
            Invalid = 0,
            Success = 1,
            FutureSchema = 2
        }

        private sealed class LegacyGameProfile
        {
            public string ExePath { get; set; } = string.Empty;
            public string ExeName { get; set; } = string.Empty;
            public GraphicsApi Api { get; set; } = GraphicsApi.Unknown;
            public string Architecture { get; set; } = "Unknown";
            public bool DxvkEnabled { get; set; }
            public string? DxvkVersion { get; set; }
            public bool HudEnabled { get; set; }
            public int FrameLimit { get; set; }
        }
    }
}
