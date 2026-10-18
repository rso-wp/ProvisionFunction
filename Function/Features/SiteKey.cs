using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Function.Features;

/// <summary>
/// One WordPress site, <c>Name.Tenant.&lt;domain&gt;</c>, and the names of everything created for it. Both parts are
/// re-validated here because they end up in SQL identifiers and docker object names; ProvisioningService and UserService
/// apply the same DNS-label rule.
/// </summary>
public partial record SiteKey
{
    [GeneratedRegex("^[a-z0-9]([-a-z0-9]{0,28}[a-z0-9])?$")]
    private static partial Regex DnsLabel();

    public string Tenant { get; }
    public string Name { get; }

    public SiteKey(string tenant, string name)
    {
        Tenant = DnsLabel().IsMatch(tenant) ? tenant : throw new ArgumentException($"Invalid tenant '{tenant}'.", nameof(tenant));
        Name = DnsLabel().IsMatch(name) ? name : throw new ArgumentException($"Invalid site name '{name}'.", nameof(name));
    }

    /// <summary>Docker container: readable and unique ("." cannot occur in either part).</summary>
    public string Container => $"wp-{Name}.{Tenant}";

    public string Volume => $"{Container}-html";

    // MySQL user names are limited to 32 characters and '-' would have to become '_' (which could make two sites
    // collide), so the database and its user are named after a hash of the site instead.
    public string Database => $"wp_{Hash}";

    public string DatabaseUser => $"u_{Hash}";

    private string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Name}.{Tenant}")))[..20];

    public override string ToString() => $"{Name}.{Tenant}";
}
