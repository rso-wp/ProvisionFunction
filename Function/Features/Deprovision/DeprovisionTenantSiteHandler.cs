using Function.Contracts;
using Function.Infrastructure.Sites;
using Function.Infrastructure.MySql;

namespace Function.Features.Deprovision;

public class DeprovisionTenantSiteHandler(ITenantDatabases databases, ITenantSites sites, ILogger<DeprovisionTenantSiteHandler> logger)
{
    /// <summary>Removes the site (and its storage) first, then drops the database and its user. Idempotent.</summary>
    public async Task HandleAsync(DeprovisionTenantSite command, CancellationToken ct)
    {
        var site = new SiteKey(command.Tenant, command.Name);
        logger.LogInformation("Deprovisioning {Site} ({ProvisionId})", site, command.ProvisionId);

        await sites.RemoveAsync(site, ct);
        await databases.DropAsync(site, ct);
    }
}
