using Function.Contracts;
using Function.Infrastructure.Docker;
using Function.Infrastructure.MySql;

namespace Function.Features.Provision;

public class ProvisionTenantSiteHandler(ITenantDatabases databases, ITenantSites sites, ILogger<ProvisionTenantSiteHandler> logger)
{
    /// <summary>Creates the site's database first, then a WordPress container connected to it.</summary>
    public async Task<TenantSiteProvisioned> HandleAsync(ProvisionTenantSite command, CancellationToken ct)
    {
        var site = new SiteKey(command.Tenant, command.Name);
        logger.LogInformation("Provisioning {Site} ({ProvisionId})", command.Host, command.ProvisionId);

        var database = await databases.CreateAsync(site, ct);
        var url = await sites.StartAsync(site, database, ct);

        return new TenantSiteProvisioned(command.ProvisionId, url);
    }
}
