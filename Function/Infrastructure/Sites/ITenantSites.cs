using Function.Features;
using Function.Infrastructure.MySql;

namespace Function.Infrastructure.Sites;

/// <summary>Where the WordPress sites run: a local docker (<c>Sites:Backend = Docker</c>) or Kubernetes (<c>Kubernetes</c>).</summary>
public interface ITenantSites
{
    /// <summary>
    /// (Re)creates and starts the site's WordPress, connected to <paramref name="database"/>, and waits until it answers.
    /// <paramref name="host"/> is its public host name. Returns the site URL.
    /// </summary>
    Task<string> StartAsync(SiteKey site, string host, TenantDatabase database, CancellationToken ct);

    /// <summary>Removes the site and its storage. Missing parts are ignored.</summary>
    Task RemoveAsync(SiteKey site, CancellationToken ct);
}
