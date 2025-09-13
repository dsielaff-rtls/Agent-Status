using System.ComponentModel.DataAnnotations;

namespace Agent_Status.Models
{
    public class ZendeskConfigurationModel
    {
        [Required(ErrorMessage = "Subdomain is required")]
        [Display(Name = "Zendesk Subdomain")]
        [RegularExpression(@"^[a-zA-Z0-9-]+$", ErrorMessage = "Subdomain can only contain letters, numbers, and hyphens")]
        public string Subdomain { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Please enter a valid email address")]
        [Display(Name = "Email Address")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "API Token is required")]
        [Display(Name = "API Token")]
        [DataType(DataType.Password)]
        public string ApiToken { get; set; } = string.Empty;

        // Agent Selection Configuration - now stores complete agent data with selection state
        [Display(Name = "Agents")]
        public Dictionary<long, ZendeskAgentConfig> Agents { get; set; } = new();

        // Legacy property for backwards compatibility (can be removed later)
        [Display(Name = "Selected Agents")]
        public Dictionary<long, bool> SelectedAgents { get; set; } = new();
    }

    public class ZendeskAgent
    {
        public long Id { get; set; }  // Changed from int to long for large IDs
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool Active { get; set; }
        public string Role { get; set; } = string.Empty;
        public bool IsSelected { get; set; } = false;  // Whether this agent is selected for monitoring
    }

    public class ZendeskAgentConfig
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool Active { get; set; }
        public string Role { get; set; } = string.Empty;
        public bool IsSelected { get; set; } = false;  // Whether this agent should be monitored
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;  // When this agent info was last fetched
    }
}