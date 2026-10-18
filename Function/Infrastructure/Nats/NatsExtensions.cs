using System.Text.Json;
using Function.Contracts;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;

namespace Function.Infrastructure.Nats;

public class NatsOptions
{
    public const string SectionName = "Nats";
    public string Url { get; set; } = "nats://localhost:4222";
}

public static class NatsExtensions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddNats(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(NatsOptions.SectionName).Get<NatsOptions>() ?? new NatsOptions();

        services.AddSingleton(new NatsClient(options.Url));
        services.AddSingleton(sp => sp.GetRequiredService<NatsClient>().CreateJetStreamContext());

        return services;
    }

    /// <summary>Creates the stream if missing. ProvisioningService declares the same stream.</summary>
    public static async Task EnsureProvisioningStreamAsync(this INatsJSContext js, CancellationToken ct)
    {
        try
        {
            await js.GetStreamAsync(ProvisioningSubjects.Stream, cancellationToken: ct);
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 404)
        {
            await js.CreateStreamAsync(
                new StreamConfig(ProvisioningSubjects.Stream, [ProvisioningSubjects.All])
                {
                    Storage = StreamConfigStorage.File,
                    MaxAge = TimeSpan.FromDays(7)
                },
                ct);
        }
    }

    public static async Task PublishJsonAsync<T>(this INatsJSContext js, string subject, T message, CancellationToken ct)
    {
        var ack = await js.PublishAsync(subject, JsonSerializer.SerializeToUtf8Bytes(message, Json), cancellationToken: ct);
        ack.EnsureSuccess();
    }
}
