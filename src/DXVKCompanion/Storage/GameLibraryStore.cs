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

        internal Action<string, string>? CustomTempWriter { get; set; }
        internal Action<string, string>? CustomCommitReplace { get; set; }
        internal Action<string>? CustomTempCleaner { get; set; }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private const string RecoverySuffix = ".recovery";

        private readonly string _libraryFilePath;
        private readonly string _backupsDirectoryPath;

        private bool _isRefused;
        private string? _refusedSchemaText;

        public bool IsRefused
        {
            get
            {
                lock (_sync)
                {
                    return _isRefused;
                }
            }
        }

        public string? RefusedSchemaVersionText
        {
            get
            {
                lock (_sync)
                {
                    return _refusedSchemaText;
                }
            }
        }

        public GameLibraryStore(
            string? libraryFilePath = null,
            string? backupsDirectoryPath = null,
            string? legacyProfilesPath = null)
        {
            _libraryFilePath = libraryFilePath ?? GameLibraryPaths.GameLibraryFile;
            _backupsDirectoryPath = backupsDirectoryPath ?? GameLibraryPaths.BackupsDir;
            // legacyProfilesPath is retained for caller compatibility, but clean-slate V1 policy does not import legacy profiles.
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
                EnsureNotRefusedLocked();

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
                EnsureNotRefusedLocked();

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
                EnsureNotRefusedLocked();

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
                EnsureNotRefusedLocked();
                WriteAllLocked();
            }
        }

        private void EnsureNotRefusedLocked()
        {
            if (_isRefused)
            {
                throw new IncompatibleLibrarySchemaException(
                    _refusedSchemaText,
                    GameLibrary.CurrentSchemaVersion,
                    $"Cannot modify game library: file on disk declares schema {_refusedSchemaText}, which is newer than supported schema {GameLibrary.CurrentSchemaVersion}.");
            }

            if (File.Exists(_libraryFilePath))
            {
                var assessment = GameLibrarySchemaCompatibility.AssessFile(_libraryFilePath);
                if (assessment.IsRefused)
                {
                    _isRefused = true;
                    _refusedSchemaText = assessment.FoundSchemaVersionText;
                    throw new IncompatibleLibrarySchemaException(
                        _refusedSchemaText,
                        GameLibrary.CurrentSchemaVersion,
                        $"Cannot modify game library: file on disk declares schema {_refusedSchemaText}, which is newer than supported schema {GameLibrary.CurrentSchemaVersion}.");
                }
            }
        }

        private void Load()
        {
            if (File.Exists(_libraryFilePath))
            {
                var assessment = GameLibrarySchemaCompatibility.AssessFile(_libraryFilePath);
                if (assessment.IsRefused)
                {
                    lock (_sync)
                    {
                        _isRefused = true;
                        _refusedSchemaText = assessment.FoundSchemaVersionText;
                    }
                    // A refused library remains byte-for-byte unchanged.
                    // No temporary file, recovery copy, backup creation/rotation or directory creation on that refusal path.
                    return;
                }

                Paths.EnsureDirectories();
                Directory.CreateDirectory(_backupsDirectoryPath);

                var result = TryLoadCurrentFormat();
                if (result == LoadResult.Success || result == LoadResult.FutureSchema)
                    return;

                // A current-format file exists but is unreadable. Never silently
                // fall back to stale legacy data. Preserve it first.
                TryPreserveBrokenCurrentFile();
                Log($"GameLibraryStore: current library could not be loaded. Preserved a recovery copy.");
                return;
            }

            Paths.EnsureDirectories();
            Directory.CreateDirectory(_backupsDirectoryPath);
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
                    lock (_sync)
                    {
                        _isRefused = true;
                        _refusedSchemaText = library.SchemaVersion.ToString();
                    }
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
            EnsureNotRefusedLocked();

            Paths.EnsureDirectories();
            Directory.CreateDirectory(_backupsDirectoryPath);

            var library = new GameLibrary
            {
                SchemaVersion = GameLibrary.CurrentSchemaVersion,
                Installations = _installations.Values
                    .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.InstallationPath, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            var json = JsonSerializer.Serialize(library, JsonOptions);
            var tempPath = _libraryFilePath + ".tmp." + Guid.NewGuid().ToString("N");
            bool committed = false;

            try
            {
                if (CustomTempWriter != null)
                {
                    CustomTempWriter(tempPath, json);
                }
                else
                {
                    File.WriteAllText(tempPath, json);
                }

                if (CustomCommitReplace != null)
                {
                    CustomCommitReplace(tempPath, _libraryFilePath);
                }
                else
                {
                    if (File.Exists(_libraryFilePath))
                    {
                        File.Move(tempPath, _libraryFilePath, overwrite: true);
                    }
                    else
                    {
                        File.Move(tempPath, _libraryFilePath);
                    }
                }

                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    try
                    {
                        if (CustomTempCleaner != null)
                        {
                            CustomTempCleaner(tempPath);
                        }
                        else if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch
                    {
                        // Cleanup failure must not mask the primary operation.
                    }
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
