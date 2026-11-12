using System.Text.Json;
using Function.Contracts;
using Function.Infrastructure.Queues;

namespace Tests;

/// <summary>How long a failed command waits before it is tried again.</summary>
public class CommandRetriesDelayTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    [InlineData(6, 900)]   // 960 s capped at 15 minutes
    [InlineData(7, 900)]
    public void Delay_Doubles_FromThirtySeconds_UpToFifteenMinutes(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), CommandRetries.Delay(failures));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Delay_WithNoFailures_IsTheFirstDelay(int failures) =>
        Assert.Equal(TimeSpan.FromSeconds(30), CommandRetries.Delay(failures));

    [Theory]
    [InlineData(11)]
    [InlineData(50)]
    [InlineData(int.MaxValue)]
    public void Delay_NeverOverflows_AndStaysAtTheCap(int failures) =>
        Assert.Equal(TimeSpan.FromMinutes(15), CommandRetries.Delay(failures));

    [Fact]
    public void Delay_NeverShrinks_AsFailuresGrow()
    {
        var previous = TimeSpan.Zero;
        for (var failures = 1; failures <= 30; failures++)
        {
            var delay = CommandRetries.Delay(failures);
            Assert.True(delay >= previous, $"delay went down at failure {failures}");
            previous = delay;
        }
    }

    [Fact]
    public void AllAttempts_SpanAboutFortyFiveMinutes()
    {
        // ProvisioningService only resends a command after an hour, i.e. once these retries are over, and the docs promise
        // "about 45 minutes". The last attempt is not followed by a retry, so there are MaxAttempts - 1 delays.
        var total = TimeSpan.Zero;
        for (var failures = 1; failures < ProvisioningQueues.MaxAttempts; failures++) total += CommandRetries.Delay(failures);

        Assert.InRange(total, TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(50));
        Assert.True(total < TimeSpan.FromHours(1), "retries must finish before ProvisioningService resends the command");
    }

    [Fact]
    public void MaxAttempts_IsEight() => Assert.Equal(8, ProvisioningQueues.MaxAttempts);
}

/// <summary>
/// The queue messages are a contract with ProvisioningService, which keeps its own copy of these types. A change in the
/// JSON here breaks it silently, so the shape is pinned.
/// </summary>
public class QueueContractTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static BinaryData Body(string json) => BinaryData.FromString(json);

    [Fact]
    public void QueueNames_AreThoseProvisioningServiceUses()
    {
        Assert.Equal("provision-site", ProvisioningQueues.Provision);
        Assert.Equal("deprovision-site", ProvisioningQueues.Deprovision);
        Assert.Equal("provisioning-events", ProvisioningQueues.Events);
        Assert.Equal("provisioning-events-poison", ProvisioningQueues.EventsPoison);
    }

    [Fact]
    public void EventTypes_AreThoseProvisioningServiceUnderstands()
    {
        Assert.Equal("provisioned", ProvisioningEventTypes.Provisioned);
        Assert.Equal("provisioning-failed", ProvisioningEventTypes.ProvisioningFailed);
        Assert.Equal("destroyed", ProvisioningEventTypes.Destroyed);
    }

    [Fact]
    public void Messages_NeverExpire() => Assert.Equal(TimeSpan.FromSeconds(-1), ProvisioningQueues.Forever);

    [Fact]
    public void ProvisionCommand_IsReadFromCamelCaseJson()
    {
        var command = QueueExtensions.Deserialize<ProvisionTenantSite>(Body(
            $$"""{"provisionId":"{{Id}}","tenant":"acme","name":"blog","host":"blog.acme.example.test","attempt":3}"""));

        Assert.Equal(new ProvisionTenantSite(Id, "acme", "blog", "blog.acme.example.test", 3), command);
    }

    [Fact]
    public void ProvisionCommand_FromProvisioningService_HasNoAttempt_SoItStartsAtZero()
    {
        var command = QueueExtensions.Deserialize<ProvisionTenantSite>(Body(
            $$"""{"provisionId":"{{Id}}","tenant":"acme","name":"blog","host":"blog.acme.example.test"}"""));

        Assert.Equal(0, command.Attempt);
    }

    [Fact]
    public void DeprovisionCommand_IsReadFromCamelCaseJson_WithAttemptDefaultingToZero()
    {
        var command = QueueExtensions.Deserialize<DeprovisionTenantSite>(Body(
            $$"""{"provisionId":"{{Id}}","tenant":"acme","name":"blog"}"""));

        Assert.Equal(new DeprovisionTenantSite(Id, "acme", "blog", 0), command);
    }

    [Fact]
    public void Commands_ReadPropertyNamesIgnoringCase()
    {
        // JsonSerializerDefaults.Web: PascalCase from another writer is read too.
        var command = QueueExtensions.Deserialize<DeprovisionTenantSite>(Body(
            $$"""{"ProvisionId":"{{Id}}","Tenant":"acme","Name":"blog"}"""));

        Assert.Equal(Id, command.ProvisionId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    public void EmptyBody_Throws(string json) =>
        Assert.ThrowsAny<Exception>(() => QueueExtensions.Deserialize<ProvisionTenantSite>(Body(json)));

    [Fact]
    public void NotJson_Throws() =>
        Assert.ThrowsAny<JsonException>(() => QueueExtensions.Deserialize<ProvisionTenantSite>(Body("not json")));

    [Fact]
    public void ProvisionedEvent_IsWrittenAsCamelCase_WithTheUrl()
    {
        var json = BinaryData.FromObjectAsJson(
            new ProvisioningEvent(ProvisioningEventTypes.Provisioned, Id, "acme", "blog", Url: "http://blog.acme.example.test"),
            QueueExtensions.Json).ToString();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("provisioned", root.GetProperty("type").GetString());
        Assert.Equal(Id, root.GetProperty("provisionId").GetGuid());
        Assert.Equal("acme", root.GetProperty("tenant").GetString());
        Assert.Equal("blog", root.GetProperty("name").GetString());
        Assert.Equal("http://blog.acme.example.test", root.GetProperty("url").GetString());
    }

    [Fact]
    public void FailedEvent_CarriesTheReason()
    {
        var json = BinaryData.FromObjectAsJson(
            new ProvisioningEvent(ProvisioningEventTypes.ProvisioningFailed, Id, "acme", "blog", Reason: "no capacity"),
            QueueExtensions.Json).ToString();

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("provisioning-failed", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("no capacity", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void Event_RoundTrips()
    {
        var original = new ProvisioningEvent(ProvisioningEventTypes.Destroyed, Id, "acme", "blog");

        var read = QueueExtensions.Deserialize<ProvisioningEvent>(
            BinaryData.FromObjectAsJson(original, QueueExtensions.Json));

        Assert.Equal(original, read);
    }

    [Fact]
    public void RetryOfACommand_KeepsEverythingAndRaisesOnlyTheAttempt()
    {
        var command = new ProvisionTenantSite(Id, "acme", "blog", "blog.acme.example.test");

        var retry = QueueExtensions.Deserialize<ProvisionTenantSite>(
            BinaryData.FromObjectAsJson(command with { Attempt = 2 }, QueueExtensions.Json));

        Assert.Equal(command with { Attempt = 2 }, retry);
    }
}
