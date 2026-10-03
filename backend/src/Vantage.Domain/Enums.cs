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

public enum MembershipSource { Manual, BulkUpload, Clone, AccessRequest, OwnerAuto, SuperAdminSelf, Migration }

public enum AccessRequestStatus { Pending, Approved, Rejected, Cancelled }

public enum AccessReviewStatus { Open, Completed, Escalated }

public enum ReviewDecision { Pending, Keep, Remove }

public enum ScanStatus { NotRequired, Pending, Passed, Failed }

public enum ViewSource { Portal, PowerBiActivity, TableauUsage }

public enum EmailStatus { Pending, Sent, Failed }

public enum JobRunStatus { Running, Succeeded, Failed }
