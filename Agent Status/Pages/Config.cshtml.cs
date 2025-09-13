using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Agent_Status.Models;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent_Status.Pages
{
    [IgnoreAntiforgeryToken]
    public class ConfigModel : PageModel
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<ConfigModel> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly ZendeskTalkService _zendeskService;

        [BindProperty]
        public ZendeskConfigurationModel ZendeskConfig { get; set; } = new();

        public ZendeskConfigurationModel? CurrentConfig { get; set; }

        public List<ZendeskAgent>? FetchedAgents { get; set; }

        [TempData]
        public bool IsConfigurationSaved { get; set; }

        public ConfigModel(IConfiguration configuration, ILogger<ConfigModel> logger, IWebHostEnvironment environment, ZendeskTalkService zendeskService)
        {
            _configuration = configuration;
            _logger = logger;
            _environment = environment;
            _zendeskService = zendeskService;
        }

        public void OnGet()
        {
            LoadCurrentConfiguration();
            
            // Load stored agents and display them if they exist
            if (CurrentConfig?.Agents?.Any() == true)
            {
                FetchedAgents = CurrentConfig.Agents.Values
                    .Select(agent => new ZendeskAgent
                    {
                        Id = agent.Id,
                        Name = agent.Name,
                        Email = agent.Email,
                        Active = agent.Active,
                        Role = agent.Role,
                        IsSelected = agent.IsSelected
                    })
                    .OrderBy(a => a.Name)
                    .ToList();
                    
                _logger.LogInformation("Loaded {Count} stored agents for display", FetchedAgents.Count);
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // Debug: Log all form values
            _logger.LogInformation("Form submission received. Debugging form values:");
            _logger.LogInformation("Request.Form.Count: {Count}", Request.Form.Count);
            _logger.LogInformation("Request.ContentLength: {ContentLength}", Request.ContentLength);
            _logger.LogInformation("Request.ContentType: {ContentType}", Request.ContentType);
            
            foreach (var formKey in Request.Form.Keys)
            {
                _logger.LogInformation("Form key: {Key}, Value: {Value}", formKey, Request.Form[formKey]);
            }
            
            // Debug: Log ZendeskConfig values
            _logger.LogInformation("ZendeskConfig values: Subdomain='{Subdomain}', Email='{Email}', ApiToken='{ApiTokenLength} chars'", 
                ZendeskConfig.Subdomain, ZendeskConfig.Email, ZendeskConfig.ApiToken?.Length ?? 0);

            // Ensure SelectedAgents is not null to avoid validation issues
            if (ZendeskConfig.SelectedAgents == null)
            {
                ZendeskConfig.SelectedAgents = new Dictionary<long, bool>();
            }

            if (!ModelState.IsValid)
            {
                // Log validation errors for debugging
                foreach (var modelError in ModelState)
                {
                    foreach (var error in modelError.Value.Errors)
                    {
                        _logger.LogWarning("Validation error for {PropertyName}: {ErrorMessage}", 
                            modelError.Key, error.ErrorMessage);
                    }
                }
                
                LoadCurrentConfiguration();
                return Page();
            }

            try
            {
                await SaveConfigurationAsync();
                IsConfigurationSaved = true;
                _logger.LogInformation("Zendesk configuration updated successfully");
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving Zendesk configuration");
                ModelState.AddModelError("", "An error occurred while saving the configuration. Please try again.");
                LoadCurrentConfiguration();
                return Page();
            }
        }

        public async Task<IActionResult> OnPostFetchAgentsAsync()
        {
            _logger.LogInformation("OnPostFetchAgentsAsync called");
            
            // Debug: Log all form values
            _logger.LogInformation("Request.Form.Count: {Count}", Request.Form.Count);
            foreach (var formKey in Request.Form.Keys)
            {
                _logger.LogInformation("Form key: {Key}, Value: {Value}", formKey, Request.Form[formKey]);
            }
            
            try
            {
                // Read values directly from form since model binding might not work for handler methods
                var subdomain = Request.Form["ZendeskConfig.Subdomain"].ToString();
                var email = Request.Form["ZendeskConfig.Email"].ToString();
                var apiToken = Request.Form["ZendeskConfig.ApiToken"].ToString();
                
                // Fallback to configuration if form values are empty
                if (string.IsNullOrWhiteSpace(subdomain)) subdomain = _configuration["Zendesk:Subdomain"];
                if (string.IsNullOrWhiteSpace(email)) email = _configuration["Zendesk:Email"];
                if (string.IsNullOrWhiteSpace(apiToken)) apiToken = _configuration["Zendesk:ApiToken"];

                _logger.LogInformation("Using values: Subdomain='{Subdomain}', Email='{Email}', HasApiToken={HasToken}", 
                    subdomain, email, !string.IsNullOrWhiteSpace(apiToken));

                if (string.IsNullOrWhiteSpace(subdomain) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(apiToken))
                {
                    _logger.LogWarning("Zendesk configuration is incomplete");
                    return new JsonResult(new { success = false, message = "Zendesk configuration is incomplete. Please configure connection settings first." });
                }

                // Create a temporary HTTP client with the form credentials
                _logger.LogInformation("Fetching agents from Zendesk API using form credentials");
                
                using var httpClient = new HttpClient();
                var authString = $"{email}/token:{apiToken}";
                var authBytes = System.Text.Encoding.UTF8.GetBytes(authString);
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(authBytes));

                var allUsers = new List<ZendeskUserApiResponse>();
                var url = $"https://{subdomain}.zendesk.com/api/v2/users.json?role[]=agent&role[]=admin&per_page=100";
                var currentPage = 1;
                const int maxPages = 200; // Safety limit to prevent infinite loops
                
                _logger.LogInformation("Starting paginated fetch from: {Url} (filtering for agents and admins)", url);
                
                do
                {
                    _logger.LogInformation("Fetching page {Page}", currentPage);
                    
                    try
                    {
                        var response = await httpClient.GetAsync(url);
                        
                        // Handle the case where we've gone beyond available pages
                        if (!response.IsSuccessStatusCode)
                        {
                            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                            {
                                _logger.LogInformation("Reached end of pagination at page {Page} (400 Bad Request)", currentPage);
                                break;
                            }
                            response.EnsureSuccessStatusCode(); // This will throw for other error codes
                        }
                        
                        var usersJson = await response.Content.ReadAsStringAsync();
                        
                        _logger.LogInformation("Successfully retrieved page {Page} JSON response", currentPage);
                        
                        var usersResponse = JsonSerializer.Deserialize<ZendeskUsersResponse>(usersJson);

                        if (usersResponse?.Users == null)
                        {
                            _logger.LogWarning("No users found in page {Page} response or deserialization failed", currentPage);
                            break;
                        }

                        _logger.LogInformation("Page {Page}: Retrieved {Count} users", currentPage, usersResponse.Users.Count);
                        allUsers.AddRange(usersResponse.Users);
                        
                        // If this page has fewer users than the page size, we're likely at the end
                        if (usersResponse.Users.Count < 100)
                        {
                            _logger.LogInformation("Page {Page} contained fewer than 100 users ({Count}), assuming end of data", currentPage, usersResponse.Users.Count);
                            break;
                        }
                        
                        // Move to next page
                        url = usersResponse.NextPage;
                        currentPage++;
                        
                        if (string.IsNullOrEmpty(url))
                        {
                            _logger.LogInformation("No next_page URL provided, pagination complete");
                            break;
                        }
                        
                        // Safety check to prevent excessive pagination
                        if (currentPage > maxPages)
                        {
                            _logger.LogWarning("Reached maximum page limit ({MaxPages}), stopping pagination for safety", maxPages);
                            break;
                        }
                    }
                    catch (HttpRequestException ex) when (ex.Message.Contains("400"))
                    {
                        _logger.LogInformation("Reached end of pagination at page {Page} due to 400 error: {Error}", currentPage, ex.Message);
                        break;
                    }
                }
                while (!string.IsNullOrEmpty(url));

                _logger.LogInformation("Completed pagination. Total users fetched: {Count}", allUsers.Count);
                
                // Log the first few users to see their roles
                for (int i = 0; i < Math.Min(3, allUsers.Count); i++)
                {
                    var user = allUsers[i];
                    _logger.LogInformation("User {Index}: Id={Id}, Name='{Name}', Role='{Role}', RoleType={RoleType}, Active={Active}", 
                        i + 1, user.Id, user.Name, user.Role, user.RoleType, user.Active);
                }

                var agents = allUsers
                    .Where(u => (u.Role == "agent" || u.Role == "admin") && u.RoleType != 1) // Exclude light agents (role_type=1)
                    .Select(u => new ZendeskAgent
                    {
                        Id = u.Id,
                        Name = u.Name ?? "Unknown",
                        Email = u.Email ?? "No email",
                        Active = u.Active,
                        Role = u.Role ?? "unknown"
                    })
                    .OrderBy(a => a.Name)
                    .ToList();

                _logger.LogInformation("Processed {Count} users (agents and admins, excluding light agents) from {TotalCount} total users", 
                    agents.Count, allUsers.Count);
                
                // Log some examples of what roles we're seeing in all users
                var roleGroups = allUsers.GroupBy(u => new { u.Role, u.RoleType }).ToList();
                foreach (var roleGroup in roleGroups)
                {
                    var roleTypeName = roleGroup.Key.RoleType switch 
                    {
                        0 => "custom agent",
                        1 => "light agent", 
                        2 => "chat agent",
                        3 => "chat agent (contributor)",
                        4 => "admin",
                        5 => "billing admin",
                        _ => $"unknown ({roleGroup.Key.RoleType})"
                    };
                    _logger.LogInformation("Found {Count} users with role '{Role}' and role_type {RoleType} ({RoleTypeName})", 
                        roleGroup.Count(), roleGroup.Key.Role ?? "null", roleGroup.Key.RoleType, roleTypeName);
                }

                FetchedAgents = agents;
                
                _logger.LogInformation("Successfully processed {Count} agents", agents.Count);

                // Sync the agent list with stored configuration
                await SyncAgentListAsync(agents);

                return new JsonResult(new { success = true, agents = agents });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching agents from Zendesk");
                return new JsonResult(new { success = false, message = $"Error fetching agents: {ex.Message}" });
            }
        }

        public async Task<IActionResult> OnPostSaveAgentsAsync()
        {
            try
            {
                _logger.LogInformation("OnPostSaveAgentsAsync called");
                
                // Debug: Log all form values
                _logger.LogInformation("Form submission received. Form keys: [{Keys}]", 
                    string.Join(", ", Request.Form.Keys));
                
                // Get the selected agents from the form
                var selectedAgents = new Dictionary<long, bool>();
                
                foreach (var key in Request.Form.Keys.Where(k => k.StartsWith("SelectedAgents[")))
                {
                    // Extract agent ID from key like "SelectedAgents[123]"
                    var agentIdStr = key.Substring("SelectedAgents[".Length).TrimEnd(']');
                    if (long.TryParse(agentIdStr, out long agentId))
                    {
                        var isSelected = Request.Form[key].ToString().Contains("true");
                        selectedAgents[agentId] = isSelected;
                        _logger.LogInformation("Agent {AgentId}: {IsSelected}", agentId, isSelected);
                    }
                }

                _logger.LogInformation("Processed {Count} agent selections", selectedAgents.Count);

                // Load current configuration
                LoadCurrentConfiguration();
                
                if (CurrentConfig != null)
                {
                    // Update both the new Agents structure and legacy SelectedAgents
                    if (CurrentConfig.Agents != null && CurrentConfig.Agents.Any())
                    {
                        // Update IsSelected in the full agent objects
                        foreach (var kvp in selectedAgents)
                        {
                            if (CurrentConfig.Agents.ContainsKey(kvp.Key))
                            {
                                CurrentConfig.Agents[kvp.Key].IsSelected = kvp.Value;
                                _logger.LogDebug("Updated agent {AgentId} selection to {IsSelected}", kvp.Key, kvp.Value);
                            }
                        }
                        
                        // Also ensure any agents not in the form (unchecked) are set to false
                        foreach (var agentConfig in CurrentConfig.Agents.Values)
                        {
                            if (!selectedAgents.ContainsKey(agentConfig.Id))
                            {
                                agentConfig.IsSelected = false;
                                _logger.LogDebug("Set unchecked agent {AgentId} to not selected", agentConfig.Id);
                            }
                        }
                    }
                    
                    // Update legacy SelectedAgents for backwards compatibility
                    CurrentConfig.SelectedAgents = selectedAgents;
                    
                    ZendeskConfig = CurrentConfig;
                    await SaveConfigurationAsync();
                    
                    _logger.LogInformation("Agent configuration saved successfully");
                }

                IsConfigurationSaved = true;
                _logger.LogInformation("Agent selection updated successfully. {Count} agents selected.", 
                    selectedAgents.Count(x => x.Value));
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving agent selection");
                ModelState.AddModelError("", "An error occurred while saving agent selection. Please try again.");
                LoadCurrentConfiguration();
                return Page();
            }
        }

        public async Task<IActionResult> OnPostTestConnectionAsync()
        {
            try
            {
                // Get form values
                var subdomain = Request.Form["subdomain"].ToString();
                var email = Request.Form["email"].ToString();
                var apiToken = Request.Form["apiToken"].ToString();
                
                // Validate the input
                if (string.IsNullOrWhiteSpace(subdomain) || 
                    string.IsNullOrWhiteSpace(email) || 
                    string.IsNullOrWhiteSpace(apiToken))
                {
                    return new JsonResult(new { success = false, message = "All fields are required" });
                }

                // Test the connection by making a simple API call
                using var httpClient = new HttpClient();
                var authString = $"{email}/token:{apiToken}";
                var authBytes = System.Text.Encoding.UTF8.GetBytes(authString);
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(authBytes));

                var testUrl = $"https://{subdomain}.zendesk.com/api/v2/users/me.json";
                var response = await httpClient.GetAsync(testUrl);

                if (response.IsSuccessStatusCode)
                {
                    return new JsonResult(new { success = true, message = "Connection successful" });
                }
                else
                {
                    return new JsonResult(new { success = false, message = $"HTTP {response.StatusCode}: {response.ReasonPhrase}" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing Zendesk connection");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        private void LoadCurrentConfiguration()
        {
            // Load selected agents from configuration
            var selectedAgentsConfig = _configuration.GetSection("Zendesk:SelectedAgents");
            var selectedAgents = new Dictionary<long, bool>();
            
            if (selectedAgentsConfig.Exists())
            {
                foreach (var child in selectedAgentsConfig.GetChildren())
                {
                    if (long.TryParse(child.Key, out long agentId))
                    {
                        selectedAgents[agentId] = child.Get<bool>();
                    }
                }
            }

            // Load full agents dictionary from configuration
            var agentsConfig = _configuration.GetSection("Zendesk:Agents");
            var agents = new Dictionary<long, ZendeskAgentConfig>();
            
            if (agentsConfig.Exists())
            {
                foreach (var child in agentsConfig.GetChildren())
                {
                    if (long.TryParse(child.Key, out long agentId))
                    {
                        var agentConfig = child.Get<ZendeskAgentConfig>();
                        if (agentConfig != null)
                        {
                            agents[agentId] = agentConfig;
                        }
                    }
                }
            }

            CurrentConfig = new ZendeskConfigurationModel
            {
                Subdomain = _configuration["Zendesk:Subdomain"] ?? string.Empty,
                Email = _configuration["Zendesk:Email"] ?? string.Empty,
                ApiToken = _configuration["Zendesk:ApiToken"] ?? string.Empty,
                SelectedAgents = selectedAgents,
                Agents = agents
            };

            // If form wasn't posted, populate with current values
            if (string.IsNullOrEmpty(ZendeskConfig.Subdomain))
            {
                ZendeskConfig = new ZendeskConfigurationModel
                {
                    Subdomain = CurrentConfig.Subdomain,
                    Email = CurrentConfig.Email,
                    ApiToken = CurrentConfig.ApiToken,
                    SelectedAgents = CurrentConfig.SelectedAgents,
                    Agents = CurrentConfig.Agents
                };
            }
        }

        private async Task SaveConfigurationAsync()
        {
            var appSettingsPath = Path.Combine(_environment.ContentRootPath, "appsettings.json");
            
            string jsonContent;
            if (System.IO.File.Exists(appSettingsPath))
            {
                jsonContent = await System.IO.File.ReadAllTextAsync(appSettingsPath);
            }
            else
            {
                jsonContent = "{}";
            }

            var jsonDocument = JsonDocument.Parse(jsonContent);
            var root = jsonDocument.RootElement;

            var updatedJson = new Dictionary<string, object>();

            // Copy existing properties
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name != "Zendesk")
                {
                    updatedJson[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText()) ?? new object();
                }
            }

            // Add/update Zendesk configuration
            _logger.LogInformation("SaveConfigurationAsync: Saving {AgentCount} agents to configuration", 
                ZendeskConfig.Agents?.Count ?? 0);
            updatedJson["Zendesk"] = new
            {
                Subdomain = ZendeskConfig.Subdomain,
                Email = ZendeskConfig.Email,
                ApiToken = ZendeskConfig.ApiToken,
                Agents = ZendeskConfig.Agents ?? new Dictionary<long, ZendeskAgentConfig>(),
                SelectedAgents = ZendeskConfig.SelectedAgents
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var updatedJsonString = JsonSerializer.Serialize(updatedJson, options);
            _logger.LogInformation("SaveConfigurationAsync: Writing to file {Path}", appSettingsPath);
            await System.IO.File.WriteAllTextAsync(appSettingsPath, updatedJsonString);
            _logger.LogInformation("SaveConfigurationAsync: File write completed");
        }

        /// <summary>
        /// Syncs the fetched agents list with the stored configuration.
        /// Adds new agents, removes deleted agents, preserves existing selections.
        /// </summary>
        private async Task SyncAgentListAsync(List<ZendeskAgent> fetchedAgents)
        {
            try
            {
                _logger.LogInformation("Starting agent list sync with {Count} fetched agents", fetchedAgents.Count);
                
                // Load current configuration to get existing agent data
                LoadCurrentConfiguration();
                var currentAgents = CurrentConfig?.Agents ?? new Dictionary<long, ZendeskAgentConfig>();
                var existingSelections = CurrentConfig?.SelectedAgents ?? new Dictionary<long, bool>();
                
                // Create new agent configuration dictionary
                var updatedAgents = new Dictionary<long, ZendeskAgentConfig>();
                var addedCount = 0;
                var updatedCount = 0;
                var preservedSelectionCount = 0;
                
                // Process each fetched agent
                foreach (var fetchedAgent in fetchedAgents)
                {
                    var agentConfig = new ZendeskAgentConfig
                    {
                        Id = fetchedAgent.Id,
                        Name = fetchedAgent.Name,
                        Email = fetchedAgent.Email,
                        Active = fetchedAgent.Active,
                        Role = fetchedAgent.Role,
                        LastUpdated = DateTime.UtcNow
                    };

                    // Preserve existing selection state or default to false for new agents
                    if (currentAgents.ContainsKey(fetchedAgent.Id))
                    {
                        // Existing agent - preserve selection and update info
                        agentConfig.IsSelected = currentAgents[fetchedAgent.Id].IsSelected;
                        updatedCount++;
                        preservedSelectionCount++;
                        _logger.LogDebug("Updated existing agent {AgentId} ({Name}), selection preserved: {IsSelected}", 
                            fetchedAgent.Id, fetchedAgent.Name, agentConfig.IsSelected);
                    }
                    else if (existingSelections.ContainsKey(fetchedAgent.Id))
                    {
                        // Legacy: check old SelectedAgents dictionary
                        agentConfig.IsSelected = existingSelections[fetchedAgent.Id];
                        addedCount++;
                        preservedSelectionCount++;
                        _logger.LogDebug("Migrated agent {AgentId} ({Name}) from legacy format, selection: {IsSelected}", 
                            fetchedAgent.Id, fetchedAgent.Name, agentConfig.IsSelected);
                    }
                    else
                    {
                        // New agent - default to not selected
                        agentConfig.IsSelected = false;
                        addedCount++;
                        _logger.LogDebug("Added new agent {AgentId} ({Name}), defaulted to not selected", 
                            fetchedAgent.Id, fetchedAgent.Name);
                    }

                    updatedAgents[fetchedAgent.Id] = agentConfig;
                }

                // Calculate removed agents
                var removedCount = currentAgents.Keys.Except(updatedAgents.Keys).Count();
                var removedAgents = currentAgents.Keys.Except(updatedAgents.Keys).ToList();
                
                if (removedAgents.Any())
                {
                    _logger.LogInformation("Removing {Count} agents that no longer exist: [{AgentIds}]", 
                        removedCount, string.Join(", ", removedAgents));
                }

                // Update the configuration
                if (CurrentConfig != null)
                {
                    CurrentConfig.Agents = updatedAgents;
                    // Keep SelectedAgents for backwards compatibility but sync it
                    CurrentConfig.SelectedAgents = updatedAgents.ToDictionary(
                        kvp => kvp.Key, 
                        kvp => kvp.Value.IsSelected);
                    
                    // IMPORTANT: Update ZendeskConfig to match CurrentConfig
                    ZendeskConfig = CurrentConfig;
                    
                    _logger.LogInformation("About to save configuration with {AgentCount} agents", updatedAgents.Count);
                    _logger.LogInformation("CurrentConfig.Agents has {Count} entries", CurrentConfig.Agents?.Count ?? 0);
                    _logger.LogInformation("ZendeskConfig.Agents has {Count} entries", ZendeskConfig.Agents?.Count ?? 0);
                    
                    await SaveConfigurationAsync();
                    
                    _logger.LogInformation("Configuration save completed");
                }

                _logger.LogInformation("Agent sync completed: {Added} added, {Updated} updated, {Removed} removed, {PreservedSelections} selections preserved", 
                    addedCount, updatedCount, removedCount, preservedSelectionCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during agent list sync");
                throw;
            }
        }
    }

    public class TestConnectionRequest
    {
        public string Subdomain { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string ApiToken { get; set; } = string.Empty;
    }

    public class ZendeskUsersResponse
    {
        [JsonPropertyName("users")]
        public List<ZendeskUserApiResponse> Users { get; set; } = new();
        
        [JsonPropertyName("next_page")]
        public string? NextPage { get; set; }
        
        [JsonPropertyName("previous_page")]
        public string? PreviousPage { get; set; }
        
        [JsonPropertyName("count")]
        public int Count { get; set; }
    }

    public class ZendeskUserApiResponse
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }  // Changed from int to long for large IDs
        
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        
        [JsonPropertyName("email")]
        public string? Email { get; set; }
        
        [JsonPropertyName("active")]
        public bool Active { get; set; }
        
        [JsonPropertyName("role")]
        public string? Role { get; set; }
        
        [JsonPropertyName("role_type")]
        public int? RoleType { get; set; }
        
        // Additional properties that might be useful
        [JsonPropertyName("user_fields")]
        public Dictionary<string, object>? UserFields { get; set; }
        
        [JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }
    }
}