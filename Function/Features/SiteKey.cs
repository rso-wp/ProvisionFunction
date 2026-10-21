using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Function.Features;

/// <summary>
/// One WordPress site, <c>Name.Tenant.&lt;domain&gt;</c>, and the names of everything created for it. Both parts are
/// re-validated here because they end up in object names and labels; ProvisioningService and UserService apply the same
/// DNS-label rule.
///
/// Everything is named after the <see cref="ProvisionId"/>, not the site name: a site that is deleted and created again
/// under the same name gets new objects, so a late or repeated command for the old one can never touch the new one.
/// </summary>
public partial record SiteKey
{
    [GeneratedRegex("^[a-z0-9]([-a-z0-9]{0,28}[a-z0-9])?$")]
    private static partial Regex DnsLabel();

    private readonly string hash;

    public Guid ProvisionId { get; }
    public string Tenant { get; }
    public string Name { get; }

    public SiteKey(Guid provisionId, string tenant, string name)
    {
        ProvisionId = provisionId;
        Tenant = DnsLabel().IsMatch(tenant) ? tenant : throw new ArgumentException($"Invalid tenant '{tenant}'.", nameof(tenant));
        Name = DnsLabel().IsMatch(name) ? name : throw new ArgumentException($"Invalid site name '{name}'.", nameof(name));
        // MySQL user names are limited to 32 characters and Kubernetes names to DNS labels, so the objects are named after a
        // hash of the provision id.
        hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(provisionId.ToString())))[..20];
    }

    /// <summary>Docker container: readable, and unique per provision.</summary>
    public string Container => $"wp-{Name}.{Tenant}-{hash[..8]}";

    public string Volume => $"{Container}-html";

    public string Database => $"wp_{hash}";

    public string DatabaseUser => $"u_{hash}";

    /// <summary>Kubernetes objects (Secret, PVC, Deployment, Service, Ingress). The tenant and site are in the labels.</summary>
    public string ResourceName => $"wp-{hash}";

    public override string ToString() => $"{Name}.{Tenant}";
}
