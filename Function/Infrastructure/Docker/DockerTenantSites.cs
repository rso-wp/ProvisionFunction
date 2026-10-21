using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Function.Features;
using Function.Infrastructure.MySql;
using Function.Infrastructure.Sites;
using Microsoft.Extensions.Options;

namespace Function.Infrastructure.Docker;

public class DockerOptions
{
    public const string SectionName = "Docker";

    /// <summary>Docker API endpoint. Empty = platform default (npipe on Windows, unix socket on Linux).</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Docker network the WordPress containers join; MySQL must be reachable on it.</summary>
    public string Network { get; set; } = "rso";

    public string WordPressImage { get; set; } = "wordpress:6-apache";

    /// <summary>Host address the site port is published on, and the host used in the returned URL.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";
    public string PublicHost { get; set; } = "localhost";
}

/// <summary>Sites as containers on a local docker (Sites:Backend = Docker, local development).</summary>
public class DockerTenantSites(DockerClient docker, IOptions<DockerOptions> options, ILogger<DockerTenantSites> logger)
    : ITenantSites
{
    private const string HttpPort = "80/tcp";

    public async Task<string> StartAsync(SiteKey site, string host, TenantDatabase database, CancellationToken ct)
    {
        var o = options.Value;
        var name = site.Container;

        await EnsureImageAsync(o.WordPressImage, ct);
        await RemoveContainerAsync(name, ct); // a redelivered command starts from scratch (the volume is kept)

        var created = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            Image = o.WordPressImage,
            Env =
            [
                $"WORDPRESS_DB_HOST={database.Host}",
                $"WORDPRESS_DB_NAME={database.Name}",
                $"WORDPRESS_DB_USER={database.User}",
                $"WORDPRESS_DB_PASSWORD={database.Password}",
                // The published port is chosen by docker, so derive the site URL from the request instead of storing it.
                $"WORDPRESS_CONFIG_EXTRA={WordPressConfig.Extra("'http://' . $_SERVER['HTTP_HOST']", database.UseTls)}"
            ],
            Labels = new Dictionary<string, string>
            {
                ["rso.tenant"] = site.Tenant, ["rso.site"] = site.Name, ["rso.provision-id"] = site.ProvisionId.ToString()
            },
            ExposedPorts = new Dictionary<string, EmptyStruct> { [HttpPort] = default },
            // WordPress needs a few seconds after the container starts (it copies its files on first boot). The site is
            // only reported as provisioned once it answers HTTP.
            Healthcheck = new HealthcheckConfig
            {
                Test = ["CMD", "php", "-r", "exit(@file_get_contents('http://localhost/wp-login.php') === false ? 1 : 0);"],
                Interval = TimeSpan.FromSeconds(2),
                Timeout = TimeSpan.FromSeconds(5),
                Retries = 60
            },
            HostConfig = new HostConfig
            {
                NetworkMode = o.Network,
                // Empty HostPort: docker picks a free port.
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [HttpPort] = [new PortBinding { HostIP = o.BindAddress, HostPort = "" }]
                },
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                Mounts = [new Mount { Type = "volume", Source = site.Volume, Target = "/var/www/html" }]
            }
        }, ct);

        await docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        await WaitUntilHealthyAsync(created.ID, name, ct);

        var inspected = await docker.Containers.InspectContainerAsync(created.ID, ct);
        IList<PortBinding>? bindings = null;
        inspected.NetworkSettings?.Ports?.TryGetValue(HttpPort, out bindings);
        var hostPort = bindings?.FirstOrDefault()?.HostPort
            ?? throw new InvalidOperationException($"Container {name} has no published port for {HttpPort}.");
        var url = $"http://{o.PublicHost}:{hostPort}";

        logger.LogInformation("Container {Container} running at {Url}", name, url);
        return url;
    }

    private async Task WaitUntilHealthyAsync(string containerId, string name, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (true)
        {
            var state = (await docker.Containers.InspectContainerAsync(containerId, ct)).State;
            var health = state?.Health?.Status;
            if (health == "healthy") return;
            if (health == "unhealthy" || state?.Running == false || DateTime.UtcNow > deadline)
                throw new InvalidOperationException($"Container {name} did not become healthy (status: {health ?? state?.Status}).");

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    public async Task RemoveAsync(SiteKey site, CancellationToken ct)
    {
        await RemoveContainerAsync(site.Container, ct);

        var volume = site.Volume;
        try
        {
            await docker.Volumes.RemoveAsync(volume, force: true, ct);
            logger.LogInformation("Volume {Volume} removed", volume);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private async Task RemoveContainerAsync(string name, CancellationToken ct)
    {
        try
        {
            await docker.Containers.RemoveContainerAsync(name, new ContainerRemoveParameters { Force = true }, ct);
            logger.LogInformation("Container {Container} removed", name);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private async Task EnsureImageAsync(string image, CancellationToken ct)
    {
        try
        {
            await docker.Images.InspectImageAsync(image, ct);
            return;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }

        logger.LogInformation("Pulling {Image}", image);
        var separator = image.LastIndexOf(':');
        await docker.Images.CreateImageAsync(
            new ImagesCreateParameters
            {
                FromImage = separator > 0 ? image[..separator] : image,
                Tag = separator > 0 ? image[(separator + 1)..] : "latest"
            },
            null,
            new Progress<JSONMessage>(),
            ct);
    }
}
