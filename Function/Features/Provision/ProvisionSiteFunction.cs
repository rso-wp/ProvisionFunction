using Azure.Storage.Queues.Models;
using Function.Contracts;
using Function.Infrastructure.Queues;
using Microsoft.Azure.Functions.Worker;

namespace Function.Features.Provision;

public class ProvisionSiteFunction(
    ProvisionTenantSiteHandler handler,
    ProvisioningEventPublisher events,
    CommandRetries retries,
    ILogger<ProvisionSiteFunction> logger)
{
    /// <summary>
    /// A failure queues the command again with a growing delay (<see cref="CommandRetries"/>). After
    /// <see cref="ProvisioningQueues.MaxAttempts"/> attempts it reports <see cref="ProvisioningEventTypes.ProvisioningFailed"/>
    /// (the site shows as Failed); ProvisioningService keeps sending the command now and then, and a site that comes up
    /// later shows as running. Only a crash or a failure to queue the retry throws (host.json then retries the message).
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var failures = command.Attempt + 1;
            if (failures < ProvisioningQueues.MaxAttempts)
            {
                logger.LogWarning(ex, "Provisioning {ProvisionId} failed (attempt {Attempt} of {Max}), retrying in {Delay}",
                    command.ProvisionId, failures, ProvisioningQueues.MaxAttempts, CommandRetries.Delay(failures));
                await retries.ScheduleAsync(ProvisioningQueues.Provision, command with { Attempt = failures }, failures, ct);
                return;
            }

            logger.LogError(ex, "Provisioning {ProvisionId} failed {Attempts} times; reporting it as failed",
                command.ProvisionId, failures);
            await events.PublishAsync(
                new ProvisioningEvent(ProvisioningEventTypes.ProvisioningFailed, command.ProvisionId, command.Tenant, command.Name,
                    Reason: ex.Message),
                ct);
        }
    }
}
