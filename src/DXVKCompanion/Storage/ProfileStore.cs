using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DXVKCompanion.Models;

namespace DXVKCompanion.Storage
{
    public class ProfileStore
    {
        private readonly Dictionary<string, GameProfile> _profiles = new();

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

        private void Load()
        {
            Paths.EnsureDirectories();

            if (!File.Exists(Paths.ProfilesFile))
                return;

            var json = File.ReadAllText(Paths.ProfilesFile);
            var list = JsonSerializer.Deserialize<List<GameProfile>>(json);

            if (list == null)
                return;

            foreach (var p in list)
                _profiles[p.ExePath] = p;
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
