using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;

namespace DXVKCompanion.Storage
{
    public class ProfileStore
    {
        // Windows paths are case-insensitive at the filesystem level — without this comparer,
        // "C:\Games\Game.exe" and "c:\games\game.exe" would create two separate profiles
        // for what is actually the same file.
        private readonly Dictionary<string, GameProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _profilesFile;

        public string ProfilesFile => _profilesFile;
        internal IOperationLeaseCoordinator LeaseCoordinator { get; set; } = OperationLeaseCoordinator.Default;

        internal Action<string, string>? CustomTempWriter { get; set; }
        internal Action<string, string>? CustomCommitReplace { get; set; }
        internal Action<string>? CustomTempCleaner { get; set; }
        internal bool HasPendingWriteRetry { get; private set; }

        public ProfileStore(string? profilesFile = null)
        {
            _profilesFile = profilesFile ?? Paths.ProfilesFile;
            Load();
        }

        public GameProfile GetOrCreate(string exePath) => GetOrCreate(exePath, null);

        internal GameProfile GetOrCreate(string exePath, IOperationLease? storeLease)
        {
            if (_profiles.TryGetValue(exePath, out var profile))
                return profile;

            var newProfile = new GameProfile(exePath);
            _profiles[exePath] = newProfile;
            Save(newProfile, storeLease);

            return newProfile;
        }

        public void Save(GameProfile profile) => Save(profile, null);

        internal void Save(GameProfile profile, IOperationLease? storeLease)
        {
            _profiles[profile.ExePath] = profile;

            IOperationLease? acquiredLease = null;
            if (storeLease != null)
            {
                if (storeLease.IsDisposed || !storeLease.IsAcquired || !storeLease.IsValidForStore(_profilesFile))
                {
                    throw new ArgumentException("Invalid, mismatched, or disposed store lease token.", nameof(storeLease));
                }
            }
            else
            {
                acquiredLease = LeaseCoordinator.TryAcquireStoreLease(_profilesFile, TimeSpan.FromMilliseconds(500));
                if (acquiredLease == null)
                {
                    HasPendingWriteRetry = true;
                    throw new IOException($"Operation in progress for profile store: {_profilesFile}. Please retry shortly.");
                }
            }

            try
            {
                WriteAll();
            }
            finally
            {
                acquiredLease?.Dispose();
            }
        }

        public IEnumerable<GameProfile> GetAll() => _profiles.Values;

        private void Load()
        {
            var dir = Path.GetDirectoryName(_profilesFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(_profilesFile))
                return;

            try
            {
                var json = File.ReadAllText(_profilesFile);
                var list = JsonSerializer.Deserialize<List<GameProfile>>(json);

                if (list == null)
                    return;

                foreach (var p in list)
                    _profiles[p.ExePath] = p;
            }
            catch
            {
                // Corrupted profile file → ignore and start fresh
            }
        }

        private void WriteAll()
        {
            var dir = Path.GetDirectoryName(_profilesFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var list = new List<GameProfile>(_profiles.Values);

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            string tempPath = _profilesFile + ".tmp." + Guid.NewGuid().ToString("N");
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
                    CustomCommitReplace(tempPath, _profilesFile);
                }
                else
                {
                    if (File.Exists(_profilesFile))
                    {
                        File.Move(tempPath, _profilesFile, overwrite: true);
                    }
                    else
                    {
                        File.Move(tempPath, _profilesFile);
                    }
                }

                committed = true;
                HasPendingWriteRetry = false;
            }
            finally
            {
                if (!committed)
                {
                    HasPendingWriteRetry = true;
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
                        // Clean only the temporary file owned by the current operation.
                        // Cleanup failure must never mask the primary failure or reverse a commit.
                    }
                }
            }
        }
    }
}
