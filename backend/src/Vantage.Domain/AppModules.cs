namespace Vantage.Domain;

/// <summary>
/// Modules that role permissions are granted on (View or Edit per module, per role).
/// The keys are stored in RolePermission.Module, so never rename a key once deployed.
/// </summary>
public static class AppModules
{
    public const string Dashboards = "dashboards";            // User Portal: home, viewer, pins, folders
    public const string Catalogue = "catalogue";              // User Portal: catalogue and request access
    public const string OwnerWorkspace = "owner-workspace";   // Approvals, monthly review, owner analytics
    public const string Publishing = "publishing";            // Publish and replace dashboards
    public const string DashboardConfig = "dashboard-config"; // Configuration page, refresh, versions, lifecycle
    public const string Directory = "directory";              // Dashboard Directory grid
    public const string Categories = "categories";
    public const string Groups = "groups";
    public const string Users = "users";
    public const string Roles = "roles";
    public const string Analytics = "analytics";              // Super Admin stats and access reports
    public const string Audit = "audit";
    public const string AdminConfig = "admin-config";         // Settings, branding, CDN list, BI service master
    public const string Tenants = "tenants";                  // Power BI and Tableau tenant master
    public const string ScheduledJobs = "scheduled-jobs";     // Schedule, last and next run, Run Now for the background jobs

    public static readonly IReadOnlyList<(string Key, string Name, string Portal)> All =
    [
        (Dashboards, "My Dashboards", "User"),
        (Catalogue, "Dashboard Catalogue", "User"),
        (OwnerWorkspace, "Owner Workspace", "User"),
        (Publishing, "Publishing", "Admin"),
        (DashboardConfig, "Dashboard Configuration", "Admin"),
        (Directory, "Dashboard Directory", "Admin"),
        (Categories, "Categories", "Admin"),
        (Groups, "Access Groups", "Admin"),
        (Users, "Users", "Admin"),
        (Roles, "Roles and Permissions", "Admin"),
        (Analytics, "Analytics", "Admin"),
        (Audit, "Audit Log", "Admin"),
        (AdminConfig, "Admin Configuration", "Admin"),
        (Tenants, "Tenants", "Admin"),
        (ScheduledJobs, "Scheduled Jobs", "Admin"),
    ];
}

public static class SystemRoles
{
    public const string SuperAdmin = "Super Admin";
    public const string DashboardOwner = "Dashboard Owner";
    public const string DashboardUser = "Dashboard User";
}
