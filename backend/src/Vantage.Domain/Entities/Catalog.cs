namespace Vantage.Domain.Entities;

/// <summary>Three-level category tree: Primary (1), Secondary (2), Tertiary (3).</summary>
public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public int Level { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public Category? Parent { get; set; }
    public List<Category> Children { get; set; } = [];
}

/// <summary>A Power BI tenant (one service principal) or a Tableau Server site (one Connected App).</summary>
public class BiTenant
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public BiPlatform Platform { get; set; }
    public bool IsActive { get; set; } = true;

    // Power BI
    public string? AzureTenantId { get; set; }
    public string? ClientId { get; set; }

    // Tableau Server
    public string? ServerUrl { get; set; }
    public string? SiteContentUrl { get; set; }
    public string? ConnectedAppClientId { get; set; }
    public string? ConnectedAppSecretId { get; set; }
    /// <summary>Tableau user name used only to verify the Connected App sign-in from the Tenant Master.</summary>
    public string? VerifyUserName { get; set; }
    public string TableauApiVersion { get; set; } = "3.21";

    /// <summary>
    /// Name of the secret (Power BI client secret or Tableau Connected App secret value).
    /// Entered in the Tenant Master; stored encrypted (AppSecret) locally and in AWS Secrets Manager in AWS. Never shown again.
    /// </summary>
    public string? SecretName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    // Result of the last "Verify connection" run.
    public DateTime? LastVerifiedAtUtc { get; set; }
    public bool? LastVerifyPassed { get; set; }
    /// <summary>JSON list of checks: name, passed, detail.</summary>
    public string? LastVerifyReport { get; set; }

    public List<PowerBiWorkspace> Workspaces { get; set; } = [];
}

public class PowerBiWorkspace
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Name { get; set; } = "";
    public Guid WorkspaceId { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>From the last verification: the service principal's access on the workspace (Admin, Member, Contributor, Viewer).</summary>
    public string? ServicePrincipalAccess { get; set; }
    /// <summary>From the last verification: whether the workspace is on a Fabric/Premium capacity (needed for embedding).</summary>
    public bool? OnDedicatedCapacity { get; set; }

    public BiTenant Tenant { get; set; } = null!;
}

/// <summary>Per dashboard type: what the publish form asks for and whether the type is switched on.</summary>
public class BiTypeConfig
{
    public BiType Type { get; set; }
    public string DisplayName { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    /// <summary>When the type is Inactive, also hide the dashboards of this type from the User Portal (C56). Off by default: they stay and show a badge.</summary>
    public bool HideWhenInactive { get; set; }
    public bool RequiresFile { get; set; }
    public bool RequiresUrl { get; set; }
    public bool RequiresTenant { get; set; }
    public bool SupportsRls { get; set; }
    public bool SupportsRefresh { get; set; }
    /// <summary>Comma-separated, e.g. ".pbix" or ".html".</summary>
    public string? AllowedExtensions { get; set; }
    public int? MaxFileSizeMb { get; set; }
}

public class ApprovedCdn
{
    public int Id { get; set; }
    public string Host { get; set; } = "";
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Key/value settings edited in Admin Configuration (branding, thresholds, switches).</summary>
public class SystemSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Description { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int? UpdatedByUserId { get; set; }
}

/// <summary>
/// Secrets entered in the Admin Portal (Power BI client secrets, Tableau Connected App secrets), encrypted with
/// ASP.NET Core Data Protection. Local and Docker builds only; AWS uses Secrets Manager.
/// </summary>
public class AppSecret
{
    public string Name { get; set; } = "";
    public string ProtectedValue { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
    public int? UpdatedByUserId { get; set; }
}
