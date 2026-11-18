# ProvisionFunction

The worker that **builds and removes the WordPress sites** of RSO (a WordPress-as-a-service platform). It is an Azure Function
(.NET 10, isolated worker) triggered by storage queues. ProvisioningService sends the commands; this reports back.

```
ProvisioningService --queue provision-site--------> ProvisionSite     1. creates the site's MySQL database and user
                    --queue deprovision-site------> DeprovisionSite   2. starts WordPress (Kubernetes or Docker)
                    <--queue provisioning-events--- provisioned / provisioning-failed / destroyed
```

How it fits with the other services, and how to run all of it on your machine or on Azure: the **Deployment** repository.

## What it does

- **`ProvisionSite`** (queue `provision-site`): creates the database and user for the site on the MySQL server (one database and
  one user per site, the password is random), then starts the WordPress site and waits until it answers. On success it sends
  `provisioned` with the site's URL.
- **`DeprovisionSite`** (queue `deprovision-site`): removes the site and its storage first, then drops its database and user.
  Safe to repeat. Then it sends `destroyed`.
- **Failures** are retried by queueing the command again with a growing delay (30 seconds, doubling up to 15 minutes, 8 attempts,
  about 45 minutes). After the last attempt a provision reports `provisioning-failed`; ProvisioningService sends the command again
  later until the site reaches its end state.
- **Everything is named after the provision id** (a hash of it), not the site name, so a site deleted and created again under the
  same name never touches the old one's objects.
- **Where the sites run:** `Sites:Backend` is `Kubernetes` (AKS: Secret, volume, Deployment, Service and Ingress in the `tenants`
  namespace) or `Docker` (default, local development: containers on your Docker).
- **`GET /api/health`**: the worker is up.

The queue messages are a contract with ProvisioningService, which keeps its own copy of the types (`Contracts/`); change both
together.

## Run it locally

You need the .NET 10 SDK, [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
(`npm i -g azure-functions-core-tools@4`) and Docker. The infrastructure (MySQL, Azurite for the queues) comes from the compose
file in the **Deployment** repository, which must sit next to this one:

```powershell
docker compose -f ..\Deployment\local\docker-compose.yml up -d     # MySQL :3306, Azurite
cd Function
func start
```

Create `Function/local.settings.json` (it is not in git) with the local values; they point at Azurite and the compose MySQL:

```json
{
  "IsEncrypted": false,
  "Values": {
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "ProvisioningQueues": "UseDevelopmentStorage=true",
    "MySql__AdminConnectionString": "Server=localhost;Port=3306;User ID=root;Password=rso",
    "MySql__HostForSites": "rso-mysql:3306"
  }
}
```

Sites are then started as Docker containers on your machine. Or run the function inside compose instead:
`docker compose -f ..\Deployment\local\docker-compose.yml --profile apps up -d --build`.

## Configuration

Settings come from `local.settings.json` locally and from the Function App's application settings in Azure (`__` for nesting).

| Setting | Meaning |
|---|---|
| `ProvisioningQueues` | the storage account of the queues (connection string), or `ProvisioningQueues__queueServiceUri` for a managed identity |
| `AzureWebJobsStorage` | the Functions host's own storage |
| `MySql__AdminConnectionString` | an account that may create databases and users on the MySQL server |
| `MySql__HostForSites` | MySQL address as the WordPress sites see it, `host:port` |
| `MySql__SitesUseTls` | `true` for Azure Database for MySQL |
| `Sites__Backend` | `Docker` (default) or `Kubernetes` |
| `Kubernetes__ApiServer`, `Kubernetes__CaCertificate` | the AKS API server; the function signs in with its managed identity |
| `Kubernetes__Namespace`, `Kubernetes__IngressClass`, `Kubernetes__StorageClass`, `Kubernetes__StorageSize` | where and how the sites are created (defaults `tenants`, `traefik`, cluster default, `2Gi`) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | optional: logs go to Application Insights (the Functions host sends them) |

## Tests

```powershell
dotnet test Tests/Tests.csproj
```

They cover the object naming (`SiteKey`), the retry delays, the queue message contract, and the order of the steps in the two
handlers, with mocked database and site services. The Kubernetes, Docker and MySQL code is not covered (it needs those systems).

## Deploying

On Azure, Terraform (in the **Deployment** repository) creates the Function App **without its code**. Publish the code with its
`nonLocal\publish-function.ps1`, or set `publish_function = true` in Terraform so that `terraform apply` publishes it. Until the
code is published, new sites stay `Queued`.

The `Dockerfile` here is only for the local compose setup. **Push a git tag like `v1.2.3`** to publish
`armodilo/provisionfunction:v1.2.3` (secrets `DOCKERHUB_USERNAME`, `DOCKERHUB_TOKEN`). Pushes and pull requests run the tests
(`.github/workflows/ci.yml`).
