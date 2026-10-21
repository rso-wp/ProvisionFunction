using Azure.Storage.Queues.Models;
using Function.Contracts;
using Function.Infrastructure.Queues;
using Microsoft.Azure.Functions.Worker;

namespace Function.Features.Provision;

public class ProvisionSiteFunction(
    ProvisionTenantSiteHandler handler,
    ProvisioningEventPublisher events,
    ILogger<ProvisionSiteFunction> logger)
{
    /// <summary>
    /// A failure throws, so the queue redelivers the command after the visibility timeout (host.json). The last attempt
    /// reports <see cref="ProvisioningEventTypes.ProvisioningFailed"/> instead, so the site shows as Failed.
    /// </summary>
    [Function("ProvisionSite")]
    public async Task Run(
        [QueueTrigger(ProvisioningQueues.Provision, Connection = ProvisioningQueues.Connection)] QueueMessage message,
        CancellationToken ct)
    {
        var command = QueueExtensions.Deserialize<ProvisionTenantSite>(message.Body);
        try
        {
            var url = await handler.HandleAsync(command, ct);
            await events.PublishAsync(
                new ProvisioningEvent(ProvisioningEventTypes.Provisioned, command.ProvisionId, command.Tenant, command.Name, Url: url),
                ct);
        }
        catch (Exception ex) when (message.DequeueCount >= ProvisioningQueues.MaxAttempts && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Provisioning {ProvisionId} failed permanently after {Attempts} attempts",
                command.ProvisionId, message.DequeueCount);
            await events.PublishAsync(
                new ProvisioningEvent(ProvisioningEventTypes.ProvisioningFailed, command.ProvisionId, command.Tenant, command.Name,
                    Reason: ex.Message),
                ct);
        }
    }
}
