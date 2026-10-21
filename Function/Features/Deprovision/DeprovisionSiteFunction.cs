using Azure.Storage.Queues.Models;
using Function.Contracts;
using Function.Infrastructure.Queues;
using Microsoft.Azure.Functions.Worker;

namespace Function.Features.Deprovision;

public class DeprovisionSiteFunction(
    DeprovisionTenantSiteHandler handler,
    ProvisioningEventPublisher events,
    CommandRetries retries,
    ILogger<DeprovisionSiteFunction> logger)
{
    /// <summary>
    /// A failure queues the command again with a growing delay (<see cref="CommandRetries"/>), up to
    /// <see cref="ProvisioningQueues.MaxAttempts"/> attempts. Then it gives up for now: the site stays Deleting, and
    /// ProvisioningService sends the command again an hour later, until the site is gone.
    /// </summary>
    [Function("DeprovisionSite")]
    public async Task Run(
        [QueueTrigger(ProvisioningQueues.Deprovision, Connection = ProvisioningQueues.Connection)] QueueMessage message,
        CancellationToken ct)
    {
        var command = QueueExtensions.Deserialize<DeprovisionTenantSite>(message.Body);
        try
        {
            await handler.HandleAsync(command, ct);
            await events.PublishAsync(
                new ProvisioningEvent(ProvisioningEventTypes.Destroyed, command.ProvisionId, command.Tenant, command.Name),
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var failures = command.Attempt + 1;
            if (failures < ProvisioningQueues.MaxAttempts)
            {
                logger.LogWarning(ex, "Deprovisioning {ProvisionId} failed (attempt {Attempt} of {Max}), retrying in {Delay}",
                    command.ProvisionId, failures, ProvisioningQueues.MaxAttempts, CommandRetries.Delay(failures));
                await retries.ScheduleAsync(ProvisioningQueues.Deprovision, command with { Attempt = failures }, failures, ct);
                return;
            }

            logger.LogError(ex, "Deprovisioning {ProvisionId} failed {Attempts} times; ProvisioningService will send it again",
                command.ProvisionId, failures);
        }
    }
}
