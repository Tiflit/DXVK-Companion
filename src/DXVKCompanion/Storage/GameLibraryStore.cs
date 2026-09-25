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

        public GameLibraryStore(string? libraryFilePath = null, string? backupsDirectoryPath = null)
        {
            _libraryFilePath = libraryFilePath ?? GameLibraryPaths.GameLibraryFile;
            _backupsDirectoryPath = backupsDirectoryPath ?? GameLibraryPaths.BackupsDir;
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
