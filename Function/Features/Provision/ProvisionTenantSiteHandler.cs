using Function.Contracts;
using Function.Infrastructure.Sites;
using Function.Infrastructure.MySql;

namespace Function.Features.Provision;

public class ProvisionTenantSiteHandler(ITenantDatabases databases, ITenantSites sites, ILogger<ProvisionTenantSiteHandler> logger)
{
    /// <summary>Creates the site's database first, then the WordPress site connected to it. Returns the site URL.</summary>
    public async Task<string> HandleAsync(ProvisionTenantSite command, CancellationToken ct)
    {
        var site = new SiteKey(command.ProvisionId, command.Tenant, command.Name);
        logger.LogInformation("Provisioning {Site} ({ProvisionId})", command.Host, command.ProvisionId);

        var database = await databases.CreateAsync(site, ct);
        return await sites.StartAsync(site, command.Host, database, ct);
    }
}
