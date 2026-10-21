using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Azure.Identity;
using Function.Features;
using Function.Infrastructure.MySql;
using Function.Infrastructure.Sites;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace Function.Infrastructure.Kubernetes;

public class KubernetesOptions
{
    public const string SectionName = "Kubernetes";

    /// <summary>
    /// API server URL (AKS: https://...azmk8s.io:443); the function then signs in with its managed identity. Empty: use the
    /// kubeconfig (<c>KUBECONFIG</c> or ~/.kube/config), e.g. a local kind / Docker Desktop cluster.
    /// </summary>
    public string ApiServer { get; set; } = "";

    /// <summary>Base64 PEM of the cluster CA, with <see cref="ApiServer"/> (terraform: kube_config.cluster_ca_certificate).</summary>
    public string CaCertificate { get; set; } = "";

    public string Namespace { get; set; } = "tenants";
    public string IngressClass { get; set; } = "traefik";

    /// <summary>StorageClass of the site volumes; empty = the cluster default. AKS: managed-csi (Azure Disk).</summary>
    public string StorageClass { get; set; } = "";

    public string StorageSize { get; set; } = "2Gi";
    public string WordPressImage { get; set; } = "wordpress:6-apache";

    /// <summary>Scheme of the returned site URL (the ingress serves plain HTTP).</summary>
    public string UrlScheme { get; set; } = "http";

    /// <summary>How long a new site may take to become ready (the first one also pulls the image).</summary>
    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public static IKubernetes CreateClient(KubernetesOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.ApiServer))
            return new k8s.Kubernetes(KubernetesClientConfiguration.BuildDefaultConfig());

        var ca = new X509Certificate2Collection();
        ca.ImportFromPem(Encoding.UTF8.GetString(Convert.FromBase64String(o.CaCertificate)));
        return new k8s.Kubernetes(new KubernetesClientConfiguration
        {
            Host = o.ApiServer,
            SslCaCerts = ca,
            TokenProvider = new AksTokenProvider(new DefaultAzureCredential())
        });
    }
}

/// <summary>
/// Sites in Kubernetes (Sites:Backend = Kubernetes): per site a Secret (DB password), a PersistentVolumeClaim
/// (/var/www/html), a Deployment (1 replica of the official WordPress image), a Service and an Ingress for its host, all
/// named <see cref="SiteKey.ResourceName"/> in <see cref="KubernetesOptions.Namespace"/>. Server-side apply, so a retried
/// command updates what is already there.
/// </summary>
public class KubernetesTenantSites(IKubernetes k8s, IOptions<KubernetesOptions> options, ILogger<KubernetesTenantSites> logger)
    : ITenantSites
{
    private const string FieldManager = "provision-function";
    private const string HttpPort = "http";

    public async Task<string> StartAsync(SiteKey site, string host, TenantDatabase database, CancellationToken ct)
    {
        var o = options.Value;
        var name = site.ResourceName;
        var ns = o.Namespace;
        var labels = Labels(site);
        var url = $"{o.UrlScheme}://{host}";

        await k8s.CoreV1.PatchNamespacedSecretAsync(Apply(new V1Secret
        {
            ApiVersion = "v1",
            Kind = "Secret",
            Metadata = Meta(name, ns, labels),
            StringData = new Dictionary<string, string> { ["password"] = database.Password }
        }), name, ns, fieldManager: FieldManager, force: true, cancellationToken: ct);

        await k8s.CoreV1.PatchNamespacedPersistentVolumeClaimAsync(Apply(new V1PersistentVolumeClaim
        {
            ApiVersion = "v1",
            Kind = "PersistentVolumeClaim",
            Metadata = Meta(name, ns, labels),
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = ["ReadWriteOnce"],
                StorageClassName = string.IsNullOrWhiteSpace(o.StorageClass) ? null : o.StorageClass,
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(o.StorageSize) }
                }
            }
        }), name, ns, fieldManager: FieldManager, force: true, cancellationToken: ct);

        await k8s.AppsV1.PatchNamespacedDeploymentAsync(
            Apply(Deployment(site, name, ns, labels, url, database)), name, ns, fieldManager: FieldManager, force: true, cancellationToken: ct);

        await k8s.CoreV1.PatchNamespacedServiceAsync(Apply(new V1Service
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = Meta(name, ns, labels),
            Spec = new V1ServiceSpec
            {
                Selector = labels,
                Ports = [new V1ServicePort { Name = HttpPort, Port = 80, TargetPort = HttpPort }]
            }
        }), name, ns, fieldManager: FieldManager, force: true, cancellationToken: ct);

        await k8s.NetworkingV1.PatchNamespacedIngressAsync(Apply(new V1Ingress
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "Ingress",
            Metadata = Meta(name, ns, labels),
            Spec = new V1IngressSpec
            {
                IngressClassName = o.IngressClass,
                Rules =
                [
                    new V1IngressRule
                    {
                        Host = host,
                        Http = new V1HTTPIngressRuleValue
                        {
                            Paths =
                            [
                                new V1HTTPIngressPath
                                {
                                    Path = "/",
                                    PathType = "Prefix",
                                    Backend = new V1IngressBackend
                                    {
                                        Service = new V1IngressServiceBackend
                                        {
                                            Name = name,
                                            Port = new V1ServiceBackendPort { Name = HttpPort }
                                        }
                                    }
                                }
                            ]
                        }
                    }
                ]
            }
        }), name, ns, fieldManager: FieldManager, force: true, cancellationToken: ct);

        await WaitUntilReadyAsync(name, ns, ct);
        logger.LogInformation("Site {Site} ready at {Url} ({Name} in {Namespace})", site, url, name, ns);
        return url;
    }

    private V1Deployment Deployment(SiteKey site, string name, string ns, IDictionary<string, string> labels, string url, TenantDatabase database)
    {
        var o = options.Value;
        // Every provision resets the DB password (MySqlTenantDatabases); the hash makes the pods restart with the new one.
        var passwordHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(database.Password)))[..16];

        return new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = Meta(name, ns, labels),
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
                Strategy = new V1DeploymentStrategy { Type = "Recreate" }, // the volume is ReadWriteOnce
                Selector = new V1LabelSelector { MatchLabels = labels },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = labels,
                        Annotations = new Dictionary<string, string> { ["rso/db-password"] = passwordHash }
                    },
                    Spec = new V1PodSpec
                    {
                        Containers =
                        [
                            new V1Container
                            {
                                Name = "wordpress",
                                Image = o.WordPressImage,
                                Ports = [new V1ContainerPort { Name = HttpPort, ContainerPort = 80 }],
                                Env =
                                [
                                    new V1EnvVar { Name = "WORDPRESS_DB_HOST", Value = database.Host },
                                    new V1EnvVar { Name = "WORDPRESS_DB_NAME", Value = database.Name },
                                    new V1EnvVar { Name = "WORDPRESS_DB_USER", Value = database.User },
                                    new V1EnvVar
                                    {
                                        Name = "WORDPRESS_DB_PASSWORD",
                                        ValueFrom = new V1EnvVarSource
                                        {
                                            SecretKeyRef = new V1SecretKeySelector { Name = name, Key = "password" }
                                        }
                                    },
                                    new V1EnvVar
                                    {
                                        Name = "WORDPRESS_CONFIG_EXTRA",
                                        Value = WordPressConfig.Extra($"'{url}'", database.UseTls)
                                    }
                                ],
                                VolumeMounts = [new V1VolumeMount { Name = "html", MountPath = "/var/www/html" }],
                                // WordPress copies its files on first boot, so "running" is not "answering". A redirect to
                                // the installer counts as ready.
                                ReadinessProbe = new V1Probe
                                {
                                    HttpGet = new V1HTTPGetAction { Path = "/wp-login.php", Port = HttpPort },
                                    PeriodSeconds = 5,
                                    TimeoutSeconds = 5
                                },
                                Resources = new V1ResourceRequirements
                                {
                                    Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("50m"), ["memory"] = new("128Mi") },
                                    Limits = new Dictionary<string, ResourceQuantity> { ["memory"] = new("512Mi") }
                                }
                            }
                        ],
                        Volumes =
                        [
                            new V1Volume
                            {
                                Name = "html",
                                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = name }
                            }
                        ]
                    }
                }
            }
        };
    }

    /// <summary>Waits until the Deployment's current revision has an available pod.</summary>
    private async Task WaitUntilReadyAsync(string name, string ns, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + options.Value.ReadyTimeout;
        while (true)
        {
            var d = await k8s.AppsV1.ReadNamespacedDeploymentStatusAsync(name, ns, cancellationToken: ct);
            var s = d.Status;
            if (s is not null && s.ObservedGeneration >= d.Metadata.Generation
                && s.UpdatedReplicas == 1 && s.AvailableReplicas == 1 && (s.Replicas ?? 0) == 1)
                return;

            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException($"Site {name} did not become ready: {await DescribePodsAsync(name, ns, ct)}");

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    /// <summary>Why the pods are not ready (e.g. ImagePullBackOff, CrashLoopBackOff, an unbound volume), for the error.</summary>
    private async Task<string> DescribePodsAsync(string name, string ns, CancellationToken ct)
    {
        var pods = await k8s.CoreV1.ListNamespacedPodAsync(ns, labelSelector: $"app.kubernetes.io/instance={name}", cancellationToken: ct);
        if (pods.Items.Count == 0) return "no pod was created";

        return string.Join("; ", pods.Items.Select(p =>
        {
            var waiting = p.Status?.ContainerStatuses?.FirstOrDefault()?.State?.Waiting;
            var unscheduled = p.Status?.Conditions?.FirstOrDefault(c => c.Type == "PodScheduled" && c.Status == "False");
            return $"{p.Metadata.Name}: {p.Status?.Phase}"
                + (waiting is null ? "" : $", {waiting.Reason}: {waiting.Message}")
                + (unscheduled is null ? "" : $", not scheduled: {unscheduled.Message}");
        }));
    }

    public async Task RemoveAsync(SiteKey site, CancellationToken ct)
    {
        var name = site.ResourceName;
        var ns = options.Value.Namespace;

        await IgnoreMissing(() => k8s.NetworkingV1.DeleteNamespacedIngressAsync(name, ns, cancellationToken: ct));
        await IgnoreMissing(() => k8s.CoreV1.DeleteNamespacedServiceAsync(name, ns, cancellationToken: ct));
        await IgnoreMissing(() => k8s.AppsV1.DeleteNamespacedDeploymentAsync(name, ns, cancellationToken: ct));
        await IgnoreMissing(() => k8s.CoreV1.DeleteNamespacedSecretAsync(name, ns, cancellationToken: ct));
        // Deletes the disk too (reclaim policy Delete), once the pod is gone.
        await IgnoreMissing(() => k8s.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(name, ns, cancellationToken: ct));

        logger.LogInformation("Site {Site} removed ({Name} in {Namespace})", site, name, ns);
    }

    private static async Task IgnoreMissing(Func<Task> delete)
    {
        try
        {
            await delete();
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static Dictionary<string, string> Labels(SiteKey site) => new()
    {
        ["app.kubernetes.io/name"] = "wordpress",
        ["app.kubernetes.io/instance"] = site.ResourceName,
        ["app.kubernetes.io/managed-by"] = FieldManager,
        ["rso.tenant"] = site.Tenant,
        ["rso.site"] = site.Name,
        ["rso.provision-id"] = site.ProvisionId.ToString()
    };

    private static V1ObjectMeta Meta(string name, string ns, IDictionary<string, string> labels) =>
        new() { Name = name, NamespaceProperty = ns, Labels = labels };

    private static V1Patch Apply(object body) => new(body, V1Patch.PatchType.ApplyPatch);
}
