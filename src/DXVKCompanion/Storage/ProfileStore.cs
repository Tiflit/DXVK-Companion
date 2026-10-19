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
        private readonly object _sync = new();
        private readonly string _profilesFile;
        private byte[]? _lastCommittedBytes;

        internal string ProfilesFile => _profilesFile;
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
            lock (_sync)
            {
                if (_profiles.TryGetValue(exePath, out var profile))
                    return profile;
            }

            var newProfile = new GameProfile(exePath);
            Save(newProfile, storeLease);

            return newProfile;
        }

        public void Save(GameProfile profile) => Save(profile, null);

        internal void Save(GameProfile profile, IOperationLease? storeLease)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

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
                lock (_sync)
                {
                    // Authoritative profile-baseline verification before mutating in-memory dictionary
                    bool fileExists = File.Exists(_profilesFile);
                    if (_lastCommittedBytes == null)
                    {
                        if (fileExists)
                        {
                            HasPendingWriteRetry = true;
                            throw new IOException($"Profile store baseline mismatch: '{_profilesFile}' was created externally.");
                        }
                    }
                    else
                    {
                        if (!fileExists)
                        {
                            HasPendingWriteRetry = true;
                            throw new IOException($"Profile store baseline mismatch: '{_profilesFile}' was deleted externally.");
                        }

                        byte[] currentDiskBytes = File.ReadAllBytes(_profilesFile);
                        if (!currentDiskBytes.AsSpan().SequenceEqual(_lastCommittedBytes))
                        {
                            HasPendingWriteRetry = true;
                            throw new IOException($"Profile store baseline mismatch: '{_profilesFile}' has been modified externally.");
                        }
                    }

                    // Protected in-memory mutation
                    _profiles[profile.ExePath] = profile;

                    byte[] committedBytes = WriteAll();
                    _lastCommittedBytes = committedBytes;
                }
            }
            finally
            {
                acquiredLease?.Dispose();
            }
        }

        internal bool TryCheckCanModifyProfileStore(out string? refusalReason, IOperationLease? storeLease)
        {
            refusalReason = null;
            IOperationLease? acquiredLease = null;
            if (storeLease != null)
            {
                if (storeLease.IsDisposed || !storeLease.IsAcquired || !storeLease.IsValidForStore(_profilesFile))
                {
                    refusalReason = "Invalid, mismatched, or disposed store lease token for profile store.";
                    return false;
                }
            }
            else
            {
                acquiredLease = LeaseCoordinator.TryAcquireStoreLease(_profilesFile, TimeSpan.FromMilliseconds(500));
                if (acquiredLease == null)
                {
                    refusalReason = $"Operation in progress for profile store: {_profilesFile}. Please retry shortly.";
                    return false;
                }
            }

            try
            {
                lock (_sync)
                {
                    bool fileExists = File.Exists(_profilesFile);
                    if (_lastCommittedBytes == null)
                    {
                        if (fileExists)
                        {
                            refusalReason = $"Profile store baseline mismatch: '{_profilesFile}' was created externally.";
                            return false;
                        }
                    }
                    else
                    {
                        if (!fileExists)
                        {
                            refusalReason = $"Profile store baseline mismatch: '{_profilesFile}' was deleted externally.";
                            return false;
                        }

                        byte[] currentDiskBytes = File.ReadAllBytes(_profilesFile);
                        if (!currentDiskBytes.AsSpan().SequenceEqual(_lastCommittedBytes))
                        {
                            refusalReason = $"Profile store baseline mismatch: '{_profilesFile}' has been modified externally.";
                            return false;
                        }
                    }
                    return true;
                }
            }
            finally
            {
                acquiredLease?.Dispose();
            }
        }

        public IEnumerable<GameProfile> GetAll()
        {
            lock (_sync)
            {
                return new List<GameProfile>(_profiles.Values);
            }
        }

        private void Load()
        {
            var dir = Path.GetDirectoryName(_profilesFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(_profilesFile))
            {
                _lastCommittedBytes = null;
                return;
            }

            try
            {
                var bytes = File.ReadAllBytes(_profilesFile);
                _lastCommittedBytes = bytes;
                var list = JsonSerializer.Deserialize<List<GameProfile>>(bytes);

                if (list == null)
                    return;

                foreach (var p in list)
                    _profiles[p.ExePath] = p;
            }
            catch
            {
                // Corrupted profile file -> ignore and start fresh
            }
        }

        private byte[] WriteAll()
        {
            var dir = Path.GetDirectoryName(_profilesFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var list = new List<GameProfile>(_profiles.Values);

            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(list, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            string tempPath = _profilesFile + ".tmp." + Guid.NewGuid().ToString("N");
            bool committed = false;
            try
            {
                if (CustomTempWriter != null)
                {
                    string jsonString = System.Text.Encoding.UTF8.GetString(jsonBytes);
                    CustomTempWriter(tempPath, jsonString);
                }
                else
                {
                    File.WriteAllBytes(tempPath, jsonBytes);
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
                return jsonBytes;
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
