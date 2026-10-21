using Docker.DotNet;
using Function.Infrastructure.Docker;
using Function.Infrastructure.Kubernetes;
using Function.Infrastructure.MySql;
using Function.Infrastructure.Sites;
using Microsoft.Extensions.Options;

namespace Function.Infrastructure;

public static class InfrastructureExtensions
{
    public static IServiceCollection AddTenantDatabases(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MySqlOptions>()
            .Bind(configuration.GetSection(MySqlOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.AdminConnectionString), "'MySql:AdminConnectionString' is not configured.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.HostForSites), "'MySql:HostForSites' is not configured.")
            .ValidateOnStart();
        services.AddSingleton<ITenantDatabases, MySqlTenantDatabases>();

        return services;
    }

    /// <summary><c>Sites:Backend</c>: <c>Docker</c> (default, local development) or <c>Kubernetes</c> (AKS).</summary>
    public static IServiceCollection AddTenantSites(this IServiceCollection services, IConfiguration configuration)
    {
        var backend = configuration["Sites:Backend"] is { Length: > 0 } value ? value : "Docker";

        if (backend.Equals("Kubernetes", StringComparison.OrdinalIgnoreCase))
        {
            services.AddOptions<KubernetesOptions>().Bind(configuration.GetSection(KubernetesOptions.SectionName));
            services.AddSingleton(sp => KubernetesOptions.CreateClient(sp.GetRequiredService<IOptions<KubernetesOptions>>().Value));
            services.AddSingleton<ITenantSites, KubernetesTenantSites>();
            return services;
        }

        if (!backend.Equals("Docker", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown 'Sites:Backend' '{backend}' (Docker or Kubernetes).");

        services.AddOptions<DockerOptions>().Bind(configuration.GetSection(DockerOptions.SectionName));
        services.AddSingleton(_ =>
        {
            var endpoint = configuration[$"{DockerOptions.SectionName}:Endpoint"];
            var builder = new DockerClientBuilder();
            if (!string.IsNullOrWhiteSpace(endpoint)) builder = builder.WithEndpoint(new Uri(endpoint));
            return builder.Build();
        });
        services.AddSingleton<ITenantSites, DockerTenantSites>();

        return services;
    }
}
