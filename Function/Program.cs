using Function.Features.Deprovision;
using Function.Features.Provision;
using Function.Infrastructure;
using Function.Infrastructure.Queues;
using Microsoft.Azure.Functions.Worker.Builder;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddProvisioningQueues(builder.Configuration);
builder.Services.AddTenantDatabases(builder.Configuration);
builder.Services.AddTenantSites(builder.Configuration);
builder.Services.AddSingleton<ProvisionTenantSiteHandler>();
builder.Services.AddSingleton<DeprovisionTenantSiteHandler>();

await builder.Build().RunAsync();
