using Function.Features.Deprovision;
using Function.Features.Provision;
using Function.Infrastructure;
using Function.Infrastructure.Nats;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNats(builder.Configuration);
builder.Services.AddTenantDatabases(builder.Configuration);
builder.Services.AddTenantSites(builder.Configuration);
builder.Services.AddSingleton<ProvisionTenantSiteHandler>();
builder.Services.AddSingleton<DeprovisionTenantSiteHandler>();
builder.Services.AddHostedService<CommandConsumer>();

await builder.Build().RunAsync();
