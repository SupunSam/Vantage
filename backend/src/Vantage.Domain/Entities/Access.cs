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
    /// <summary>Set when a Super Admin decided instead of the owners: why.</summary>
    public string? OverrideReason { get; set; }

    public Dashboard Dashboard { get; set; } = null!;
    public User Requester { get; set; } = null!;
    public DashboardGroup? AssignedGroup { get; set; }
}

/// <summary>
/// An admin's request to add several people to one access group. The dashboard's owners approve all of it, or only
/// the people they tick. Nobody is added until then, unless a Super Admin overrides with a recorded reason (C26).
/// </summary>
public class GroupAddRequest
{
    public int Id { get; set; }
    public int DashboardId { get; set; }
    public int GroupId { get; set; }
    /// <summary>The admin who asked. Null when an access rule raised the request.</summary>
    public int? RequestedByUserId { get; set; }
    /// <summary>Add (the normal case) or Remove (proposed by an access rule).</summary>
    public GroupChangeAction Action { get; set; } = GroupChangeAction.Add;
    /// <summary>The access rule that raised this request, if any. The name is kept in case the rule is later deleted.</summary>
    public int? RuleId { get; set; }
    public string? RuleName { get; set; }
    public string? ServiceNowReference { get; set; }
    /// <summary>Optional note from the admin to the owners.</summary>
    public string? Note { get; set; }
    /// <summary>People already in another group of the dashboard are moved here on approval.</summary>
    public bool MoveFromOtherGroups { get; set; }
    /// <summary>Kept so the memberships created on approval say how the people were added (Manual, BulkUpload, Clone).</summary>
    public MembershipSource Source { get; set; }
    public GroupAddRequestStatus Status { get; set; } = GroupAddRequestStatus.Pending;
    public DateTime CreatedAtUtc { get; set; }
    public int? DecidedByUserId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionNote { get; set; }
    /// <summary>Set when a Super Admin decided instead of the owners: why.</summary>
    public string? OverrideReason { get; set; }

    public Dashboard Dashboard { get; set; } = null!;
    public DashboardGroup Group { get; set; } = null!;
    public User? RequestedBy { get; set; }
    public List<GroupAddRequestItem> Items { get; set; } = [];
}

public class GroupAddRequestItem
{
    public int Id { get; set; }
    public int RequestId { get; set; }
    public int UserId { get; set; }
    public GroupAddItemDecision Decision { get; set; } = GroupAddItemDecision.Pending;
    /// <summary>What happened when the decision was applied, for example "Added" or "Already in the group".</summary>
    public string? Result { get; set; }

    public GroupAddRequest Request { get; set; } = null!;
    public User User { get; set; } = null!;
}

/// <summary>
/// A Super Admin's rule for one access group: people whose HRMS details match all the conditions are proposed to the owners
/// for adding to the group (Add) or for removal from it (Remove). A rule never changes membership itself (C29).
/// </summary>
public class AccessGroupRule
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public string Name { get; set; } = "";
    public GroupChangeAction Action { get; set; }
    /// <summary>Add rules only: also propose people who are in another group of the same dashboard (they would move).</summary>
    public bool MoveFromOtherGroups { get; set; }
    public bool IsActive { get; set; } = true;
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>Changing a rule starts it afresh: people the owners rejected under the old version are proposed again.</summary>
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
    public string? LastRunSummary { get; set; }

    public DashboardGroup Group { get; set; } = null!;
    public List<AccessGroupRuleCondition> Conditions { get; set; } = [];
}

/// <summary>One "HRMS field equals value" test of a rule. A person must pass all of a rule's conditions.</summary>
public class AccessGroupRuleCondition
{
    public int Id { get; set; }
    public int RuleId { get; set; }
    /// <summary>One of <c>AccessRuleFields</c>: Department, Division, JobTitle, JobGrade, Location, ManagerEmail, EmploymentStatus.</summary>
    public string Field { get; set; } = "";
    public string Value { get; set; } = "";

    public AccessGroupRule Rule { get; set; } = null!;
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
