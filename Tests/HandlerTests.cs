using Function.Contracts;
using Function.Features;
using Function.Features.Deprovision;
using Function.Features.Provision;
using Function.Infrastructure.MySql;
using Function.Infrastructure.Sites;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Tests;

public class ProvisionTenantSiteHandlerTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Host = "blog.acme.example.test";

    private readonly ITenantDatabases databases = Substitute.For<ITenantDatabases>();
    private readonly ITenantSites sites = Substitute.For<ITenantSites>();
    private readonly ProvisionTenantSiteHandler handler;
    private readonly List<string> calls = [];

    public ProvisionTenantSiteHandlerTests()
    {
        handler = new ProvisionTenantSiteHandler(databases, sites, NullLogger<ProvisionTenantSiteHandler>.Instance);

        databases.CreateAsync(Arg.Any<SiteKey>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            calls.Add("create-database");
            return new TenantDatabase("mysql:3306", call.Arg<SiteKey>().Database, call.Arg<SiteKey>().DatabaseUser, "secret", true);
        });
        sites.StartAsync(Arg.Any<SiteKey>(), Arg.Any<string>(), Arg.Any<TenantDatabase>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("start-site");
            return $"http://{Host}";
        });
    }

    private static ProvisionTenantSite Command(string tenant = "acme", string name = "blog") =>
        new(Id, tenant, name, Host);

    [Fact]
    public async Task CreatesTheDatabaseFirst_ThenStartsTheSite()
    {
        await handler.HandleAsync(Command(), CancellationToken.None);

        Assert.Equal(["create-database", "start-site"], calls);
    }

    [Fact]
    public async Task StartsTheSite_WithTheSameKey_TheCommandsHost_AndTheDatabaseJustCreated()
    {
        var expected = new SiteKey(Id, "acme", "blog");

        await handler.HandleAsync(Command(), CancellationToken.None);

        await databases.Received(1).CreateAsync(Arg.Is<SiteKey>(k => k == expected), Arg.Any<CancellationToken>());
        await sites.Received(1).StartAsync(
            Arg.Is<SiteKey>(k => k == expected),
            Host,
            Arg.Is<TenantDatabase>(d => d.Name == expected.Database && d.User == expected.DatabaseUser && d.Password == "secret"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReturnsTheSiteUrl()
    {
        var url = await handler.HandleAsync(Command(), CancellationToken.None);

        Assert.Equal($"http://{Host}", url);
    }

    [Fact]
    public async Task PassesTheCancellationTokenOn()
    {
        using var cts = new CancellationTokenSource();

        await handler.HandleAsync(Command(), cts.Token);

        await databases.Received(1).CreateAsync(Arg.Any<SiteKey>(), cts.Token);
        await sites.Received(1).StartAsync(Arg.Any<SiteKey>(), Arg.Any<string>(), Arg.Any<TenantDatabase>(), cts.Token);
    }

    [Fact]
    public async Task DatabaseCannotBeCreated_TheSiteIsNotStarted_AndTheErrorReachesTheCaller()
    {
        databases.CreateAsync(Arg.Any<SiteKey>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("mysql down"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(Command(), CancellationToken.None));

        Assert.Equal("mysql down", ex.Message);
        await sites.DidNotReceiveWithAnyArgs().StartAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task SiteCannotBeStarted_TheErrorReachesTheCaller_SoTheCommandIsRetried()
    {
        sites.StartAsync(default!, default!, default!, default).ReturnsForAnyArgs<string>(_ => throw new TimeoutException("never answered"));

        await Assert.ThrowsAsync<TimeoutException>(() => handler.HandleAsync(Command(), CancellationToken.None));
    }

    [Theory]
    [InlineData("Acme", "blog")]
    [InlineData("acme", "Blog")]
    [InlineData("acme", "blog.evil")]
    [InlineData("", "blog")]
    [InlineData("acme", "")]
    public async Task InvalidTenantOrName_FailsBeforeAnythingIsCreated(string tenant, string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(Command(tenant, name), CancellationToken.None));

        await databases.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
        await sites.DidNotReceiveWithAnyArgs().StartAsync(default!, default!, default!, default);
    }
}

public class DeprovisionTenantSiteHandlerTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly ITenantDatabases databases = Substitute.For<ITenantDatabases>();
    private readonly ITenantSites sites = Substitute.For<ITenantSites>();
    private readonly DeprovisionTenantSiteHandler handler;
    private readonly List<string> calls = [];

    public DeprovisionTenantSiteHandlerTests()
    {
        handler = new DeprovisionTenantSiteHandler(databases, sites, NullLogger<DeprovisionTenantSiteHandler>.Instance);

        sites.RemoveAsync(Arg.Any<SiteKey>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("remove-site");
            return Task.CompletedTask;
        });
        databases.DropAsync(Arg.Any<SiteKey>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("drop-database");
            return Task.CompletedTask;
        });
    }

    private static DeprovisionTenantSite Command(string tenant = "acme", string name = "blog") => new(Id, tenant, name);

    [Fact]
    public async Task RemovesTheSiteFirst_ThenDropsTheDatabase()
    {
        // The database must outlive the site: a running WordPress would otherwise lose it mid-request.
        await handler.HandleAsync(Command(), CancellationToken.None);

        Assert.Equal(["remove-site", "drop-database"], calls);
    }

    [Fact]
    public async Task BothSteps_UseTheSiteKeyOfTheProvision()
    {
        var expected = new SiteKey(Id, "acme", "blog");

        await handler.HandleAsync(Command(), CancellationToken.None);

        await sites.Received(1).RemoveAsync(Arg.Is<SiteKey>(k => k == expected), Arg.Any<CancellationToken>());
        await databases.Received(1).DropAsync(Arg.Is<SiteKey>(k => k == expected), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PassesTheCancellationTokenOn()
    {
        using var cts = new CancellationTokenSource();

        await handler.HandleAsync(Command(), cts.Token);

        await sites.Received(1).RemoveAsync(Arg.Any<SiteKey>(), cts.Token);
        await databases.Received(1).DropAsync(Arg.Any<SiteKey>(), cts.Token);
    }

    [Fact]
    public async Task SiteCannotBeRemoved_TheDatabaseIsKept_AndTheErrorReachesTheCaller()
    {
        sites.RemoveAsync(Arg.Any<SiteKey>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("api down"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(Command(), CancellationToken.None));

        Assert.Equal("api down", ex.Message);
        await databases.DidNotReceiveWithAnyArgs().DropAsync(default!, default);
    }

    [Fact]
    public async Task DatabaseCannotBeDropped_TheErrorReachesTheCaller_SoTheCommandIsRetried()
    {
        databases.DropAsync(Arg.Any<SiteKey>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("mysql"));

        await Assert.ThrowsAsync<TimeoutException>(() => handler.HandleAsync(Command(), CancellationToken.None));
    }

    [Fact]
    public async Task RepeatedCommand_RunsTheSameStepsAgain_WithoutFailing()
    {
        // Deprovision is idempotent; the handler adds nothing that would make a second run fail.
        await handler.HandleAsync(Command(), CancellationToken.None);
        await handler.HandleAsync(Command(), CancellationToken.None);

        Assert.Equal(["remove-site", "drop-database", "remove-site", "drop-database"], calls);
    }

    [Theory]
    [InlineData("Acme", "blog")]
    [InlineData("acme", "bad name")]
    [InlineData("", "blog")]
    public async Task InvalidTenantOrName_FailsBeforeAnythingIsTouched(string tenant, string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(Command(tenant, name), CancellationToken.None));

        await sites.DidNotReceiveWithAnyArgs().RemoveAsync(default!, default);
        await databases.DidNotReceiveWithAnyArgs().DropAsync(default!, default);
    }
}
