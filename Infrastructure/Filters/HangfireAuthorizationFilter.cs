using Hangfire.Dashboard;

namespace OCREngine.Infrastructure.Filters;

/// <summary>
/// Simple authorization filter for Hangfire Dashboard.
/// In production, implement proper authentication/authorization.
/// </summary>
public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        // For development - allow all
        // In production, add proper authorization logic:
        // - Check if user is authenticated
        // - Check user roles/claims
        // - Validate IP address, etc.

        return true; // TODO: Implement proper authorization in production
    }
}
