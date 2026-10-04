namespace Vantage.Infrastructure.Settings;

/// <summary>Keys for SystemSetting rows edited in Admin Configuration, with their seeded defaults.</summary>
public static class SettingKeys
{
    public const string BrandPortalName = "branding.portalName";
    public const string BrandLogoUrl = "branding.logoUrl";
    /// <summary>File-store key of an uploaded logo. Internal: not shown as a setting, and not sent to the browser.</summary>
    public const string BrandLogoFile = "branding.logoFile";
    public const string BrandPrimaryColor = "branding.primaryColor";
    public const string BrandAccentColor = "branding.accentColor";
    public const string InactivityDays = "lifecycle.inactivityDays";
    public const string HrmsSyncCron = "hrms.syncCron";
    public const string LeaverRevokeImmediately = "hrms.leaverRevokeImmediately";
    public const string IdleTimeoutMinutes = "session.idleTimeoutMinutes";
    public const string NewHireDigestFrequency = "digest.newHireFrequency";
    public const string DefaultGridPageSize = "ui.defaultGridPageSize";
    public const string ExternalSeeInternalCatalogue = "catalogue.externalSeeInternal";
    public const string InternalEmailDomain = "auth.internalEmailDomain";
    public const string EmailEnabled = "email.enabled";

    public const string DefaultLogoUrl = "/brand/logo.svg";

    /// <summary>Settings from earlier builds that no longer exist; the seeder removes them.</summary>
    public static readonly IReadOnlyList<string> Retired = ["review.reminderDays", "review.escalationDays"];

    public static readonly IReadOnlyList<(string Key, string Value, string Description)> Defaults =
    [
        (BrandPortalName, "Vantage", "Name shown in the header of both portals"),
        (BrandLogoUrl, DefaultLogoUrl, "Logo image URL or uploaded file path"),
        (BrandPrimaryColor, "#1F4E79", "Primary colour (hex)"),
        (BrandAccentColor, "#E8833A", "Accent colour (hex)"),
        (InactivityDays, "90", "Days without views before owners are emailed and the dashboard is flagged"),
        (HrmsSyncCron, "0 2 1 * *", "When the HRMS job reads the synced HRMS table (cron, UTC). Default: 02:00 on the 1st of each month"),
        (LeaverRevokeImmediately, "true", "Leavers lose access and sessions as soon as the HRMS job marks them Inactive"),
        (IdleTimeoutMinutes, "30", "Idle session timeout in minutes"),
        (NewHireDigestFrequency, "Monthly", "How often Super Admins and BPI receive the new-hire list"),
        (DefaultGridPageSize, "25", "Default rows per page in grids"),
        (ExternalSeeInternalCatalogue, "false", "Whether external users see Audience = Internal dashboards in the catalogue"),
        (InternalEmailDomain, "rrd.com", "Email domain that marks a user as internal and routes sign-in to ADFS"),
        (EmailEnabled, "true", "Send the emails the portal queues (access requests, approvals and so on). When off they wait in the outbox"),
    ];
}
