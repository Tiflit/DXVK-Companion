using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DXVKCompanion.Utils
{
    public class CompanionUpdateChecker
    {
        private const string ApiUrl = "https://api.github.com/repos/Tiflit/DXVK-Companion/releases/latest";
        private readonly HttpClient _client;

        public CompanionUpdateChecker()
        {
            _client = new HttpClient();
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("DXVK-Companion/1.0");
        }

        public async Task<string?> GetLatestVersionAsync()
        {
            try
            {
                var json = await _client.GetStringAsync(ApiUrl);
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
