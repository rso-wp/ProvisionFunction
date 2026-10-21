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

    /// <summary>Events that cannot be read (not valid JSON) are moved here by ProvisioningService, for inspection.</summary>
    public const string EventsPoison = "provisioning-events-poison";

    /// <summary>
    /// Attempts ProvisionFunction makes at one command, with growing delays in between (about 45 minutes in all). After the
    /// last one a provision reports provisioning-failed and a deprovision gives up; ProvisioningService sends the command
    /// again an hour later, for as long as the site has not reached its end state (ProvisioningReconciler).
    /// </summary>
    public const int MaxAttempts = 8;

    /// <summary>Time to live of every message: they never expire (the default is 7 days).</summary>
    public static readonly TimeSpan Forever = TimeSpan.FromSeconds(-1);
}

/// <summary>Start the site <c>Name.Tenant.&lt;domain&gt;</c>; <see cref="Host"/> is that full host name.</summary>
/// <param name="Attempt">Failed attempts so far: 0 from ProvisioningService, counted up by ProvisionFunction when it retries.</param>
public record ProvisionTenantSite(Guid ProvisionId, string Tenant, string Name, string Host, int Attempt = 0);

/// <param name="Attempt">Failed attempts so far: 0 from ProvisioningService, counted up by ProvisionFunction when it retries.</param>
public record DeprovisionTenantSite(Guid ProvisionId, string Tenant, string Name, int Attempt = 0);

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
