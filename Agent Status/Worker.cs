
using Prometheus;
using Serilog;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Agent_Status;

/// <summary>
/// Background service that monitors Zendesk agent availability and updates metrics.
/// Implements configuration validation and exponential backoff for resilient operation.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly ZendeskTalkService _zendeskService;
    
    // Configuration validation state
    private bool _isConfigurationValid = false;
    private DateTime _lastConfigurationCheck = DateTime.MinValue;
    private readonly TimeSpan _configurationCheckInterval = TimeSpan.FromMinutes(5);
    
    // Backoff state for failed API calls
    private int _consecutiveFailures = 0;
    private DateTime _lastFailureTime = DateTime.MinValue;
    private readonly int _maxBackoffDelaySeconds = 300; // 5 minutes maximum backoff
    private readonly int _baseBackoffDelaySeconds = 10; // Initial backoff delay
    
    // Agent state tracking for change detection
    private readonly Dictionary<long, string> _previousAgentStates = new();
    private readonly Dictionary<long, string> _previousCallStatuses = new();
    
    // Numeric mappings for agent states
    // Agent States: 0=offline, 1=away, 2=transfers_only, 3=online, -1=unknown
    // Call Status: 0=null (no call), 1=on_call, 2=wrap_up, -1=unknown
    
    // Metrics
    private static readonly Counter WorkerRunCounter = Metrics.CreateCounter("worker_run_total", "Number of times the worker loop has run.");
    private static readonly Counter ApiCallCounter = Metrics.CreateCounter("zendesk_api_calls_total", "Total number of Zendesk API calls made.", new[] { "status" });
    private static readonly Gauge BackoffDelayGauge = Metrics.CreateGauge("zendesk_backoff_delay_seconds", "Current backoff delay in seconds.");
    private static readonly Gauge ConfigurationValidGauge = Metrics.CreateGauge("zendesk_configuration_valid", "Whether Zendesk configuration is valid (1=valid, 0=invalid).");
    
    // Agent availability metrics
    private static readonly Gauge AgentStateGauge = Metrics.CreateGauge("zendesk_agent_state", 
        "Current agent state (0=offline, 1=away, 2=transfers_only, 3=online)", new[] { "agent_id", "agent_name" });
    private static readonly Gauge AgentCallStatusGauge = Metrics.CreateGauge("zendesk_agent_call_status", 
        "Current call status (0=null/no_call, 1=on_call, 2=wrap_up)", new[] { "agent_id", "agent_name" });
    private static readonly Counter AgentStateChangeCounter = Metrics.CreateCounter("zendesk_agent_state_changes_total", 
        "Total number of agent state changes", new[] { "agent_id", "agent_name" });
    private static readonly Counter AgentCallStatusChangeCounter = Metrics.CreateCounter("zendesk_agent_call_status_changes_total", 
        "Total number of agent call status changes", new[] { "agent_id", "agent_name" });
    private static readonly Gauge LastAvailabilityUpdateGauge = Metrics.CreateGauge("zendesk_agent_last_update_timestamp", 
        "Unix timestamp of last availability update", new[] { "agent_id", "agent_name" });
    
    // Ticket metrics
    private static readonly Gauge OpenTicketsCountGauge = Metrics.CreateGauge("zendesk_view_tickets_total", 
        "Total number of tickets in Zendesk view 360077881353");
    private static readonly Counter TicketCountApiCallCounter = Metrics.CreateCounter("zendesk_view_ticket_count_api_calls_total", 
        "Total number of view ticket count API calls made", new[] { "status" });

    public Worker(ILogger<Worker> logger, IConfiguration configuration, ZendeskTalkService zendeskService)
    {
        _logger = logger;
        _configuration = configuration;
        _zendeskService = zendeskService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker service starting");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            WorkerRunCounter.Inc();
            
            try
            {
                // Check configuration validity periodically
                ValidateConfiguration();
                
                if (!_isConfigurationValid)
                {
                    _logger.LogWarning("Zendesk configuration is not valid. Skipping API calls. Next check in {Interval}",
                        _configurationCheckInterval);
                    ConfigurationValidGauge.Set(0);
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Check every minute when config is invalid
                    continue;
                }
                
                ConfigurationValidGauge.Set(1);
                
                // Check if we should wait due to previous failures (backoff logic)
                var backoffDelay = CalculateBackoffDelay();
                if (backoffDelay > TimeSpan.Zero)
                {
                    _logger.LogInformation("Waiting {BackoffDelay} seconds before next API attempt due to previous failures. Consecutive failures: {Failures}",
                        backoffDelay.TotalSeconds, _consecutiveFailures);
                    BackoffDelayGauge.Set(backoffDelay.TotalSeconds);
                    await Task.Delay(backoffDelay, stoppingToken);
                    continue;
                }
                
                BackoffDelayGauge.Set(0);
                
                // Perform the actual work - monitoring agent availability
                await PerformZendeskMonitoringAsync();
                
                // Reset failure count on successful operation
                if (_consecutiveFailures > 0)
                {
                    _logger.LogInformation("API call succeeded. Resetting failure count from {PreviousFailures} to 0", _consecutiveFailures);
                    _consecutiveFailures = 0;
                }
                
                // Normal delay between successful operations
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); // Check every 15 seconds when everything is working
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in worker loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); // Short delay on unexpected errors
            }
        }
        
        _logger.LogInformation("Worker service stopping");
    }

    /// <summary>
    /// Validates that all required Zendesk configuration values are present and not default values.
    /// Only checks periodically to avoid excessive configuration reads.
    /// </summary>
    private void ValidateConfiguration()
    {
        // Only check configuration periodically to avoid excessive reads
        if (_isConfigurationValid && DateTime.UtcNow - _lastConfigurationCheck < _configurationCheckInterval)
        {
            return;
        }

        _lastConfigurationCheck = DateTime.UtcNow;
        
        var subdomain = _configuration["Zendesk:Subdomain"];
        var email = _configuration["Zendesk:Email"];
        var apiToken = _configuration["Zendesk:ApiToken"];

        // Check if configuration values are present and not default placeholder values
        var isValid = !string.IsNullOrWhiteSpace(subdomain) &&
                     !string.IsNullOrWhiteSpace(email) &&
                     !string.IsNullOrWhiteSpace(apiToken) &&
                     subdomain != "your_subdomain" &&
                     email != "your_email@example.com" &&
                     apiToken != "your_api_token";

        if (isValid != _isConfigurationValid)
        {
            if (isValid)
            {
                _logger.LogInformation("Zendesk configuration is now valid. Resuming API monitoring");
            }
            else
            {
                _logger.LogWarning("Zendesk configuration is invalid or contains default values. " +
                                 "Please configure valid values via the /config page. " +
                                 "Subdomain: {SubdomainValid}, Email: {EmailValid}, ApiToken: {ApiTokenValid}",
                                 !string.IsNullOrWhiteSpace(subdomain) && subdomain != "your_subdomain",
                                 !string.IsNullOrWhiteSpace(email) && email != "your_email@example.com",
                                 !string.IsNullOrWhiteSpace(apiToken) && apiToken != "your_api_token");
            }
        }

        _isConfigurationValid = isValid;
    }

    /// <summary>
    /// Calculates the backoff delay based on consecutive failures using exponential backoff.
    /// Formula: min(base_delay * 2^failures, max_delay)
    /// </summary>
    private TimeSpan CalculateBackoffDelay()
    {
        if (_consecutiveFailures == 0)
        {
            return TimeSpan.Zero;
        }

        // Exponential backoff: base delay * 2^failures, capped at maximum
        var delaySeconds = Math.Min(
            _baseBackoffDelaySeconds * Math.Pow(2, _consecutiveFailures - 1),
            _maxBackoffDelaySeconds);

        return TimeSpan.FromSeconds(delaySeconds);
    }

    /// <summary>
    /// Performs the actual Zendesk monitoring work - checking agent availability.
    /// Implements proper error handling and failure tracking for backoff logic.
    /// </summary>
    /// <summary>
    /// Performs the main monitoring work including agent availability and open tickets count.
    /// Handles errors gracefully and maintains metrics for each operation.
    /// </summary>
    private async Task PerformZendeskMonitoringAsync()
    {
        // Get the list of agents to monitor from configuration
        var selectedAgents = GetSelectedAgentsFromConfiguration();
        
        if (!selectedAgents.Any())
        {
            _logger.LogInformation("No agents are configured for monitoring. Please configure agents via the /config page.");
            return;
        }
        
        _logger.LogDebug("Checking availability for {AgentCount} configured agents", selectedAgents.Count);
        
        var successCount = 0;
        var failureCount = 0;
        
        // Monitor agent availability
        foreach (var agentId in selectedAgents)
        {
            try
            {
                _logger.LogDebug("Checking availability for agent {AgentId}", agentId);
                
                var availability = await _zendeskService.GetAgentAvailabilityAsync(agentId);
                
                ApiCallCounter.WithLabels("success").Inc();
                successCount++;
                
                _logger.LogDebug("Successfully retrieved availability for agent {AgentId}: {Availability}", 
                               agentId, availability);
                
                // Parse and update Prometheus metrics
                await UpdateAgentAvailabilityMetrics(agentId, availability);
            }
            catch (Exception ex)
            {
                failureCount++;
                _logger.LogWarning(ex, "Failed to get availability for agent {AgentId}", agentId);
                // Continue with other agents even if one fails
            }
        }
        
        // Monitor open tickets count
        await MonitorViewTicketsCountAsync();
        
        _logger.LogInformation("Completed monitoring cycle: {SuccessCount} successful, {FailureCount} failed out of {TotalCount} agents", 
                             successCount, failureCount, selectedAgents.Count);
        
        // Only throw if ALL agents failed - this maintains the backoff logic for complete failures
        if (failureCount > 0 && successCount == 0)
        {
            throw new InvalidOperationException($"Failed to retrieve availability for all {failureCount} configured agents");
        }
    }

    /// <summary>
    /// Monitors the count of tickets in a specific Zendesk view and updates Prometheus metrics.
    /// Handles errors gracefully to avoid disrupting agent monitoring.
    /// Uses view 360077881353 by default.
    /// </summary>
    private async Task MonitorViewTicketsCountAsync()
    {
        try
        {
            _logger.LogInformation("Fetching tickets count from view 360077881353");
            
            var viewTicketsCount = await _zendeskService.GetViewTicketsCountAsync();
            
            // Update Prometheus metrics
            OpenTicketsCountGauge.Set(viewTicketsCount);
            TicketCountApiCallCounter.WithLabels("success").Inc();
            
            _logger.LogDebug("Successfully updated view tickets count metric: {Count}", viewTicketsCount);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("Rate limited"))
        {
            // Log rate limiting but don't fail the entire monitoring cycle
            _logger.LogWarning("Rate limited when fetching view tickets count: {Message}", ex.Message);
            TicketCountApiCallCounter.WithLabels("rate_limited").Inc();
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("Authentication failed"))
        {
            // Log authentication issues but don't fail the entire monitoring cycle
            _logger.LogError("Authentication failed when fetching view tickets count: {Message}", ex.Message);
            TicketCountApiCallCounter.WithLabels("auth_failed").Inc();
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("Access forbidden"))
        {
            // Log authorization issues but don't fail the entire monitoring cycle
            _logger.LogError("Access forbidden when fetching view tickets count: {Message}", ex.Message);
            TicketCountApiCallCounter.WithLabels("forbidden").Inc();
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("not found"))
        {
            // Log view not found issues but don't fail the entire monitoring cycle
            _logger.LogError("View not found when fetching view tickets count: {Message}", ex.Message);
            TicketCountApiCallCounter.WithLabels("not_found").Inc();
        }
        catch (HttpRequestException ex)
        {
            // Log other HTTP issues but don't fail the entire monitoring cycle
            _logger.LogWarning(ex, "HTTP error when fetching view tickets count");
            TicketCountApiCallCounter.WithLabels("http_error").Inc();
        }
        catch (JsonException ex)
        {
            // Log JSON parsing issues but don't fail the entire monitoring cycle
            _logger.LogWarning(ex, "JSON parsing error when fetching view tickets count");
            TicketCountApiCallCounter.WithLabels("json_error").Inc();
        }
        catch (InvalidOperationException ex)
        {
            // Log API response format issues but don't fail the entire monitoring cycle
            _logger.LogWarning(ex, "Invalid API response when fetching view tickets count");
            TicketCountApiCallCounter.WithLabels("invalid_response").Inc();
        }
        catch (Exception ex)
        {
            // Log unexpected errors but don't fail the entire monitoring cycle
            _logger.LogError(ex, "Unexpected error when fetching view tickets count");
            TicketCountApiCallCounter.WithLabels("error").Inc();
        }
    }

    /// <summary>
    /// Gets the list of agent IDs that are selected (set to true) in the configuration.
    /// Supports both new Agents structure and legacy SelectedAgents for backwards compatibility.
    /// </summary>
    private List<long> GetSelectedAgentsFromConfiguration()
    {
        var selectedAgents = new List<long>();
        
        try
        {
            // Try new Agents structure first by reading the section and parsing manually
            var agentsSection = _configuration.GetSection("Zendesk:Agents");
            
            if (agentsSection.Exists())
            {
                var agentKeys = agentsSection.GetChildren().Select(x => x.Key).ToList();
                
                foreach (var agentKey in agentKeys)
                {
                    if (long.TryParse(agentKey, out var agentId))
                    {
                        var isSelectedValue = _configuration[$"Zendesk:Agents:{agentKey}:IsSelected"];
                        if (bool.TryParse(isSelectedValue, out var isSelected) && isSelected)
                        {
                            selectedAgents.Add(agentId);
                        }
                    }
                }
                
                if (selectedAgents.Any())
                {
                    _logger.LogDebug("Found {Count} selected agents in new Agents configuration: [{AgentIds}]", 
                                   selectedAgents.Count, string.Join(", ", selectedAgents));
                    return selectedAgents;
                }
            }
            
            // Fallback to legacy SelectedAgents structure
            var legacyAgentsSection = _configuration.GetSection("Zendesk:SelectedAgents");
            
            if (legacyAgentsSection.Exists())
            {
                var agentsConfig = legacyAgentsSection.Get<Dictionary<string, bool>>();
                
                if (agentsConfig != null)
                {
                    foreach (var kvp in agentsConfig)
                    {
                        if (kvp.Value && long.TryParse(kvp.Key, out var agentId))
                        {
                            selectedAgents.Add(agentId);
                        }
                    }
                }
                
                _logger.LogDebug("Found {Count} selected agents in legacy SelectedAgents configuration: [{AgentIds}]", 
                               selectedAgents.Count, string.Join(", ", selectedAgents));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading selected agents from configuration");
        }
        
        return selectedAgents;
    }

    /// <summary>
    /// Parses the Zendesk availability response and updates Prometheus metrics accordingly.
    /// </summary>
    private async Task UpdateAgentAvailabilityMetrics(long agentId, string availabilityJson)
    {
        try
        {
            var doc = JsonDocument.Parse(availabilityJson);
            
            // Extract availability data from the response
            string agentState = "unknown";
            string callStatus = "null"; // Default to null (no call)
            string agentName = agentId.ToString(); // Default to ID if name not available
            
            // Try to parse the availability object
            if (doc.RootElement.TryGetProperty("availability", out var availability))
            {
                if (availability.TryGetProperty("agent_state", out var stateElement))
                {
                    agentState = stateElement.GetString() ?? "unknown";
                }
                
                // Try to get call status - Zendesk uses "call_status" field with values: "on_call", "wrap_up", or null
                if (availability.TryGetProperty("call_status", out var callStatusElement))
                {
                    if (callStatusElement.ValueKind == JsonValueKind.Null)
                    {
                        callStatus = "null";
                    }
                    else
                    {
                        callStatus = callStatusElement.GetString() ?? "null";
                    }
                }
            }
            
            // Try to get agent name from a separate call if needed (optional enhancement)
            agentName = await GetAgentNameAsync(agentId) ?? agentId.ToString();
            
            // Track state changes
            if (_previousAgentStates.TryGetValue(agentId, out var previousState) && previousState != agentState)
            {
                AgentStateChangeCounter.WithLabels(agentId.ToString(), agentName).Inc();
                _logger.LogInformation("Agent {AgentId} ({AgentName}) state changed from {PreviousState} to {NewState}", 
                                     agentId, agentName, previousState, agentState);
            }
            
            // Track call status changes
            if (_previousCallStatuses.TryGetValue(agentId, out var previousCallStatus) && previousCallStatus != callStatus)
            {
                AgentCallStatusChangeCounter.WithLabels(agentId.ToString(), agentName).Inc();
                _logger.LogInformation("Agent {AgentId} ({AgentName}) call status changed from {PreviousStatus} to {NewStatus}", 
                                     agentId, agentName, previousCallStatus, callStatus);
            }
            
            _previousAgentStates[agentId] = agentState;
            _previousCallStatuses[agentId] = callStatus;
            
            // Convert agent state to numeric value
            var stateValue = agentState.ToLowerInvariant() switch
            {
                "offline" => 0.0,
                "away" => 1.0,
                "transfers_only" or "transfers only" => 2.0,
                "online" or "available" => 3.0,
                _ => -1.0 // Unknown states get -1
            };
            
            // Convert call status to numeric value based on Zendesk API documentation
            var callStatusValue = callStatus.ToLowerInvariant() switch
            {
                "null" => 0.0,        // No call
                "on_call" => 1.0,     // Currently on a call
                "wrap_up" => 2.0,     // In wrap-up mode after a call
                _ => -1.0             // Unknown call status
            };
            
            // Update metrics with numeric values
            AgentStateGauge.WithLabels(agentId.ToString(), agentName).Set(stateValue);
            AgentCallStatusGauge.WithLabels(agentId.ToString(), agentName).Set(callStatusValue);
            LastAvailabilityUpdateGauge.WithLabels(agentId.ToString(), agentName).SetToCurrentTimeUtc();
            
            _logger.LogDebug("Updated metrics for agent {AgentId} ({AgentName}): state={AgentState}({StateValue}), call_status={CallStatus}({CallStatusValue})", 
                           agentId, agentName, agentState, stateValue, callStatus, callStatusValue);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse availability JSON for agent {AgentId}: {Json}", agentId, availabilityJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update metrics for agent {AgentId}", agentId);
        }
    }

    /// <summary>
    /// Gets the agent name for a given agent ID. Returns null if not found or configured.
    /// This could be enhanced to cache agent names or retrieve them from the Zendesk configuration.
    /// </summary>
    private async Task<string?> GetAgentNameAsync(long agentId)
    {
        try
        {
            // Try to get agent name from configuration first (faster)
            var agentName = _configuration[$"Zendesk:Agents:{agentId}:Name"];
            if (!string.IsNullOrEmpty(agentName))
            {
                return agentName;
            }
            
            // If not in config, you could optionally make an API call to get agent details
            // For now, just return null to use the agent ID as the name
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not retrieve agent name for {AgentId}", agentId);
            return null;
        }
    }
}
