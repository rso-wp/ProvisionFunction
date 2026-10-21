using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Queues;
using Function.Contracts;

namespace Function.Infrastructure.Queues;

public static class QueueExtensions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Client for the provisioning queues, from the same setting the queue triggers use: a connection string in
    /// <c>ProvisioningQueues</c>, or <c>ProvisioningQueues__queueServiceUri</c> with the function's managed identity.
    /// </summary>
    public static IServiceCollection AddProvisioningQueues(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ =>
        {
            var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None };
            var connectionString = configuration[ProvisioningQueues.Connection];
            if (!string.IsNullOrWhiteSpace(connectionString)) return new QueueServiceClient(connectionString, options);

            var uri = configuration[$"{ProvisioningQueues.Connection}:queueServiceUri"]
                ?? throw new InvalidOperationException(
                    $"'{ProvisioningQueues.Connection}' (connection string) or '{ProvisioningQueues.Connection}__queueServiceUri' is not configured.");
            return new QueueServiceClient(new Uri(uri), new DefaultAzureCredential(), options);
        });
        services.AddSingleton<ProvisioningEventPublisher>();
        services.AddSingleton<CommandRetries>();

        return services;
    }

    public static T Deserialize<T>(BinaryData body) =>
        body.ToObjectFromJson<T>(Json) ?? throw new InvalidOperationException($"Empty {typeof(T).Name} message.");
}

/// <summary>Sends results to ProvisioningService (queue <see cref="ProvisioningQueues.Events"/>).</summary>
public class ProvisioningEventPublisher(QueueServiceClient queues)
{
    private readonly QueueClient queue = queues.GetQueueClient(ProvisioningQueues.Events);

    public async Task PublishAsync(ProvisioningEvent e, CancellationToken ct) =>
        await queue.SendMessageAsync(
            BinaryData.FromObjectAsJson(e, QueueExtensions.Json), timeToLive: ProvisioningQueues.Forever, cancellationToken: ct);
}

/// <summary>
/// Retries a failed command by queueing it again, invisible for a while: 30 s after the first failure, doubling up to
/// 15 minutes. The queue trigger's own retries (host.json) have a fixed 30 s delay and would use up all attempts within a
/// few minutes of an outage; with these, <see cref="ProvisioningQueues.MaxAttempts"/> attempts span about 45 minutes.
/// </summary>
public class CommandRetries(QueueServiceClient queues)
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    public static TimeSpan Delay(int failures) =>
        TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, FirstDelay.Ticks << Math.Clamp(failures - 1, 0, 10)));

    /// <param name="failures">Failed attempts so far, including the one that just failed (the command's new Attempt).</param>
    public async Task ScheduleAsync<T>(string queue, T command, int failures, CancellationToken ct) =>
        await queues.GetQueueClient(queue).SendMessageAsync(
            BinaryData.FromObjectAsJson(command, QueueExtensions.Json),
            visibilityTimeout: Delay(failures),
            timeToLive: ProvisioningQueues.Forever,
            cancellationToken: ct);
}
