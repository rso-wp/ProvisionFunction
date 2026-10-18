using System.Text.Json;
using Function.Contracts;
using Function.Features.Deprovision;
using Function.Features.Provision;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Function.Infrastructure.Nats;

/// <summary>
/// Durable JetStream consumer for provisioning commands. A message is acked only after its handler succeeded and the
/// resulting event was published; on failure it is redelivered with a growing delay, up to <see cref="MaxDeliver"/> times.
/// </summary>
public class CommandConsumer(
    INatsJSContext js,
    ProvisionTenantSiteHandler provision,
    DeprovisionTenantSiteHandler deprovision,
    ILogger<CommandConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "provision-function";
    private const int MaxDeliver = 5;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "NATS consumer stopped, reconnecting in 5s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await js.EnsureProvisioningStreamAsync(ct);

        var consumer = await js.CreateOrUpdateConsumerAsync(
            ProvisioningSubjects.Stream,
            new ConsumerConfig(ConsumerName)
            {
                FilterSubject = ProvisioningSubjects.Commands,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                AckWait = TimeSpan.FromMinutes(5), // pulling the wordpress image can take a while
                MaxDeliver = MaxDeliver
            },
            ct);

        logger.LogInformation("Listening on {Subject}", ProvisioningSubjects.Commands);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct))
        {
            var attempt = (int)(msg.Metadata?.NumDelivered ?? 1);
            try
            {
                await HandleAsync(msg.Subject, msg.Data ?? [], ct);
                await msg.AckAsync(cancellationToken: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (attempt < MaxDeliver)
                {
                    logger.LogWarning(ex, "{Subject} failed (attempt {Attempt}/{Max}), retrying", msg.Subject, attempt, MaxDeliver);
                    await msg.NakAsync(delay: TimeSpan.FromSeconds(10 * attempt), cancellationToken: ct);
                    continue;
                }

                logger.LogError(ex, "{Subject} failed permanently after {Attempt} attempts", msg.Subject, attempt);
                await HandleFinalFailureAsync(msg.Subject, msg.Data ?? [], ex, ct);
                await msg.AckAsync(cancellationToken: ct);
            }
        }
    }

    private async Task HandleAsync(string subject, byte[] data, CancellationToken ct)
    {
        switch (subject)
        {
            case ProvisioningSubjects.Provision:
                var started = await provision.HandleAsync(Deserialize<ProvisionTenantSite>(data), ct);
                await js.PublishJsonAsync(ProvisioningSubjects.Provisioned, started, ct);
                break;

            case ProvisioningSubjects.Deprovision:
                var destroyed = await deprovision.HandleAsync(Deserialize<DeprovisionTenantSite>(data), ct);
                await js.PublishJsonAsync(ProvisioningSubjects.Destroyed, destroyed, ct);
                break;

            default:
                logger.LogWarning("Ignoring message on unknown subject {Subject}", subject);
                break;
        }
    }

    private async Task HandleFinalFailureAsync(string subject, byte[] data, Exception ex, CancellationToken ct)
    {
        // Provisioning: tell ProvisioningService so the site shows as Failed. Deprovisioning: the row stays Deleting
        // and the resources must be cleaned up by hand (see the log).
        if (subject == ProvisioningSubjects.Provision)
        {
            var command = Deserialize<ProvisionTenantSite>(data);
            await js.PublishJsonAsync(
                ProvisioningSubjects.ProvisioningFailed,
                new TenantSiteProvisioningFailed(command.ProvisionId, ex.Message),
                ct);
        }
    }

    private static T Deserialize<T>(byte[] data) =>
        JsonSerializer.Deserialize<T>(data, NatsExtensions.Json)
        ?? throw new InvalidOperationException($"Empty {typeof(T).Name} message.");
}
