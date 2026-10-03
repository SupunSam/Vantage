namespace Vantage.Domain.Entities;

/// <summary>A request from the catalogue or the "no access" page. Always needs owner (or Super Admin) approval.</summary>
public class AccessRequest
{
    public int Id { get; set; }
    public int DashboardId { get; set; }
    public int RequesterUserId { get; set; }
    public string? RequesterComment { get; set; }
    public AccessRequestStatus Status { get; set; } = AccessRequestStatus.Pending;
    public DateTime RequestedAtUtc { get; set; }
    public int? DecidedByUserId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionNote { get; set; }
    /// <summary>The group the approver placed the requester in.</summary>
    public int? AssignedGroupId { get; set; }

    public Dashboard Dashboard { get; set; } = null!;
    public User Requester { get; set; } = null!;
    public DashboardGroup? AssignedGroup { get; set; }
}

/// <summary>Monthly access review of one dashboard by its owner.</summary>
public class AccessReview
{
    public int Id { get; set; }
    public int DashboardId { get; set; }
    /// <summary>First day of the month being reviewed.</summary>
    public DateOnly Period { get; set; }
    public int OwnerUserId { get; set; }
    public AccessReviewStatus Status { get; set; } = AccessReviewStatus.Open;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RemindedAtUtc { get; set; }
    public DateTime? EscalatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int? CompletedByUserId { get; set; }

    public Dashboard Dashboard { get; set; } = null!;
    public List<AccessReviewItem> Items { get; set; } = [];
}

public class AccessReviewItem
{
    public int Id { get; set; }
    public int ReviewId { get; set; }
    public int GroupMemberId { get; set; }
    public ReviewDecision Decision { get; set; } = ReviewDecision.Pending;
    public DateTime? DecidedAtUtc { get; set; }

    public AccessReview Review { get; set; } = null!;
    public GroupMember GroupMember { get; set; } = null!;
}

public class Pin
{
    public int UserId { get; set; }
    public int DashboardId { get; set; }
    public int SortOrder { get; set; }
    public DateTime PinnedAtUtc { get; set; }

    public Dashboard Dashboard { get; set; } = null!;
}

public class PersonalFolder
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public List<PersonalFolderItem> Items { get; set; } = [];
}

public class PersonalFolderItem
{
    public int FolderId { get; set; }
    public int DashboardId { get; set; }
    public DateTime AddedAtUtc { get; set; }

    public PersonalFolder Folder { get; set; } = null!;
    public Dashboard Dashboard { get; set; } = null!;
}
