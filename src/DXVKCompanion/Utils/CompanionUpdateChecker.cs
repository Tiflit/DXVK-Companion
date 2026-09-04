using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DXVKCompanion.Utils
{
    public class CompanionUpdateChecker
    {
        private readonly HttpClient _client;

        public CompanionUpdateChecker(HttpClient client)
        {
            _client = client;
        }

        public async Task<string?> GetLatestVersionAsync()
        {
            try
            {
                var json = await _client.GetStringAsync(
                    "https://api.github.com/repos/Tiflit/DXVK-Companion/releases/latest"
                );

                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("tag_name").GetString();
            }
            catch
            {
                return null;
            }
        }
    }
}
