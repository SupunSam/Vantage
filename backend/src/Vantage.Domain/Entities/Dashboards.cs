namespace Vantage.Domain.Entities;

public class Dashboard
{
    /// <summary>Dashboard ID; also part of the default group name.</summary>
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public BiType Type { get; set; }
    public DashboardStatus Status { get; set; } = DashboardStatus.Draft;

    /// <summary>The deepest category chosen (Primary, Secondary or Tertiary); the dashboard sits here.</summary>
    public int? CategoryId { get; set; }

    // Power BI
    public int? TenantId { get; set; }
    public int? WorkspaceId { get; set; }
    /// <summary>Published report ID: the key that locates the dashboard in its workspace.</summary>
    public Guid? PowerBiReportId { get; set; }
    public Guid? PowerBiDatasetId { get; set; }
    /// <summary>Power BI import in progress (or last import); polled while the dashboard is Publishing.</summary>
    public Guid? PowerBiImportId { get; set; }
    /// <summary>Set while a new .pbix (Modify Dashboard or a restored version) is importing over the live report; the dashboard stays Active meanwhile.</summary>
    public Guid? ReplaceImportId { get; set; }
    /// <summary>The file version being imported by <see cref="ReplaceImportId"/>; it becomes current when the import succeeds.</summary>
    public int? ReplaceVersionNumber { get; set; }

    // Tableau
    public string? TableauViewUrl { get; set; }

    // GenAI and thumbnails (storage keys in S3 or the local file store)
    public string? ThumbnailKey { get; set; }

    public bool RlsEnabled { get; set; }
    public Audience Audience { get; set; } = Audience.Internal;
    public DataClassification DataClassification { get; set; } = DataClassification.Internal;

    public int? PrimaryOwnerId { get; set; }
    public int? BackupOwnerId { get; set; }
    /// <summary>Set when the primary owner left and the backup was promoted; blocks access changes until a Super Admin confirms.</summary>
    public bool OwnershipPendingReview { get; set; }

    public string? SharePointFolder { get; set; }
    public string? SharePointSubFolder { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public int? CreatedByUserId { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime? LastViewedAtUtc { get; set; }
    /// <summary>Set by the inactivity job when no one viewed it for the configured number of days.</summary>
    public DateTime? InactivityFlaggedAtUtc { get; set; }
    public DateTime? RetiredAtUtc { get; set; }
    public string? LastError { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public Category? Category { get; set; }
    public BiTenant? Tenant { get; set; }
    public PowerBiWorkspace? Workspace { get; set; }
    public User? PrimaryOwner { get; set; }
    public User? BackupOwner { get; set; }
    public List<DashboardTag> Tags { get; set; } = [];
    public List<DashboardGroup> Groups { get; set; } = [];
    public List<DashboardVersion> Versions { get; set; } = [];
    public RefreshSchedule? RefreshSchedule { get; set; }
}

public class DashboardTag
{
    public int DashboardId { get; set; }
    public string Tag { get; set; } = "";

    public Dashboard Dashboard { get; set; } = null!;
}

/// <summary>Uploaded file versions (.pbix or GenAI .html); the last 3 are kept.</summary>
public class DashboardVersion
{
    public int Id { get; set; }
    public int DashboardId { get; set; }
    public int VersionNumber { get; set; }
    public string FileKey { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public bool IsCurrent { get; set; }
    public ScanStatus ScanStatus { get; set; }
    public string? ScanReport { get; set; }
    public DateTime UploadedAtUtc { get; set; }
    public int? UploadedByUserId { get; set; }

    public Dashboard Dashboard { get; set; } = null!;
}

/// <summary>Power BI refresh schedule; the portal is the master copy and pushes it to Power BI.</summary>
public class RefreshSchedule
{
    public int DashboardId { get; set; }
    public bool Enabled { get; set; }
    /// <summary>Comma-separated day names, e.g. "Monday,Wednesday".</summary>
    public string Days { get; set; } = "";
    /// <summary>Comma-separated HH:mm times, e.g. "06:00,14:30".</summary>
    public string Times { get; set; } = "";
    public string TimeZoneId { get; set; } = "Sri Lanka Standard Time";
    public DateTime? LastPushedAtUtc { get; set; }
    public string? LastPushError { get; set; }

    public Dashboard Dashboard { get; set; } = null!;
}

public class DashboardGroup
{
    /// <summary>Backend Group ID used for every mapping.</summary>
    public int Id { get; set; }
    public int DashboardId { get; set; }
    /// <summary>Up to 40 characters, unique across the portal.</summary>
    public string Name { get; set; } = "";
    /// <summary>Power BI role name defined in the .pbix; sent in the embed token when the dashboard's RLS flag is on.</summary>
    public string? RlsValue { get; set; }
    public GroupStatus Status { get; set; } = GroupStatus.Active;
    public bool IsDefault { get; set; }
    public int? ClonedFromGroupId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int? CreatedByUserId { get; set; }

    public Dashboard Dashboard { get; set; } = null!;
    public List<GroupMember> Members { get; set; } = [];
}

/// <summary>
/// A user's membership of a group. DashboardId is copied from the group so the database can enforce
/// "at most one live group per user per dashboard" with a filtered unique index.
/// </summary>
public class GroupMember
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public int DashboardId { get; set; }
    public int UserId { get; set; }
    public MembershipSource Source { get; set; }
    public DateTime AddedAtUtc { get; set; }
    public int? AddedByUserId { get; set; }
    /// <summary>Null while the membership is live; set when removed (rows are never hard-deleted).</summary>
    public DateTime? RemovedAtUtc { get; set; }
    public int? RemovedByUserId { get; set; }
    public string? RemovedReason { get; set; }

    public DashboardGroup Group { get; set; } = null!;
    public User User { get; set; } = null!;
}

/// <summary>A saved, unpublished publish form (C60): the details only, kept per person. Files are chosen again at publish time.</summary>
public class PublishDraft
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public BiType Type { get; set; }
    public string Title { get; set; } = "";
    /// <summary>The form values as JSON.</summary>
    public string Payload { get; set; } = "{}";
    public DateTime UpdatedAtUtc { get; set; }
}
