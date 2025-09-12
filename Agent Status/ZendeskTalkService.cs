using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Agent_Status
{
    public class ZendeskTalkService
    {
        private readonly HttpClient _httpClient;
        private readonly string _subdomain;
        private readonly string _email;
        private readonly string _apiToken;

        public ZendeskTalkService(IConfiguration configuration)
        {
            _subdomain = configuration["Zendesk:Subdomain"]!;
            _email = configuration["Zendesk:Email"]!;
            _apiToken = configuration["Zendesk:ApiToken"]!;
            _httpClient = new HttpClient();
            var authString = $"{_email}/token:{_apiToken}";
            var authBytes = Encoding.UTF8.GetBytes(authString);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(authBytes));
        }

        public async Task<string> GetAgentAvailabilityAsync(int agentId)
        {
            var url = $"https://{_subdomain}.zendesk.com/api/v2/channels/voice/availabilities/{agentId}.json";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        public async Task<string> UpdateAgentAvailabilityAsync(int agentId, string agentState, string via)
        {
            var url = $"https://{_subdomain}.zendesk.com/api/v2/channels/voice/availabilities/{agentId}.json";
            var body = new
            {
                availability = new
                {
                    agent_state = agentState,
                    via = via
                }
            };
            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PutAsync(url, content);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
    }
}
