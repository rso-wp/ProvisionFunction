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
        await queue.SendMessageAsync(BinaryData.FromObjectAsJson(e, QueueExtensions.Json), cancellationToken: ct);
}
