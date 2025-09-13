using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Agent_Status
{
    public class ZendeskTalkService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<ZendeskTalkService> _logger;
        private readonly string _subdomain;
        private readonly string _email;
        private readonly string _apiToken;

        public ZendeskTalkService(IConfiguration configuration, ILogger<ZendeskTalkService> logger)
        {
            _logger = logger;
            _subdomain = configuration["Zendesk:Subdomain"]!;
            _email = configuration["Zendesk:Email"]!;
            _apiToken = configuration["Zendesk:ApiToken"]!;
            
            _logger.LogInformation("Initializing ZendeskTalkService for subdomain: {Subdomain}", _subdomain);
            
            _httpClient = new HttpClient();
            var authString = $"{_email}/token:{_apiToken}";
            var authBytes = Encoding.UTF8.GetBytes(authString);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(authBytes));
        }

        public async Task<string> GetAgentAvailabilityAsync(long agentId)
        {
            _logger.LogInformation("Fetching availability for agent {AgentId}", agentId);
            
            var url = $"https://{_subdomain}.zendesk.com/api/v2/channels/voice/availabilities/{agentId}.json";
            
            try
            {
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadAsStringAsync();
                
                _logger.LogInformation("Successfully retrieved availability for agent {AgentId}", agentId);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get availability for agent {AgentId}", agentId);
                throw;
            }
        }

        public async Task<string> GetAllAgentsAsync()
        {
            _logger.LogInformation("Fetching agents and admins with pagination support (excluding light agents)");
            
            var allUsers = new List<object>();
            var url = $"https://{_subdomain}.zendesk.com/api/v2/users.json?role[]=agent&role[]=admin&per_page=100";
            var currentPage = 1;
            const int maxPages = 100; // Safety limit for agent pagination
            
            try
            {
                do
                {
                    _logger.LogInformation("Fetching agents page {Page}", currentPage);
                    
                    try
                    {
                        var response = await _httpClient.GetAsync(url);
                        
                        // Handle the case where we've gone beyond available pages
                        if (!response.IsSuccessStatusCode)
                        {
                            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                            {
                                _logger.LogInformation("Reached end of agent pagination at page {Page} (400 Bad Request)", currentPage);
                                break;
                            }
                            response.EnsureSuccessStatusCode(); // This will throw for other error codes
                        }
                        
                        var pageResult = await response.Content.ReadAsStringAsync();
                        
                        // Parse to check for next page
                        var pageData = JsonSerializer.Deserialize<JsonElement>(pageResult);
                        
                        if (pageData.TryGetProperty("users", out var users))
                        {
                            foreach (var user in users.EnumerateArray())
                            {
                                // Check if this user should be excluded (light agents have role_type = 1)
                                var shouldInclude = true;
                                if (user.TryGetProperty("role_type", out var roleTypeElement) && 
                                    roleTypeElement.ValueKind == JsonValueKind.Number &&
                                    roleTypeElement.GetInt32() == 1)
                                {
                                    shouldInclude = false; // Exclude light agents
                                }
                                
                                if (shouldInclude)
                                {
                                    allUsers.Add(user);
                                }
                            }
                            
                            var includedCount = allUsers.Count - (currentPage - 1) * 100; // Count just added this page
                            _logger.LogInformation("Page {Page}: Retrieved {Total} users, included {Included} (excluding light agents)", 
                                currentPage, users.GetArrayLength(), Math.Max(0, includedCount));
                            
                            // If this page has fewer users than the page size, we're likely at the end
                            if (users.GetArrayLength() < 100)
                            {
                                _logger.LogInformation("Page {Page} contained fewer than 100 users ({Count}), assuming end of data", 
                                    currentPage, users.GetArrayLength());
                                break;
                            }
                        }
                        
                        // Check for next page
                        if (pageData.TryGetProperty("next_page", out var nextPageElement) && 
                            nextPageElement.ValueKind != JsonValueKind.Null)
                        {
                            url = nextPageElement.GetString();
                            currentPage++;
                            
                            // Safety check to prevent excessive pagination
                            if (currentPage > maxPages)
                            {
                                _logger.LogWarning("Reached maximum page limit ({MaxPages}) for agents, stopping pagination for safety", maxPages);
                                break;
                            }
                        }
                        else
                        {
                            url = null;
                            _logger.LogInformation("No next_page URL provided for agents, pagination complete");
                        }
                    }
                    catch (HttpRequestException ex) when (ex.Message.Contains("400"))
                    {
                        _logger.LogInformation("Reached end of agent pagination at page {Page} due to 400 error: {Error}", currentPage, ex.Message);
                        break;
                    }
                }
                while (!string.IsNullOrEmpty(url));

                _logger.LogInformation("Successfully retrieved all agents across {Pages} pages. Total: {Count}", 
                    currentPage, allUsers.Count);
                
                // Return consolidated result
                var result = new { users = allUsers };
                return JsonSerializer.Serialize(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all agents");
                throw;
            }
        }

        /// <summary>
        /// Gets a simplified list of agents with just ID and Name for efficient lookups.
        /// </summary>
        /// <returns>A list of agent objects with Id and Name properties</returns>
        public async Task<List<(long Id, string Name)>> GetAgentInfoAsync()
        {
            _logger.LogInformation("Fetching agent info for name lookups");
            
            var agents = new List<(long Id, string Name)>();
            var url = $"https://{_subdomain}.zendesk.com/api/v2/users.json?role[]=agent&role[]=admin&per_page=100";
            var currentPage = 1;
            const int maxPages = 20; // Limit for just getting names
            
            try
            {
                do
                {
                    var response = await _httpClient.GetAsync(url);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                        {
                            _logger.LogInformation("Reached end of agent info pagination at page {Page}", currentPage);
                            break;
                        }
                        response.EnsureSuccessStatusCode();
                    }
                    
                    var jsonResponse = await response.Content.ReadAsStringAsync();
                    var responseData = JsonSerializer.Deserialize<JsonElement>(jsonResponse);
                    
                    if (responseData.TryGetProperty("users", out var users))
                    {
                        foreach (var user in users.EnumerateArray())
                        {
                            if (user.TryGetProperty("id", out var idElement) && 
                                user.TryGetProperty("name", out var nameElement))
                            {
                                var id = idElement.GetInt64();
                                var name = nameElement.GetString() ?? $"Agent_{id}";
                                
                                // Filter out light agents if role info is available
                                var shouldInclude = true;
                                if (user.TryGetProperty("role", out var roleElement))
                                {
                                    var role = roleElement.GetString();
                                    if (role == "end-user")
                                    {
                                        shouldInclude = false;
                                    }
                                }
                                
                                if (shouldInclude)
                                {
                                    agents.Add((id, name));
                                }
                            }
                        }
                    }
                    
                    // Check for next page
                    url = null;
                    if (responseData.TryGetProperty("next_page", out var nextPageElement) && 
                        nextPageElement.ValueKind != JsonValueKind.Null)
                    {
                        url = nextPageElement.GetString();
                        currentPage++;
                        
                        if (currentPage > maxPages)
                        {
                            _logger.LogWarning("Reached maximum page limit ({MaxPages}) for agent info, stopping for safety", maxPages);
                            break;
                        }
                    }
                }
                while (!string.IsNullOrEmpty(url));
                
                _logger.LogInformation("Retrieved {Count} agent names across {Pages} pages", agents.Count, currentPage);
                return agents;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get agent info");
                throw;
            }
        }

        public async Task<string> UpdateAgentAvailabilityAsync(int agentId, string agentState, string via)
        {
            _logger.LogInformation("Updating availability for agent {AgentId} to state {AgentState} via {Via}", 
                agentId, agentState, via);
            
            var url = $"https://{_subdomain}.zendesk.com/api/v2/channels/voice/availabilities/{agentId}.json";
            var body = new
            {
                availability = new
                {
                    agent_state = agentState,
                    via = via
                }
            };
            
            try
            {
                var json = JsonSerializer.Serialize(body);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PutAsync(url, content);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadAsStringAsync();
                
                _logger.LogInformation("Successfully updated availability for agent {AgentId}", agentId);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update availability for agent {AgentId}", agentId);
                throw;
            }
        }

        /// <summary>
        /// Gets the count of tickets in a specific Zendesk view using the View Count API.
        /// This method retrieves the cached ticket count for the specified view,
        /// which is more efficient than using search queries.
        /// </summary>
        /// <param name="viewId">The ID of the view to get the ticket count for (default: 360077881353)</param>
        /// <returns>
        /// A task that represents the asynchronous operation. The task result contains 
        /// the count of tickets in the view as an integer.
        /// </returns>
        /// <exception cref="HttpRequestException">
        /// Thrown when the API request fails due to network issues or HTTP errors.
        /// </exception>
        /// <exception cref="JsonException">
        /// Thrown when the API response cannot be parsed as valid JSON.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the API response format is unexpected or missing required properties.
        /// </exception>
        /// <remarks>
        /// This method uses the Zendesk View Count API endpoint (/api/v2/views/{view_id}/count)
        /// to efficiently retrieve just the count without fetching all ticket data.
        /// 
        /// View counts are cached by Zendesk and may not reflect real-time changes.
        /// For views with many tickets, counts may be cached for 60-90 minutes.
        /// 
        /// The method includes comprehensive error handling and logging for monitoring
        /// and debugging purposes. Rate limiting (5 requests per minute per view per agent)
        /// is handled by the underlying HTTP client configuration.
        /// 
        /// Example usage:
        /// <code>
        /// var viewTicketsCount = await zendeskService.GetViewTicketsCountAsync();
        /// Console.WriteLine($"There are {viewTicketsCount} tickets in the view");
        /// </code>
        /// </remarks>
        public async Task<int> GetViewTicketsCountAsync(long viewId = 360077881353)
        {
            _logger.LogInformation("Fetching ticket count for view {ViewId}", viewId);
            
            var url = $"https://{_subdomain}.zendesk.com/api/v2/views/{viewId}/count.json";
            
            try
            {
                _logger.LogDebug("Making API request to: {Url}", url);
                
                var response = await _httpClient.GetAsync(url);
                
                // Check for rate limiting
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60;
                    _logger.LogWarning("Rate limited when fetching view ticket count. Retry after {RetryAfter} seconds", retryAfter);
                    throw new HttpRequestException($"Rate limited. Retry after {retryAfter} seconds", null, response.StatusCode);
                }
                
                // Check for authentication/authorization issues
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.LogError("Authentication failed when fetching view ticket count. Check API credentials");
                    throw new HttpRequestException("Authentication failed. Check API credentials", null, response.StatusCode);
                }
                
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _logger.LogError("Access forbidden when fetching view ticket count. Check API permissions");
                    throw new HttpRequestException("Access forbidden. Check API permissions", null, response.StatusCode);
                }
                
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogError("View {ViewId} not found when fetching ticket count", viewId);
                    throw new HttpRequestException($"View {viewId} not found", null, response.StatusCode);
                }
                
                // Ensure successful response
                response.EnsureSuccessStatusCode();
                
                var jsonResponse = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("Received response: {Response}", jsonResponse);
                
                // Parse the JSON response
                JsonElement responseData;
                try
                {
                    responseData = JsonSerializer.Deserialize<JsonElement>(jsonResponse);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to parse JSON response when fetching view ticket count: {Response}", jsonResponse);
                    throw new JsonException("Invalid JSON response from Zendesk API", ex);
                }
                
                // Extract the view_count object and then the value
                if (!responseData.TryGetProperty("view_count", out var viewCountElement))
                {
                    _logger.LogError("Response missing 'view_count' property when fetching view ticket count: {Response}", jsonResponse);
                    throw new InvalidOperationException("API response missing required 'view_count' property");
                }
                
                if (!viewCountElement.TryGetProperty("value", out var valueElement))
                {
                    _logger.LogError("Response missing 'value' property in view_count when fetching view ticket count: {Response}", jsonResponse);
                    throw new InvalidOperationException("API response missing required 'value' property in view_count");
                }
                
                // Handle null value (system loading data)
                if (valueElement.ValueKind == JsonValueKind.Null)
                {
                    _logger.LogWarning("View ticket count is null (system may be loading data), returning 0");
                    return 0;
                }
                
                if (valueElement.ValueKind != JsonValueKind.Number)
                {
                    _logger.LogError("Invalid 'value' property type when fetching view ticket count. Expected number, got: {ValueKind}", valueElement.ValueKind);
                    throw new InvalidOperationException("API response 'value' property is not a number");
                }
                
                var count = valueElement.GetInt32();
                
                // Check if the count is fresh
                var isFresh = true;
                if (viewCountElement.TryGetProperty("fresh", out var freshElement) && freshElement.ValueKind == JsonValueKind.False)
                {
                    isFresh = false;
                    _logger.LogInformation("Retrieved view ticket count: {Count} (cached data, may not be current)", count);
                }
                else
                {
                    _logger.LogInformation("Successfully retrieved view ticket count: {Count}", count);
                }
                
                return count;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP request failed when fetching view ticket count");
                throw;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogError(ex, "Request timeout when fetching view ticket count");
                throw new HttpRequestException("Request timeout when fetching view ticket count", ex);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "Request was cancelled when fetching view ticket count");
                throw new HttpRequestException("Request was cancelled when fetching view ticket count", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error when fetching view ticket count");
                throw;
            }
        }

        /// <summary>
        /// Gets the count of open tickets per agent from a specific Zendesk view.
        /// This method fetches actual ticket data from the view and groups by assignee.
        /// </summary>
        /// <param name="viewId">The ID of the view to get tickets from (default: 360077881353)</param>
        /// <returns>
        /// A task that represents the asynchronous operation. The task result contains 
        /// a dictionary where keys are agent IDs and values are ticket counts.
        /// </returns>
        /// <remarks>
        /// This method fetches the actual tickets from the view and counts them by assignee.
        /// Unassigned tickets are included with a key of 0.
        /// The method handles pagination automatically to get all tickets from the view.
        /// </remarks>
        public async Task<Dictionary<long, int>> GetViewTicketsPerAgentAsync(long viewId = 360077881353)
        {
            _logger.LogInformation("Fetching tickets per agent for view {ViewId}", viewId);
            
            var ticketCounts = new Dictionary<long, int>();
            var url = $"https://{_subdomain}.zendesk.com/api/v2/views/{viewId}/tickets.json?per_page=100";
            var currentPage = 1;
            const int maxPages = 50; // Safety limit
            
            try
            {
                do
                {
                    _logger.LogDebug("Fetching page {Page} of tickets from view {ViewId}", currentPage, viewId);
                    
                    var response = await _httpClient.GetAsync(url);
                    
                    // Handle rate limiting
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60;
                        _logger.LogWarning("Rate limited when fetching view tickets. Retry after {RetryAfter} seconds", retryAfter);
                        throw new HttpRequestException($"Rate limited. Retry after {retryAfter} seconds", null, response.StatusCode);
                    }
                    
                    response.EnsureSuccessStatusCode();
                    var jsonResponse = await response.Content.ReadAsStringAsync();
                    
                    var responseData = JsonSerializer.Deserialize<JsonElement>(jsonResponse);
                    
                    if (!responseData.TryGetProperty("tickets", out var ticketsElement))
                    {
                        _logger.LogError("Response missing 'tickets' property when fetching view tickets");
                        throw new InvalidOperationException("API response missing required 'tickets' property");
                    }
                    
                    // Count tickets by assignee
                    foreach (var ticket in ticketsElement.EnumerateArray())
                    {
                        var assigneeId = 0L; // Default to 0 for unassigned
                        
                        if (ticket.TryGetProperty("assignee_id", out var assigneeElement) && 
                            assigneeElement.ValueKind == JsonValueKind.Number)
                        {
                            assigneeId = assigneeElement.GetInt64();
                        }
                        
                        ticketCounts[assigneeId] = ticketCounts.GetValueOrDefault(assigneeId) + 1;
                    }
                    
                    _logger.LogDebug("Page {Page}: Processed {Count} tickets", currentPage, ticketsElement.GetArrayLength());
                    
                    // Check for next page
                    url = null;
                    if (responseData.TryGetProperty("next_page", out var nextPageElement) && 
                        nextPageElement.ValueKind != JsonValueKind.Null)
                    {
                        url = nextPageElement.GetString();
                        currentPage++;
                        
                        if (currentPage > maxPages)
                        {
                            _logger.LogWarning("Reached maximum page limit ({MaxPages}) for view tickets, stopping pagination for safety", maxPages);
                            break;
                        }
                    }
                }
                while (!string.IsNullOrEmpty(url));
                
                _logger.LogInformation("Successfully retrieved tickets per agent for view {ViewId} across {Pages} pages. Agents with tickets: {AgentCount}", 
                    viewId, currentPage, ticketCounts.Count);
                
                return ticketCounts;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP request failed when fetching view tickets per agent");
                throw;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogError(ex, "Request timeout when fetching view tickets per agent");
                throw new HttpRequestException("Request timeout when fetching view tickets per agent", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error when fetching view tickets per agent");
                throw;
            }
        }
    }
}
