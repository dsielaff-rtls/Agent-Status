
using Agent_Status;
using Prometheus;
using Serilog;

// Configure Serilog
var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console();

// Only add file logging if not running in Docker
bool runningInDocker = IsRunningInDocker();
if (!runningInDocker)
{
    loggerConfig.WriteTo.File("logs/agent-status-.txt", rollingInterval: RollingInterval.Day);
}

Log.Logger = loggerConfig.CreateLogger();

try
{
    if (runningInDocker)
    {
        Log.Information("Starting Agent Status service (Docker mode - console logging only)");
    }
    else
    {
        Log.Information("Starting Agent Status service (standalone mode - console and file logging)");
    }

    var builder = WebApplication.CreateBuilder(args);
    
    // Add Serilog to the host builder
    builder.Host.UseSerilog();
    
    // Add services to the container
    builder.Services.AddRazorPages();
    builder.Services.AddMemoryCache();
    builder.Services.AddAntiforgery(options =>
    {
        // Make antiforgery more permissive for development
        if (builder.Environment.IsDevelopment())
        {
            options.Cookie.SameSite = SameSiteMode.None;
            options.Cookie.SecurePolicy = CookieSecurePolicy.None;
        }
        options.SuppressXFrameOptionsHeader = false;
    });
    builder.Services.AddHostedService<Worker>();
    
    // Configure HttpClient with connection pooling for ZendeskTalkService
    builder.Services.AddHttpClient<ZendeskTalkService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("User-Agent", "Agent-Status-Monitor/1.0");
        // Enable HTTP/2 and connection keep-alive
        client.DefaultRequestVersion = new Version(2, 0);
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler()
    {
        MaxConnectionsPerServer = 10,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        // SSL configuration for CATO network - bypass certificate validation in Docker
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions()
        {
            // Only bypass SSL validation when running in Docker (CATO network)
            RemoteCertificateValidationCallback = IsRunningInDocker() ? 
                (sender, certificate, chain, sslPolicyErrors) => true : null
        }
    });
    
    var app = builder.Build();

    // Configure the HTTP request pipeline
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthorization();
    
    // Configure Prometheus to suppress default metrics and only show custom metrics
    Metrics.SuppressDefaultMetrics();
    
    // Add Prometheus metrics endpoint to the existing web app
    app.MapMetrics(); // This adds /metrics endpoint to your existing app
    
    app.MapRazorPages();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

static bool IsRunningInDocker()
{
    // Check for .NET's built-in Docker detection environment variable
    if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
    {
        return true;
    }
    
    // Check for custom environment variable we'll set in Dockerfile
    if (Environment.GetEnvironmentVariable("RUNNING_IN_DOCKER") == "true")
    {
        return true;
    }
    
    // Check for Docker-specific file (Linux containers)
    if (File.Exists("/.dockerenv"))
    {
        return true;
    }
    
    return false;
}
