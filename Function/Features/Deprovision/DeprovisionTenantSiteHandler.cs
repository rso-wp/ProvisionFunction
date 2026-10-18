using Function.Contracts;
using Function.Infrastructure.Docker;
using Function.Infrastructure.MySql;

namespace Function.Features.Deprovision;

public class DeprovisionTenantSiteHandler(ITenantDatabases databases, ITenantSites sites, ILogger<DeprovisionTenantSiteHandler> logger)
{
    /// <summary>Removes the container (and its volume) first, then drops the database and its user.</summary>
    public async Task<TenantSiteDestroyed> HandleAsync(DeprovisionTenantSite command, CancellationToken ct)
    {
        var site = new SiteKey(command.Tenant, command.Name);
        logger.LogInformation("Deprovisioning {Site} ({ProvisionId})", site, command.ProvisionId);

        await sites.RemoveAsync(site, ct);
        await databases.DropAsync(site, ct);

        return new TenantSiteDestroyed(command.ProvisionId);
    }
}
