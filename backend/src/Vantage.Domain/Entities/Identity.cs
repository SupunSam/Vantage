namespace Vantage.Domain.Entities;

/// <summary>Central user table for internal and external users. Email is the business key.</summary>
public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DisplayName { get; set; }
    public UserType UserType { get; set; }
    public UserStatus Status { get; set; } = UserStatus.Active;
    /// <summary>True when an admin set the status by hand; the HRMS job never overrides it.</summary>
    public bool StatusSetManually { get; set; }
    public string? CognitoSub { get; set; }
    public string? ContactNumber { get; set; }
    public string TimeZone { get; set; } = "Asia/Colombo";
    /// <summary>Tableau Server user name used in the Connected App JWT; entered by admins.</summary>
    public string? TableauUserName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }

    public HrmsProfile? HrmsProfile { get; set; }
    public List<UserRole> Roles { get; set; } = [];
}

/// <summary>HRMS details for internal users only, one row per user, refreshed by the monthly HRMS job.</summary>
public class HrmsProfile
{
    public int UserId { get; set; }
    public string EmployeeId { get; set; } = "";
    public string? Department { get; set; }
    public string? Division { get; set; }
    public string? JobTitle { get; set; }
    public string? JobGrade { get; set; }
    public string? ManagerEmail { get; set; }
    public string? Location { get; set; }
    public string EmploymentStatus { get; set; } = "Active";
    public DateOnly? HireDate { get; set; }
    public DateOnly? ExitDate { get; set; }
    public DateTime LastSyncedAtUtc { get; set; }

    public User User { get; set; } = null!;
}

/// <summary>
/// Read-only mirror of the HRMS source (hrms.EmployeeProfile), filled by the SQL-level sync.
/// Not managed by migrations.
/// </summary>
public class HrmsEmployeeSource
{
    public string EmployeeId { get; set; } = "";
    public string Email { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Department { get; set; }
    public string? Division { get; set; }
    public string? JobTitle { get; set; }
    public string? JobGrade { get; set; }
    public string? ManagerEmail { get; set; }
    public string? Location { get; set; }
    public string EmploymentStatus { get; set; } = "";
    public DateOnly HireDate { get; set; }
    public DateOnly? ExitDate { get; set; }
    public DateTime LastSyncedAtUtc { get; set; }
}

public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Built-in roles cannot be renamed or deleted.</summary>
    public bool IsSystem { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public List<RolePermission> Permissions { get; set; } = [];
    public List<UserRole> Users { get; set; } = [];
}

public class RolePermission
{
    public int RoleId { get; set; }
    /// <summary>Key from <see cref="AppModules"/>.</summary>
    public string Module { get; set; } = "";
    public PermissionLevel Level { get; set; }

    public Role Role { get; set; } = null!;
}

public class UserRole
{
    public int UserId { get; set; }
    public int RoleId { get; set; }
    /// <summary>True when assigned by a rule (e.g. Dashboard Owner given on becoming an owner).</summary>
    public bool IsAutomatic { get; set; }
    public DateTime AssignedAtUtc { get; set; }
    public int? AssignedByUserId { get; set; }

    public User User { get; set; } = null!;
    public Role Role { get; set; } = null!;
}
