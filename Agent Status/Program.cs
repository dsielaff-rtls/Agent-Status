
using Agent_Status;
using Prometheus;

// Start Prometheus metrics server on port 5000
var metricServer = new KestrelMetricServer(port: 5000);
metricServer.Start();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<ZendeskTalkService>();
var app = builder.Build();

// Example usage: fetch agent availability at startup (replace with your agentId)
var zendeskService = app.Services.GetRequiredService<ZendeskTalkService>();
int agentId = 123456; // TODO: Replace with a real agent ID
try
{
	var availability = await zendeskService.GetAgentAvailabilityAsync(agentId);
	Console.WriteLine($"Agent {agentId} availability: {availability}");
}
catch (Exception ex)
{
	Console.WriteLine($"Error fetching agent availability: {ex.Message}");
}

app.Run();
