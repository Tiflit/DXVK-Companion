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

        private readonly string _libraryFilePath;
        private readonly string _backupsDirectoryPath;
        private readonly string _legacyProfilesPath;

        public GameLibraryStore(
            string? libraryFilePath = null,
            string? backupsDirectoryPath = null,
            string? legacyProfilesPath = null)
        {
            _libraryFilePath = libraryFilePath ?? GameLibraryPaths.GameLibraryFile;
            _backupsDirectoryPath = backupsDirectoryPath ?? GameLibraryPaths.BackupsDir;
            _legacyProfilesPath = legacyProfilesPath ?? Paths.ProfilesFile;
            Load();
        }

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

        public GameInstallation? FindInstallationForExecutable(string exeFullPath)
        {
            if (string.IsNullOrWhiteSpace(exeFullPath)) return null;

            var fullExe = Path.GetFullPath(exeFullPath);
            var exeDir = Path.GetDirectoryName(fullExe);

            lock (_sync)
            {
                // 1. Direct match on executable directory
                if (exeDir != null)
                {
                    var normalizedDir = GameInstallation.NormalizeInstallationPath(exeDir);
                    if (_installations.TryGetValue(normalizedDir, out var direct))
                        return direct;
                }

                // 2. Match on existing installation where executable path is inside installation root
                foreach (var installation in _installations.Values)
                {
                    var rootWithSep = installation.InstallationPath.EndsWith(Path.DirectorySeparatorChar)
                        ? installation.InstallationPath
                        : installation.InstallationPath + Path.DirectorySeparatorChar;

                    if (fullExe.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                    {
                        return installation;
                    }
                }

                return null;
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

        public GameInstallation RecordDetectionSnapshot(DetectionSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            lock (_sync)
            {
                var installation = GetOrCreateInstallation(snapshot.InstallationRoot, Path.GetFileName(snapshot.InstallationRoot));
                var profile = installation.GetOrAddExecutable(snapshot.ExecutableRelativePath, snapshot.ProcessName);

                if (profile.LastKnownApi != GraphicsApi.Unknown &&
                    snapshot.Classification.PrimaryApi != GraphicsApi.Unknown &&
                    profile.LastKnownApi != snapshot.Classification.PrimaryApi)
                {
                    snapshot.HasApiChanged = true;
                    snapshot.PreviousApi = profile.LastKnownApi;
                    Log($"GameLibraryStore: API transition detected for {snapshot.ExecutableRelativePath}: {profile.LastKnownApi} -> {snapshot.Classification.PrimaryApi}");
                }

                profile.LastKnownApi = snapshot.Classification.PrimaryApi;
                profile.ApiConfidence = snapshot.Classification.Confidence;
                profile.LastKnownArchitecture = snapshot.Classification.Architecture;
                profile.DetectionEvidence = snapshot.Classification.Evidence.ToList();
                profile.LastSeenUtc = snapshot.TimestampUtc;

                installation.LastSeenUtc = snapshot.TimestampUtc;
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
            Directory.CreateDirectory(_backupsDirectoryPath);

            if (File.Exists(_libraryFilePath))
            {
                var result = TryLoadCurrentFormat();
                if (result == LoadResult.Success || result == LoadResult.FutureSchema)
                    return;

                // A current-format file exists but is unreadable. Never silently
                // fall back to stale legacy data. Preserve it first.
                TryPreserveBrokenCurrentFile();
                Log($"GameLibraryStore: current library could not be loaded. Preserved a recovery copy; no legacy import was attempted.");
                return;
            }

            // Only attempt legacy migration when current library file does not exist
            if (File.Exists(_legacyProfilesPath))
            {
                TryMigrateLegacyProfiles();
            }
        }

        private void TryMigrateLegacyProfiles()
        {
            try
            {
                var json = File.ReadAllText(_legacyProfilesPath);
                var legacyProfiles = JsonSerializer.Deserialize<List<GameProfile>>(json, JsonOptions);
                if (legacyProfiles == null || legacyProfiles.Count == 0)
                    return;

                lock (_sync)
                {
                    _installations.Clear();
                    foreach (var legacy in legacyProfiles)
                    {
                        if (string.IsNullOrWhiteSpace(legacy.ExePath))
                            continue;

                        string exeFullPath;
                        try
                        {
                            exeFullPath = Path.GetFullPath(legacy.ExePath);
                        }
                        catch
                        {
                            continue;
                        }

                        string? gameDir = Path.GetDirectoryName(exeFullPath);
                        if (string.IsNullOrWhiteSpace(gameDir))
                            continue;

                        var normalizedDir = GameInstallation.NormalizeInstallationPath(gameDir);
                        if (!_installations.TryGetValue(normalizedDir, out var installation))
                        {
                            installation = new GameInstallation
                            {
                                InstallationPath = normalizedDir,
                                DisplayName = new DirectoryInfo(normalizedDir).Name,
                                ManagementPolicy = ManagementPolicy.UseGlobal(),
                                Configuration = new DxvkConfiguration
                                {
                                    FrameLimitEnabled = legacy.FrameLimit > 0,
                                    FrameLimit = legacy.FrameLimit > 0 ? legacy.FrameLimit : 120,
                                    HudEnabled = legacy.HudEnabled
                                }
                            };
                            _installations[normalizedDir] = installation;
                        }

                        string relExe = Path.GetFileName(exeFullPath);
                        var exeProfile = installation.GetOrAddExecutable(relExe, legacy.ExeName);
                        exeProfile.LastKnownApi = legacy.Api;
                        exeProfile.LastKnownArchitecture = legacy.Architecture;

                        if (legacy.DxvkEnabled)
                        {
                            installation.RestorationState = RestorationState.Managed;
                            if (!string.IsNullOrWhiteSpace(legacy.DxvkVersion))
                            {
                                installation.ManagedDxvkVersion = legacy.DxvkVersion;
                            }
                        }
                    }

                    WriteAllLocked();
                }

                Log($"GameLibraryStore: successfully migrated {legacyProfiles.Count} legacy profile(s) from {_legacyProfilesPath}.");
            }
            catch (Exception ex)
            {
                Log($"GameLibraryStore: legacy migration failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private LoadResult TryLoadCurrentFormat()
        {
            try
            {
                var json = File.ReadAllText(_libraryFilePath);
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
                if (!File.Exists(_libraryFilePath))
                    return;

                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
                var recoveryPath = _libraryFilePath + RecoverySuffix + "." + stamp + ".json";
                File.Copy(_libraryFilePath, recoveryPath, false);
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
            Directory.CreateDirectory(_backupsDirectoryPath);

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
            var tempPath = _libraryFilePath + ".tmp";

            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _libraryFilePath, true);
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


    }
}
