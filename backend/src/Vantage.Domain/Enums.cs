namespace Vantage.Domain;

public enum UserType { Internal, External }

/// <summary>PendingSetup = external user created by an admin who has not completed first login.</summary>
public enum UserStatus { Active, Inactive, PendingSetup }

public enum PermissionLevel { None = 0, View = 1, Edit = 2 }

public enum BiPlatform { PowerBi, Tableau }

public enum DashboardType { PowerBi, Tableau, GenAi }

public enum DashboardStatus { Draft, Publishing, Active, Failed, Inactive, Retired }

public enum Audience { Internal, Client }

public enum DataClassification { Public, Internal, Confidential, Restricted }

public enum GroupStatus { Active, Inactive, Retired }

public enum MembershipSource { Manual, BulkUpload, Clone, AccessRequest, OwnerAuto, SuperAdminSelf, Migration, AccessRule }

/// <summary>What a request to the owners asks for: people added to a group, or removed from it (the latter only comes from access rules).</summary>
public enum GroupChangeAction { Add, Remove }

public enum AccessRequestStatus { Pending, Approved, Rejected, Cancelled }

/// <summary>An admin's batch of people to add to one access group, waiting for the dashboard's owners (C26).</summary>
public enum GroupAddRequestStatus { Pending, Approved, PartlyApproved, Rejected, Cancelled }

/// <summary>What the approver decided for one person in a batch. Skipped = approved but couldn't be added (for example already in the group).</summary>
public enum GroupAddItemDecision { Pending, Approved, Rejected, Skipped }

public enum AccessReviewStatus { Open, Completed, Escalated }

public enum ReviewDecision { Pending, Keep, Remove }

public enum ScanStatus { NotRequired, Pending, Passed, Failed }

public enum ViewSource { Portal, PowerBiActivity, TableauUsage }

public enum EmailStatus { Pending, Sent, Failed }

public enum JobRunStatus { Running, Succeeded, Failed }

/// <summary>What started a job run: the scheduler, or a Super Admin pressing Run Now.</summary>
public enum JobTrigger { Schedule, Manual }
