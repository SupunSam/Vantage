namespace Vantage.Infrastructure.Settings;

/// <summary>Keys for SystemSetting rows edited in Admin Configuration, with their seeded defaults.</summary>
public static class SettingKeys
{
    public const string BrandPortalName = "branding.portalName";
    public const string BrandLogoUrl = "branding.logoUrl";
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
    public const string ReviewReminderDays = "review.reminderDays";
    public const string ReviewEscalationDays = "review.escalationDays";

    public static readonly IReadOnlyList<(string Key, string Value, string Description)> Defaults =
    [
        (BrandPortalName, "Vantage", "Name shown in the header of both portals"),
        (BrandLogoUrl, "/brand/logo.svg", "Logo image URL or uploaded file path"),
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
        (ReviewReminderDays, "7", "Days after a monthly access review opens before owners are reminded"),
        (ReviewEscalationDays, "14", "Days after a monthly access review opens before Super Admins are alerted"),
    ];
}
