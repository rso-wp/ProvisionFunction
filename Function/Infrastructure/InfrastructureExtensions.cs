using Docker.DotNet;
using Function.Infrastructure.Docker;
using Function.Infrastructure.MySql;

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

    public static IServiceCollection AddTenantSites(this IServiceCollection services, IConfiguration configuration)
    {
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
