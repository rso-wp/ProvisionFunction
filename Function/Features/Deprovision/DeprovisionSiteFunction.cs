using Azure.Storage.Queues.Models;
using Function.Contracts;
using Function.Infrastructure.Queues;
using Microsoft.Azure.Functions.Worker;

namespace Function.Features.Deprovision;

public class DeprovisionSiteFunction(DeprovisionTenantSiteHandler handler, ProvisioningEventPublisher events)
{
    /// <summary>
    /// A failure throws and is retried (host.json). After the last attempt the command lands in
    /// <c>deprovision-site-poison</c>, the row stays Deleting, and the resources must be cleaned up by hand.
    /// </summary>
    [Function("DeprovisionSite")]
    public async Task Run(
        [QueueTrigger(ProvisioningQueues.Deprovision, Connection = ProvisioningQueues.Connection)] QueueMessage message,
        CancellationToken ct)
    {
        var command = QueueExtensions.Deserialize<DeprovisionTenantSite>(message.Body);
        await handler.HandleAsync(command, ct);
        await events.PublishAsync(
            new ProvisioningEvent(ProvisioningEventTypes.Destroyed, command.ProvisionId, command.Tenant, command.Name),
            ct);
    }
}
