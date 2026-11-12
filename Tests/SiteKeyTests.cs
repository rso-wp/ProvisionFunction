using System.Text.RegularExpressions;
using Function.Features;

namespace Tests;

/// <summary>
/// Everything created for a site (database, user, Kubernetes objects, container) is named from its SiteKey. A mistake here
/// can make a command touch another site's data.
/// </summary>
public class SiteKeyTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    // ---- validation ----

    [Theory]
    [InlineData("blog")]
    [InlineData("a")]
    [InlineData("my-blog")]
    [InlineData("site2")]
    [InlineData("0day")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123")] // 30 characters, the longest allowed
    public void ValidLabels_AreAccepted(string label)
    {
        var key = new SiteKey(Id, label, label);

        Assert.Equal(label, key.Tenant);
        Assert.Equal(label, key.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Blog")]
    [InlineData("BLOG")]
    [InlineData("-blog")]
    [InlineData("blog-")]
    [InlineData("my_blog")]
    [InlineData("my.blog")]
    [InlineData("my blog")]
    [InlineData("blog/../x")]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234")] // 31 characters
    public void InvalidName_IsRejected(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => new SiteKey(Id, "acme", name));

        Assert.Equal("name", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Acme")]
    [InlineData("acme-")]
    [InlineData("-acme")]
    [InlineData("acme.corp")]
    [InlineData("acme'; DROP DATABASE x; --")]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234")]
    public void InvalidTenant_IsRejected(string tenant)
    {
        var ex = Assert.Throws<ArgumentException>(() => new SiteKey(Id, tenant, "blog"));

        Assert.Equal("tenant", ex.ParamName);
    }

    // ---- naming ----

    [Fact]
    public void SameProvision_AlwaysGivesTheSameNames()
    {
        var a = new SiteKey(Id, "acme", "blog");
        var b = new SiteKey(Id, "acme", "blog");

        Assert.Equal(a.Database, b.Database);
        Assert.Equal(a.DatabaseUser, b.DatabaseUser);
        Assert.Equal(a.ResourceName, b.ResourceName);
        Assert.Equal(a.Container, b.Container);
        Assert.Equal(a.Volume, b.Volume);
    }

    [Fact]
    public void RecreatedSiteWithTheSameName_GetsNewObjects()
    {
        // A late or repeated command for the old site must never touch the new one.
        var old = new SiteKey(Guid.NewGuid(), "acme", "blog");
        var recreated = new SiteKey(Guid.NewGuid(), "acme", "blog");

        Assert.NotEqual(old.Database, recreated.Database);
        Assert.NotEqual(old.DatabaseUser, recreated.DatabaseUser);
        Assert.NotEqual(old.ResourceName, recreated.ResourceName);
        Assert.NotEqual(old.Container, recreated.Container);
        Assert.NotEqual(old.Volume, recreated.Volume);
    }

    [Fact]
    public void DifferentSites_NeverShareObjects()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => new SiteKey(Guid.NewGuid(), "acme", "blog")).ToList();

        Assert.Equal(200, keys.Select(k => k.Database).Distinct().Count());
        Assert.Equal(200, keys.Select(k => k.DatabaseUser).Distinct().Count());
        Assert.Equal(200, keys.Select(k => k.ResourceName).Distinct().Count());
    }

    [Fact]
    public void NamesDoNotDependOnTheSiteNameOrTenant()
    {
        // Same provision id: the objects are named after the id alone, the readable parts only appear in the container.
        var a = new SiteKey(Id, "acme", "blog");
        var b = new SiteKey(Id, "globex", "shop");

        Assert.Equal(a.Database, b.Database);
        Assert.Equal(a.DatabaseUser, b.DatabaseUser);
        Assert.Equal(a.ResourceName, b.ResourceName);
    }

    [Fact]
    public void Names_HaveTheDocumentedShape()
    {
        var key = new SiteKey(Id, "acme", "blog");

        Assert.Matches("^wp_[0-9a-f]{20}$", key.Database);
        Assert.Matches("^u_[0-9a-f]{20}$", key.DatabaseUser);
        Assert.Matches("^wp-[0-9a-f]{20}$", key.ResourceName);
        Assert.Matches("^wp-blog\\.acme-[0-9a-f]{8}$", key.Container);
        Assert.Equal($"{key.Container}-html", key.Volume);
        Assert.Equal("blog.acme", key.ToString());
    }

    [Fact]
    public void ContainerSuffix_IsThePrefixOfTheHashUsedByTheOtherObjects()
    {
        var key = new SiteKey(Id, "acme", "blog");
        var hash = key.ResourceName["wp-".Length..];

        Assert.EndsWith("-" + hash[..8], key.Container);
        Assert.Equal("wp_" + hash, key.Database);
        Assert.Equal("u_" + hash, key.DatabaseUser);
    }

    [Fact]
    public void Names_RespectTheLimitsOfMySqlAndKubernetes()
    {
        var key = new SiteKey(Guid.NewGuid(), "abcdefghijklmnopqrstuvwxyz0123", "abcdefghijklmnopqrstuvwxyz0123");

        Assert.True(key.DatabaseUser.Length <= 32, "MySQL user names are limited to 32 characters");
        Assert.True(key.ResourceName.Length <= 63, "Kubernetes names are limited to a DNS label");
        Assert.Matches("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", key.ResourceName);
    }

    [Fact]
    public void Names_AreSafeToUseInSqlIdentifiers()
    {
        // MySqlTenantDatabases interpolates these into SQL, so they may only contain letters, digits and underscores.
        var key = new SiteKey(Guid.NewGuid(), "acme", "blog");

        Assert.Matches(new Regex("^[a-z0-9_]+$"), key.Database);
        Assert.Matches(new Regex("^[a-z0-9_]+$"), key.DatabaseUser);
    }
}
