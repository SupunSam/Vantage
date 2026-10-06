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
    public const string ToastSeconds = "ui.toastSeconds";
    public const string BrandFooterText = "branding.footerText";
    public const string StoragePowerBiFolder = "storage.powerBiFolder";
    public const string StorageGenAiFolder = "storage.genAiFolder";
    public const string ExternalSeeInternalCatalogue = "catalogue.externalSeeInternal";
    public const string InternalEmailDomain = "auth.internalEmailDomain";
    public const string EmailEnabled = "email.enabled";
    public const string GenAiLinkMinutes = "genai.linkMinutes";
    public const string GenAiWarnSizeMb = "genai.warnSizeMb";
    public const string GenAiScanHost = "genai.scanHost";
    public const string GenAiScanPort = "genai.scanPort";
    public const string GenAiBaseUrl = "genai.baseUrl";
    public const string GenAiFrameAncestors = "genai.frameAncestors";
    /// <summary>All GenAI settings start with this. Only Super Admins may change them (they control security).</summary>
    public const string GenAiPrefix = "genai.";

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
        (BrandFooterText, "", "A line of text shown in the footer of both portals, after the copyright. Leave blank for none"),
        (StoragePowerBiFolder, "powerbi", "Folder (or, later, the S3 bucket prefix) where uploaded Power BI files are kept"),
        (StorageGenAiFolder, "genai", "Folder (or, later, the S3 bucket prefix) where uploaded GenAI files are kept"),
        (ToastSeconds, "6", "How long pop-up messages (saved, added, failed) stay on screen, in seconds"),
        (ExternalSeeInternalCatalogue, "false", "Whether external users see Audience = Internal dashboards in the catalogue"),
        (InternalEmailDomain, "rrd.com", "Email domain that marks a user as internal and routes sign-in to ADFS"),
        (EmailEnabled, "true", "Send the emails the portal queues (access requests, approvals and so on). When off they wait in the outbox"),
        (GenAiLinkMinutes, "60", "How long the signed link to a GenAI dashboard works. A person who reloads the frame after this needs to open the dashboard again"),
        (GenAiWarnSizeMb, "2", "A GenAI file this size or larger is accepted with a warning that it will load slowly. The largest allowed size is on the Dashboard Types tab"),
        (GenAiScanHost, "", "Host name of the ClamAV malware scanner. When set, every GenAI upload and restore is scanned and an unreachable scanner refuses the upload. Blank uses the scanner set for this environment, if any"),
        (GenAiScanPort, "3310", "Port of the ClamAV scanner (clamd)"),
        (GenAiBaseUrl, "", "Web address GenAI dashboards load from: a different site from both portals, for example https://genai.example.com. Blank uses the address set for this environment"),
        (GenAiFrameAncestors, "", "The portal addresses allowed to show a GenAI dashboard in a frame, separated by spaces, for example https://admin.example.com https://portal.example.com. Blank uses the addresses set for this environment"),
    ];
}
