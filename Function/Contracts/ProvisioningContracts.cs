namespace Function.Contracts;

// Wire contract shared with ProvisioningService (copy in ProvisioningService/Api/Infrastructure/Messaging).
// Keep both copies in sync. Messages are plain JSON (camelCase, not base64) on Azure Storage Queues.

public static class ProvisioningQueues
{
    /// <summary>
    /// Setting with the queues' storage account: a connection string, or <c>ProvisioningQueues__queueServiceUri</c> for a
    /// managed identity.
    /// </summary>
    public const string Connection = "ProvisioningQueues";

    // ProvisioningService -> ProvisionFunction
    public const string Provision = "provision-site";
    public const string Deprovision = "deprovision-site";

    // ProvisionFunction -> ProvisioningService
    public const string Events = "provisioning-events";

    /// <summary>Attempts per command; the same value as <c>maxDequeueCount</c> in host.json.</summary>
    public const int MaxAttempts = 5;
}

/// <summary>Start the site <c>Name.Tenant.&lt;domain&gt;</c>; <see cref="Host"/> is that full host name.</summary>
public record ProvisionTenantSite(Guid ProvisionId, string Tenant, string Name, string Host);

public record DeprovisionTenantSite(Guid ProvisionId, string Tenant, string Name);

public static class ProvisioningEventTypes
{
    public const string Provisioned = "provisioned";
    public const string ProvisioningFailed = "provisioning-failed";
    public const string Destroyed = "destroyed";
}

/// <summary>
/// Result of a command (<see cref="ProvisioningEventTypes"/>). Carries the site, so ProvisioningService can clean up a site
/// whose row was deleted while it was being created (the queues do not keep commands in order).
/// </summary>
public record ProvisioningEvent(string Type, Guid ProvisionId, string Tenant, string Name, string? Url = null, string? Reason = null);
