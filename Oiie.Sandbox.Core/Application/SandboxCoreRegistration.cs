using Azure.Core;
using Azure.Storage.Blobs;
using Oiie.Ccom;
using Oiie.Isbm.Client;
using SimHost.Application.Identity;
using SimHost.Application.Participants;
using SimHost.Application.Topology;
using SimHost.Domain.Common;
using SimHost.Infrastructure.Blob;
using SimHost.Infrastructure.Isbm;

namespace SimHost.Application;

/// <summary>
/// The engine's composition root.
///
/// Two hosts now run this engine: the Sandbox API, which exposes it over HTTP, and
/// the Blazor UI, which renders it. Both need the identical object graph — the same
/// personalities, the same action and assertion vocabularies, the same outbox and
/// inbox pumps. Registering that graph in one place is what keeps the two hosts from
/// drifting into subtly different sandboxes, which would make a defect reproducible
/// in one and not the other.
/// </summary>
public static class SandboxCoreRegistration
{
    /// <summary>
    /// Adds Key Vault as a configuration source when one is configured.
    ///
    /// Separate from <see cref="AddSandboxCore"/> because it acts on the configuration
    /// builder rather than the service collection, and has to run before anything
    /// reads a connection string.
    /// </summary>
    public static IConfigurationBuilder AddSandboxKeyVault(
        this IConfigurationBuilder configuration, TokenCredential credential)
    {
        var keyVaultUri = configuration.Build()["KeyVault:Uri"];

        if (!string.IsNullOrWhiteSpace(keyVaultUri))
        {
            configuration.AddAzureKeyVault(new Uri(keyVaultUri), credential);
        }

        return configuration;
    }

    /// <summary>
    /// Registers the whole sandbox engine: participants, infrastructure and every
    /// personality's services.
    /// </summary>
    /// <remarks>
    /// Deliberately excluded, because they are host decisions rather than engine ones:
    /// Application Insights, Razor components, and the HTTP endpoint surface.
    /// </remarks>
    public static IServiceCollection AddSandboxCore(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        TokenCredential credential)
    {
        // --- Participants ------------------------------------------------------
        var personalities = LoadPersonalities(configuration, environment);
        services.AddSingleton(new ParticipantRegistry(personalities));

        // --- Topology ----------------------------------------------------------
        // Which participant publishes to which channel, per scenario. Separate
        // from the personality packs because it is a statement about the system
        // rather than about any one participant: a channel has two ends, and
        // declaring each end in its own file is what let them drift apart.
        //
        // Loaded from content like everything else here, so a new scenario or
        // participant is a file drop and a provisioning run. Nothing in this
        // graph names a scenario, which is what keeps that true.
        services.AddSingleton(new TopologyRegistry(LoadTopology(configuration, environment)));

        // --- Infrastructure ----------------------------------------------------

        // Storage and ISBM are optional at startup so the database work can proceed before
        // either is wired. Each is registered only when configured, and the outbox — which
        // needs both — stays dormant otherwise. Failing startup instead would block schema
        // initialisation on dependencies it does not use.
        var storageConfigured = SandboxCapabilities.IsStorageConfigured(configuration);

        if (storageConfigured)
        {
            services.AddSingleton(_ => new BlobServiceClient(
                new Uri(configuration["Storage:BlobServiceUri"]!), credential));
            services.AddSingleton<IPayloadStore, BlobPayloadStore>();
        }
        else
        {
            // Registered so messaging still runs without a storage account. The archive
            // row is worth more than the payload body.
            services.AddSingleton<IPayloadStore, NullPayloadStore>();
        }

        // The client is now the real one extracted from the ws-CIR provider, so ISBM is
        // wired whenever a participant declares a base URL.
        var isbmConfigured = SandboxCapabilities.IsIsbmConfigured(personalities);

        // Registered unconditionally: the reset service calls the admin endpoints over
        // HTTP, so a factory has to exist even when no participant declares an ISBM base URL.
        services.AddHttpClient();

        if (isbmConfigured)
        {
            services.AddSingleton<IsbmClientAccessor>();
            services.AddSingleton<IIsbmClientAccessor>(sp => sp.GetRequiredService<IsbmClientAccessor>());
        }

        // --- BOD ---------------------------------------------------------------
        services.AddSingleton(_ =>
        {
            var validator = new BodValidator();
            var schemaRoot = ResolveContentPath(
                configuration["Sandbox:SchemasPath"], environment, Path.Combine("..", "schemas"));
            validator.LoadDirectory(schemaRoot);
            return validator;
        });

        // --- Application services ----------------------------------------------

        // Where the admin API lives. Only meaningful for hosts that are not the API
        // themselves, which since the split means the operator UI.
        services.AddSingleton<SandboxApiEndpoint>();

        // Stands in for the tag identity service that CIR will eventually provide. Identity
        // is a FederationId, minted once by a master — the design tool or REG-LOCATION — and
        // carried unchanged for the entity's whole lifecycle. Codes are separate, optional
        // and plural. Swapping this registration for the real client is the only change the
        // participants should need.
        services.AddSingleton<ITagIdentityService, EmulatedTagIdentityService>();

        services.AddSingleton<SandboxResetService>();

        // No participant personalities and no BOD builders or handlers are
        // registered here. Every participant is a separate deployable -- an
        // engine paired with a provider -- holding its own store and doing its
        // own ISBM and CIR work. Registering a builder per participant in this
        // container is what made adding one a recompile, which is the whole
        // thing the golden rule forbids.

        return services;
    }

    private static IReadOnlyList<PersonalityConfig> LoadPersonalities(
        IConfiguration configuration, IHostEnvironment environment)
    {
        var personalitiesRoot = ResolveContentPath(
            configuration["Sandbox:PersonalitiesPath"], environment, "PersonalityPacks");

        return PersonalityLoader.LoadAll(personalitiesRoot);
    }

    private static IReadOnlyList<TopologyConfig> LoadTopology(
        IConfiguration configuration, IHostEnvironment environment)
    {
        var topologyRoot = ResolveContentPath(
            configuration["Sandbox:TopologyPath"], environment, "Topology");

        return TopologyLoader.LoadAll(topologyRoot);
    }

    /// <summary>
    /// Resolves a configured content path.
    ///
    /// Packs and scenarios live in Oiie.Sandbox.Core and are linked into each host's
    /// build output, so they sit beside the assembly rather than in the project
    /// directory. ContentRootPath is the project directory under `dotnet run`, so
    /// resolving there alone finds nothing -- which surfaced as "Personalities
    /// directory not found" naming a path nobody had configured.
    ///
    /// Content root is still tried first, because a deployed app has its content
    /// there and an operator overriding this setting means the content root. The
    /// output directory is the fallback, which is what makes `dotnet run` work.
    ///
    /// Public because the endpoints resolve the same setting to reload fixtures,
    /// and three copies of `Path.GetFullPath(configuration[...])` there had the
    /// original defect: schema/seed reported "0 class(es) loaded" while the
    /// registry held four, because the two resolved the same setting differently.
    /// </summary>
    public static string ResolveContentPath(
        string? configured, IHostEnvironment environment, string fallback)
    {
        var path = string.IsNullOrWhiteSpace(configured) ? fallback : configured;

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var fromContentRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));

        if (Directory.Exists(fromContentRoot))
        {
            return fromContentRoot;
        }

        var fromOutput = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

        // Content root is returned when neither exists, so the error names the
        // location that was actually configured rather than an internal fallback.
        return Directory.Exists(fromOutput) ? fromOutput : fromContentRoot;
    }
}
