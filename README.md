# Agent Status Service

A .NET monitoring service that tracks Zendesk agent availability and ticket counts, providing real-time insights through Prometheus metrics and a web dashboard.

## What This Program Does

### 🎯 Core Functionality

The Agent Status Service is a background monitoring application that continuously tracks your Zendesk environment and provides real-time operational metrics. It monitors two key areas:

#### 1. **Agent Availability Monitoring**
- **Tracks agent states**: Monitors whether agents are online, away, offline, or in transfers-only mode
- **Call status tracking**: Detects when agents are on calls, in wrap-up mode, or available
- **Change detection**: Logs and counts state transitions for operational insights
- **Multi-agent support**: Simultaneously monitors multiple selected agents
- **Real-time updates**: Checks agent status every 15 seconds

#### 2. **Ticket Count Monitoring**
- **View-based counting**: Monitors ticket count from a specific Zendesk view (ID: 360077881353)
- **Efficient API usage**: Uses Zendesk's View Count API for fast, cached results
- **Continuous tracking**: Updates ticket counts every 15 seconds alongside agent monitoring

### 📊 Data Export & Metrics

The service exports comprehensive metrics in **Prometheus format** at `/metrics`, enabling integration with monitoring dashboards like Grafana:

#### Agent Metrics:
- `zendesk_agent_state` - Current agent state (0=offline, 1=away, 2=transfers_only, 3=online)
- `zendesk_agent_call_status` - Call status (0=no_call, 1=on_call, 2=wrap_up)
- `zendesk_agent_state_changes_total` - Count of agent state changes
- `zendesk_agent_call_status_changes_total` - Count of call status changes
- `zendesk_agent_last_update_timestamp` - Last update timestamp for each agent

#### Ticket Metrics:
- `zendesk_view_tickets_total` - Current ticket count in the monitored view

#### System Metrics:
- `worker_run_total` - Total monitoring cycles executed
- `zendesk_api_calls_total` - API call counts by status (success/failure/rate_limited)
- `zendesk_backoff_delay_seconds` - Current backoff delay for failed API calls
- `zendesk_configuration_valid` - Configuration validity status

### 🌐 Web Interface

Includes a user-friendly web interface for:
- **Configuration management**: Set up Zendesk credentials and select agents to monitor
- **Agent selection**: Choose which agents to track from your Zendesk instance
- **Real-time status**: View current configuration and monitoring status
- **Easy setup**: Test connections and validate credentials

### 🔄 Intelligent Monitoring

- **Exponential backoff**: Automatically reduces API calls when errors occur
- **Rate limit handling**: Respects Zendesk API rate limits and retries appropriately
- **Error resilience**: Continues monitoring even if individual API calls fail
- **Configuration validation**: Ensures credentials are valid before starting monitoring

### 🏢 Use Cases

**Operations Teams**: Monitor call center agent availability and ticket queues in real-time

**IT Administrators**: Track Zendesk system health and API performance

**Management**: Get insights into agent utilization and ticket volume trends

**DevOps**: Integrate with existing monitoring infrastructure via Prometheus metrics

## Setup

### Configuration

1. Copy the example configuration file:
   ```bash
   cp "Agent Status/appsettings.Example.json" "Agent Status/appsettings.json"
   ```

2. Edit `Agent Status/appsettings.json` with your actual Zendesk credentials:
   - Replace `your_subdomain` with your Zendesk subdomain
   - Replace `your_email@example.com` with your Zendesk admin email
   - Replace `your_api_token` with your Zendesk API token

3. Alternatively, use the web configuration interface:
   - Start the application with `dotnet run`
   - Navigate to `https://localhost:5001/config`
   - Enter your Zendesk credentials in the web form

### Getting Your Zendesk API Token

1. Log in to your Zendesk instance as an admin
2. Go to **Admin Center**
3. Navigate to **Apps and integrations > APIs > Zendesk API**
4. Under **Token Access**, ensure it's enabled
5. Click the **Add API token** button
6. Enter a description and save
7. Copy the generated token

## Running the Service

### Development Mode
```bash
cd "Agent Status"
dotnet run
```

### Production Deployment
```bash
cd "Agent Status"
dotnet publish -c Release
# Deploy published files to your server
```

### What Happens When You Start
1. **Web Interface**: Starts on `http://localhost:5000` (configurable)
2. **Metrics Endpoint**: Available at `http://localhost:5000/metrics`
3. **Configuration Check**: Validates Zendesk credentials
4. **Agent Monitoring**: Begins checking selected agents every 15 seconds
5. **Ticket Monitoring**: Starts tracking view 360077881353 every 15 seconds
6. **Logging**: Outputs to console and `logs/agent-status-{date}.txt`

### Accessing the Service
- **Web Dashboard**: `http://localhost:5000` - Configuration and status
- **Configuration Page**: `http://localhost:5000/config` - Setup credentials and select agents
- **Prometheus Metrics**: `http://localhost:5000/metrics` - Raw metrics data
- **Log Files**: `logs/` directory - Detailed application logs

## Security

- `appsettings.json` and `appsettings.Development.json` are excluded from git to protect sensitive credentials
- Use `appsettings.Example.json` as a template for configuration
- Never commit actual API tokens or credentials to version control

## Metrics

The service exposes comprehensive Prometheus metrics at `/metrics`:

### Agent Availability Metrics
- `zendesk_agent_state` - Current agent state (0=offline, 1=away, 2=transfers_only, 3=online)
- `zendesk_agent_call_status` - Current call status (0=no_call, 1=on_call, 2=wrap_up)
- `zendesk_agent_state_changes_total` - Total number of agent state changes
- `zendesk_agent_call_status_changes_total` - Total number of agent call status changes
- `zendesk_agent_last_update_timestamp` - Unix timestamp of last availability update

### Ticket Metrics
- `zendesk_view_tickets_total` - Total number of tickets in monitored Zendesk view (360077881353)
- `zendesk_view_ticket_count_api_calls_total` - Total number of view ticket count API calls by status

### System Metrics
- `worker_run_total` - Total number of worker loop executions
- `zendesk_api_calls_total` - Total number of Zendesk API calls by status
- `zendesk_backoff_delay_seconds` - Current backoff delay in seconds
- `zendesk_configuration_valid` - Whether Zendesk configuration is valid (1=valid, 0=invalid)

### Monitoring Frequency
- **Agent availability**: Every 15 seconds
- **Ticket counts**: Every 15 seconds
- **Metrics update**: Real-time with each monitoring cycle

## Architecture

### Technical Stack
- **.NET 9.0**: Modern, high-performance runtime
- **ASP.NET Core**: Web framework for configuration interface
- **Background Services**: Continuous monitoring using hosted services
- **Prometheus.NET**: Native metrics collection and export
- **Serilog**: Structured logging with file and console output

### API Integration
- **Zendesk Talk API**: For agent availability data (`/api/v2/channels/voice/availabilities/{agent_id}`)
- **Zendesk Users API**: For agent information and selection (`/api/v2/users`)
- **Zendesk Views API**: For efficient ticket counting (`/api/v2/views/{view_id}/count`)

### Key Features
- **Resilient error handling**: Exponential backoff, rate limit respect
- **Configuration management**: Web-based setup and agent selection
- **Real-time monitoring**: 15-second update intervals
- **Prometheus integration**: Industry-standard metrics format
- **Comprehensive logging**: Structured logs for debugging and monitoring

## Quick Start

1. **Clone and Build**:
   ```bash
   git clone <repository-url>
   cd Agent-Status
   dotnet build
   ```

2. **Configure Zendesk**:
   ```bash
   cd "Agent Status"
   dotnet run
   # Open http://localhost:5000/config
   # Enter your Zendesk subdomain, email, and API token
   ```

3. **Select Agents**:
   - Click "Fetch Agents" to load your Zendesk agents
   - Select which agents you want to monitor
   - Save your selection

4. **Monitor**:
   - View metrics at `http://localhost:5000/metrics`
   - Monitor logs in console or `logs/` directory
   - Integrate with Grafana or other monitoring tools

## Troubleshooting

### Common Issues
- **Configuration invalid**: Ensure your Zendesk subdomain, email, and API token are correct
- **No agents selected**: Use the configuration page to fetch and select agents
- **API rate limiting**: The service automatically handles rate limits with exponential backoff
- **View not found**: Ensure view 360077881353 exists and is accessible with your credentials

### Logs
Check the console output or log files in the `logs/` directory for detailed error messages and debugging information.