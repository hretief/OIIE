using Microsoft.Azure.Functions.Worker;   // ConfigureFunctionsApplicationInsights(IServiceCollection)
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using IsbmProvider.Abstractions;
using IsbmProvider.Infrastructure;

// Classic isolated-worker host, paired with the HttpRequestData programming model.
// APIM / Front Door terminate TLS and validate OAuth2 (Entra) + client certs (mTLS at Level 3)
// before requests reach here; a worker middleware can map the principal to ISBM RBAC roles.
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(worker =>
    {
        // Global handler: converts IsbmFaultException → structured JSON fault response.
        worker.UseMiddleware<IsbmProvider.Http.FaultMiddleware>();
    })
    .ConfigureServices((ctx, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Shared credential for managed identity (production). Not used when connection strings are provided.
        var credential = new DefaultAzureCredential();

        // --- Azure Service Bus clients ---
        // Managed identity is the intended path everywhere, including locally, where
        // DefaultAzureCredential picks up the signed-in az/VS account. A full
        // connection string is still honoured if one is supplied, but nothing in the
        // repository provides one and the infrastructure no longer creates a SAS rule.
        var sbNamespace = ctx.Configuration["ServiceBusConnection:fullyQualifiedNamespace"]
                          ?? ctx.Configuration["ServiceBusConnection"]
                          ?? "placeholder.servicebus.windows.net";

        if (sbNamespace.Contains("Endpoint=", StringComparison.OrdinalIgnoreCase))
        {
            // Explicit connection string. Kept as an escape hatch only.
            services.AddSingleton(_ => new ServiceBusClient(sbNamespace));
            services.AddSingleton(_ => new ServiceBusAdministrationClient(sbNamespace));
        }
        else
        {
            services.AddSingleton(_ => new ServiceBusClient(sbNamespace, credential));
            services.AddSingleton(_ => new ServiceBusAdministrationClient(sbNamespace, credential));
        }

        // --- Azure Blob Storage client (claim-check for large CCOM BODs) ---
        var blobUri = ctx.Configuration["BlobPayloadStore:serviceUri"]
                      ?? "https://placeholder.blob.core.windows.net";
        services.AddSingleton(_ => new Azure.Storage.Blobs.BlobServiceClient(new Uri(blobUri), credential));

        // --- Azure Key Vault client (encrypted token storage, Level 2+) ---
        var kvUri = ctx.Configuration["KeyVault:uri"];
        if (!string.IsNullOrEmpty(kvUri) && !kvUri.Contains("REPLACE"))
            services.AddSingleton(_ => new Azure.Security.KeyVault.Secrets.SecretClient(new Uri(kvUri), credential));
        else
            services.AddSingleton(_ => (Azure.Security.KeyVault.Secrets.SecretClient)null!);

        // --- Azure Table Storage client (channel store — reuses the same storage account) ---
        var tableConnStr = ctx.Configuration["AzureWebJobsStorage"] ?? "UseDevelopmentStorage=true";
        services.AddSingleton(_ => new Azure.Data.Tables.TableServiceClient(tableConnStr));

        // --- Token validation (enforces presented tokens on secured channels) ---
        services.AddSingleton<IsbmProvider.Http.TokenValidator>();

        // --- Domain ports -> adapters ---
        services.AddSingleton<ICorrelationStore, TableCorrelationStore>();   // Table Storage (scale-out safe)
        services.AddSingleton<IMessageBroker, ServiceBusMessageBroker>();       // REAL adapter (this step)

        // Session registry (notification dispatch needs to look up sessions by channel)
        services.AddSingleton<ISessionRegistry, TableSessionRegistry>();   // Table Storage (survives restarts)
        services.AddSingleton<IChannelStore, TableChannelStore>();   // Azure Table Storage (zero extra cost)
        services.AddSingleton<IPayloadStore, BlobPayloadStore>();   // REAL adapter
        // Key Vault token vault when configured; stub for local dev without KV.
        if (!string.IsNullOrEmpty(ctx.Configuration["KeyVault:uri"]) && !ctx.Configuration["KeyVault:uri"]!.Contains("REPLACE"))
            services.AddSingleton<ITokenVault, KeyVaultTokenVault>();
        else
            services.AddSingleton<ITokenVault, StubTokenVault>();
        services.AddSingleton<IFilterEngine, ContentFilterEngine>();   // REAL adapter (this step)
        // Notification dispatcher (HTTP PUT to subscriber ListenerURLs per spec §5.3/§5.4)
        services.AddHttpClient("IsbmNotifications");
        services.AddSingleton<INotificationDispatcher, HttpNotificationDispatcher>();

        // To fall back to the in-memory broker for offline testing, comment the ServiceBus
        // registration above and uncomment:
        // services.AddSingleton<IMessageBroker, StubMessageBroker>();
    })
    .Build();

host.Run();
