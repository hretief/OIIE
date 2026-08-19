using Azure.Core;
using Azure.Storage.Blobs;
using Oiie.Ccom;
using Oiie.Isbm.Client;
using SimHost.Application.Bods;
using SimHost.Application.Cir;
using SimHost.Application.Classification;
using SimHost.Application.Identity;
using SimHost.Application.Inbox;
using SimHost.Application.Outbox;
using SimHost.Application.Participants;
using SimHost.Application.Scenarios;
using SimHost.Domain.Common;
using SimHost.Infrastructure.Blob;
using SimHost.Infrastructure.Isbm;
using SimHost.Infrastructure.Sql;
using SimHost.Personalities.Eng;
using SimHost.Personalities.Mms;
using SimHost.Personalities.Cms;
using SimHost.Personalities.RegLocation;

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
    /// Registers the whole sandbox engine: participants, infrastructure, the scenario
    /// vocabularies and every personality's services.
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

        // --- Infrastructure ----------------------------------------------------
        services.AddSingleton<IParticipantConnectionStringProvider,
            KeyVaultConnectionStringProvider>();
        services.AddSingleton<IParticipantDbContextFactory, ParticipantDbContextFactory>();
        services.AddSingleton<IParticipantSchemaInitializer, ParticipantSchemaInitializer>();

        // Scenario orchestration state, in the shared sandbox schema rather than any one
        // participant's.
        services.AddSingleton<ISandboxDbContextFactory, SandboxDbContextFactory>();
        services.AddSingleton<ISandboxSchemaInitializer, SandboxSchemaInitializer>();

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
            services.AddSingleton<IIsbmSessionStoreAccessor>(sp => sp.GetRequiredService<IsbmClientAccessor>());

            // Session management is a service, not a pump: SandboxResetService closes
            // sessions before dropping tables, and it runs in both hosts. Only the
            // hosted pumps that *drain* those sessions are host-exclusive.
            services.AddSingleton<IsbmSessionManager>();
            services.AddSingleton<InboxTelemetry>();
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
        services.AddSingleton<DispatcherControl>();

        // Where the admin API lives. Only meaningful for hosts that are not the API
        // themselves, which since the split means the operator UI.
        services.AddSingleton<SandboxApiEndpoint>();

        // Stands in for the tag identity service that CIR will eventually provide. Identity
        // is a FederationId, minted once by a master — the design tool or REG-LOCATION — and
        // carried unchanged for the entity's whole lifecycle. Codes are separate, optional
        // and plural. Swapping this registration for the real client is the only change the
        // participants should need.
        services.AddSingleton<ITagIdentityService, EmulatedTagIdentityService>();

        // Registered unconditionally: the inbox pump reads the current run from it, and a
        // null run id is the correct answer outside a scenario.
        services.AddSingleton<ScenarioRunContext>();

        // The action vocabulary. Each action wraps a service the admin endpoints already
        // call, so a scenario drives the participants the way an operator would.
        services.AddSingleton<IScenarioAction, CreateTagAction>();
        services.AddSingleton<IScenarioAction, RelateTagsAction>();
        services.AddSingleton<IScenarioAction, PublishRelationshipsAction>();
        services.AddSingleton<IScenarioAction, PromoteNamedVersionAction>();
        services.AddSingleton<IScenarioAction, ApproveStewardshipAction>();
        services.AddSingleton<IScenarioAction, RegisterEquipmentAction>();
        services.AddSingleton<IScenarioAction, RaiseWorkOrderAction>();
        services.AddSingleton<IScenarioAction, CompleteWorkOrderAction>();
        services.AddSingleton<IScenarioAction, SignOffWorkOrderAction>();
        services.AddSingleton<IScenarioAction, RegisterCirAction>();
        services.AddSingleton<IScenarioAction, ResolveIdentityAction>();
        services.AddSingleton<ScenarioActionRegistry>();

        // The assertion vocabulary (spec §11.2). Phase 1 covers what uc01 needs; the
        // remaining names in the table are added as the scenarios that use them arrive.
        services.AddSingleton<IScenarioAssertion, MessageReceivedAssertion>();
        services.AddSingleton<IScenarioAssertion, MessageNotReceivedAssertion>();
        services.AddSingleton<IScenarioAssertion, BodValidAssertion>();
        services.AddSingleton<IScenarioAssertion, StoreContainsAssertion>();
        services.AddSingleton<IScenarioAssertion, StoreNotContainsAssertion>();
        services.AddSingleton<IScenarioAssertion, CirEquivalentAssertion>();
        services.AddSingleton<IScenarioAssertion, CirRegisteredAssertion>();
        services.AddSingleton<IScenarioAssertion, IdentityResolvedAssertion>();
        services.AddSingleton<IScenarioAssertion, OutboxStateAssertion>();
        services.AddSingleton<IScenarioAssertion, PendingWorkAssertion>();
        services.AddSingleton<ScenarioAssertionRegistry>();

        services.AddSingleton<ScenarioLoader>();
        services.AddSingleton<ScenarioCatalog>();
        services.AddSingleton<ScenarioRunner>();

        // Read-side services for the run-detail UI. Singletons like the rest of this group:
        // both create their own short-lived DbContexts per call rather than holding one, so
        // there is no scoped state to respect.
        services.AddSingleton<IdentityLineageService>();
        services.AddSingleton<RunTimelineService>();
        services.AddSingleton<MessageTransformService>();
        services.AddSingleton<RunDataService>();
        services.AddSingleton<ParticipantStoreBrowser>();
        services.AddSingleton<SandboxResetService>();
        services.AddSingleton<ScenarioLauncher>();

        services.AddSingleton<CcomAttributeMapperFactory>();
        services.AddSingleton<CirTelemetry>();
        services.AddSingleton<CirClient>();
        services.AddSingleton<CirRegistrationService>();
        services.AddSingleton<CmsContextResolver>();
        services.AddSingleton<MmsContextResolver>();
        services.AddSingleton<MmsInventoryWriter>();
        services.AddSingleton<ClassFixtureLoader>();
        services.AddSingleton<ClassificationRefresher>();

        services.AddSingleton<IBodBuilder, SyncSegmentsBuilder>();
        services.AddSingleton<IBodBuilder, SyncSegmentConnectionsBuilder>();
        services.AddSingleton<EngService>();

        services.AddSingleton<IBodBuilder, RegLocationSegmentsBuilder>();
        services.AddSingleton<IBodBuilder, RegLocationConnectionsBuilder>();
        services.AddSingleton<IBodHandler, SyncSegmentsHandler>();
        services.AddSingleton<IBodHandler, SyncSegmentConnectionsHandler>();
        services.AddSingleton<RegLocationService>();

        services.AddSingleton<IBodHandler, MmsSegmentsHandler>();
        services.AddSingleton<IBodHandler, MmsSegmentConnectionsHandler>();

        // OIIE Scenario 11. MMS publishes asset install/removal events for the first time —
        // through phase 1 it only consumed — and CMS is the "O&M Systems" actor
        // that receives them.
        services.AddSingleton<IBodBuilder, MmsAssetSegmentEventsBuilder>();
        services.AddSingleton<MmsWorkOrderService>();
        services.AddSingleton<IBodHandler, CmsAssetSegmentEventsHandler>();

        // CMS receives the same approved segments MMS does, and turns each into an
        // asset placeholder rather than a functional location — a condition
        // monitoring system monitors assets, not design artefacts.
        services.AddSingleton<IBodHandler, CmsSegmentsHandler>();

        return services;
    }

    /// <summary>
    /// Adds the background pumps that move messages in and out over ISBM.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="AddSandboxCore"/> so a host can compose the engine
    /// without also becoming a message mover. Two processes both draining the same ISBM
    /// sessions would settle each other's messages and make delivery nondeterministic,
    /// so exactly one host — the API — runs these.
    /// </remarks>
    public static IServiceCollection AddSandboxMessagePumps(this IServiceCollection services)
    {
        // Reuse the registry AddSandboxCore already built rather than re-reading every
        // personality.yaml from disk. Must be called after AddSandboxCore.
        var registry = services
            .FirstOrDefault(d => d.ServiceType == typeof(ParticipantRegistry))?
            .ImplementationInstance as ParticipantRegistry
            ?? throw new InvalidOperationException(
                $"{nameof(AddSandboxMessagePumps)} must be called after {nameof(AddSandboxCore)}.");

        var isbmConfigured = SandboxCapabilities.IsIsbmConfigured(registry);

        if (isbmConfigured)
        {
            services.AddHostedService<InboxPump>();
            services.AddHostedService<OutboxDispatcher>();
        }

        return services;
    }

    private static IReadOnlyList<PersonalityConfig> LoadPersonalities(
        IConfiguration configuration, IHostEnvironment environment)
    {
        var personalitiesRoot = ResolveContentPath(
            configuration["Sandbox:PersonalitiesPath"], environment, "PersonalityPacks");

        return PersonalityLoader.LoadAll(personalitiesRoot);
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
