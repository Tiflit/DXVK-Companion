using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DXVKCompanion.Models;

namespace DXVKCompanion.Storage
{
    public class ProfileStore
    {
        // Windows paths are case-insensitive at the filesystem level — without this comparer,
        // "C:\Games\Game.exe" and "c:\games\game.exe" would create two separate profiles
        // for what is actually the same file.
        private readonly Dictionary<string, GameProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

        public ProfileStore()
        {
            Load();
        }

        public GameProfile GetOrCreate(string exePath)
        {
            if (_profiles.TryGetValue(exePath, out var profile))
                return profile;

            var newProfile = new GameProfile(exePath);
            _profiles[exePath] = newProfile;
            Save(newProfile);

            return newProfile;
        }

        public void Save(GameProfile profile)
        {
            _profiles[profile.ExePath] = profile;
            WriteAll();
        }

        public IEnumerable<GameProfile> GetAll() => _profiles.Values;

        private void Load()
        {
            Paths.EnsureDirectories();

            if (!File.Exists(Paths.ProfilesFile))
                return;

            try
            {
                var json = File.ReadAllText(Paths.ProfilesFile);
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
            Paths.EnsureDirectories();

            var list = new List<GameProfile>(_profiles.Values);

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(Paths.ProfilesFile, json);
        }
    }
}
