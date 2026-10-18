namespace Function.Contracts;

// Wire contract shared with ProvisioningService (copy in ProvisioningService/Api/Infrastructure/Messaging).
// Keep both copies in sync. Payloads are JSON (camelCase) on NATS JetStream stream PROVISIONING.

public static class ProvisioningSubjects
{
    public const string Stream = "PROVISIONING";
    public const string All = "provisioning.>";

    // ProvisioningService -> ProvisionFunction
    public const string Commands = "provisioning.commands.>";
    public const string Provision = "provisioning.commands.provision";
    public const string Deprovision = "provisioning.commands.deprovision";

    // ProvisionFunction -> ProvisioningService
    public const string Events = "provisioning.events.>";
    public const string Provisioned = "provisioning.events.provisioned";
    public const string ProvisioningFailed = "provisioning.events.provisioning-failed";
    public const string Destroyed = "provisioning.events.destroyed";
}

/// <summary>Start the site <c>Name.Tenant.&lt;domain&gt;</c>; <see cref="Host"/> is that full host name.</summary>
public record ProvisionTenantSite(Guid ProvisionId, string Tenant, string Name, string Host);

public record DeprovisionTenantSite(Guid ProvisionId, string Tenant, string Name);

public record TenantSiteProvisioned(Guid ProvisionId, string Url);

public record TenantSiteProvisioningFailed(Guid ProvisionId, string Reason);

public record TenantSiteDestroyed(Guid ProvisionId);
