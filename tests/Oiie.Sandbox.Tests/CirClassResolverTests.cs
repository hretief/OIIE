using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using EngEngine.Application;
using Oiie.Ccom.Extensions;
using Oiie.Cir.Client;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// Class identity resolved through CIR rather than derived.
///
/// The identity in SegmentType used to be a hash of the RDL key. That value is
/// well-formed and stable and matches the governed library only by coincidence,
/// so a receiver binding on it records an identity ENG invented. Reference data
/// is minted by RDL and cross-referenced in CIR, so the registry is the only
/// place an answer somebody actually agreed to can come from.
///
/// The failure these tests guard against is not a wrong CIRID -- it is the
/// resolver being too eager. Turning it on against a CIR that is cold or
/// unreachable must leave publication exactly as it was, because a drain has
/// markers to publish and an unresolvable class is not a reason to stop.
/// </summary>
public class CirClassResolverTests
{
    private const string EcClass = "ENG.Streetlight";

    // ENG's internal identifier for that class, and what the entry is keyed on.
    // The name is carried as a label only.
    private const long EcClassId = 24;

    private const string RdlKey = "rdl:Streetlight";

    private static readonly Guid Governed =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static CirClassResolver Resolver(
        ICirClient cir,
        bool enabled = true,
        bool register = false,
        RdlTaxonomyValidator? rdl = null) =>
        new(cir,
            // Disabled by default: most of these tests are about the lookup
            // half, and a live validator would post a GetTaxonomySet with no
            // broker behind it.
            rdl ?? new RdlTaxonomyValidator(
                null!,
                Options.Create(new EngRdlOptions { Enabled = false }),
                Options.Create(new EngEngineOptions()),
                NullLogger<RdlTaxonomyValidator>.Instance),
            Options.Create(new EngEngineOptions
            {
                SourceId = "ENG",
                LogicalId = "ENG",
                ResolveClassIdentityFromCir = enabled,
                RegisterClassIdentityInCir = register,
                ClassRegistryId = "OIIE",
                ClassCategoryId = "RDL-CLASS"
            }),
            NullLogger<CirClassResolver>.Instance);

    private static CirRegistry RegistryHolding(string idInSource, Guid cirid) =>
        new("OIIE", [],
        [
            new CirCategory("RDL-CLASS", "ENG", [],
            [
                new CirEntry(idInSource, "ENG", cirid, null, null, null, [])
            ])
        ]);

    [Fact]
    public async Task Resolves_the_cirid_cir_holds_for_the_class()
    {
        var resolver = Resolver(new StubCir([RegistryHolding(EcClassId.ToString(), Governed)]));

        Assert.Equal(Governed, await resolver.ResolveAsync(EcClass, EcClassId));
    }

    [Fact]
    public async Task Returns_null_when_cir_holds_no_cross_reference()
    {
        // The normal state before CIR is seeded, not a fault. Null sends the
        // caller to the configured map rather than to a fabricated identity.
        var resolver = Resolver(new StubCir([]));

        Assert.Null(await resolver.ResolveAsync(EcClass, EcClassId));
    }

    [Fact]
    public async Task Returns_null_rather_than_throwing_when_cir_is_unreachable()
    {
        // CIR being down says nothing about whether the mapping is correct, so
        // it must not take the drain down with it.
        var resolver = Resolver(new UnreachableCir());

        Assert.Null(await resolver.ResolveAsync(EcClass, EcClassId));
    }

    [Fact]
    public async Task Does_not_call_cir_when_resolution_is_disabled()
    {
        // Off means off: a disabled resolver that still made the round trip
        // would put a network hop on the publish path for a value it discards.
        var cir = new StubCir([RegistryHolding(EcClassId.ToString(), Governed)]);
        var resolver = Resolver(cir, enabled: false);

        Assert.Null(await resolver.ResolveAsync(EcClass, EcClassId));
        Assert.Equal(0, cir.Calls);
    }

    [Fact]
    public async Task Asks_cir_once_per_class_including_for_misses()
    {
        // A marker is typically many elements of few classes. Caching only hits
        // would leave the unseeded case -- the common one -- paying a round trip
        // per element for an answer that will not change within the drain.
        var cir = new StubCir([]);
        var resolver = Resolver(cir);

        await resolver.ResolveAsync(EcClass, EcClassId);
        await resolver.ResolveAsync(EcClass, EcClassId);
        await resolver.ResolveAsync(EcClass, EcClassId);

        Assert.Equal(1, cir.Calls);
    }

    [Fact]
    public async Task Registers_the_missing_mapping_using_rdls_own_class_identity()
    {
        // The point of registering rather than deriving: the CIRID that lands in
        // CIR must be the GUID RDL minted for the class, because that is the
        // value every other participant holding the library already knows.
        var cir = new RecordingCir();

        var resolver = Resolver(
            cir, register: true, rdl: new StubRdl(new RdlClass { Code = RdlKey, Name = "Streetlight", Uuid = Governed }));

        Assert.Equal(Governed, await resolver.ResolveAsync(EcClass, EcClassId));

        var entry = Assert.Single(
            Assert.Single(Assert.Single(cir.Registered!.Registry).Categories).Entries);

        // Keyed on ENG's internal class id, matching what the segment publishes
        // as IDInInfoSource. A receiver holding the segment can look the entry
        // up by the id it was given; the class name is a label beside it.
        Assert.Equal(EcClassId.ToString(), entry.IdInSource);
        Assert.Equal(EcClass, entry.Name);
        Assert.Equal(Governed, entry.Cirid);

        // Minting is RDL's job. Asking CIR to make one would produce exactly the
        // invented identity this whole path exists to avoid.
        Assert.False(cir.Registered.CreateCirid);
    }

    [Fact]
    public async Task Registers_nothing_when_rdl_offers_no_identity_for_the_class()
    {
        // A class the library has no GUID for cannot be registered without
        // inventing one, and an invented identity in CIR is worse than a gap:
        // the next lookup would find it and publish it as governed.
        var cir = new RecordingCir();

        var resolver = Resolver(
            cir, register: true, rdl: new StubRdl(new RdlClass { Code = RdlKey }));

        Assert.Null(await resolver.ResolveAsync(EcClass, EcClassId));
        Assert.Null(cir.Registered);
    }

    [Fact]
    public async Task Registers_nothing_when_cir_could_not_be_reached()
    {
        // An unreachable CIR is not a miss. Writing on it would race a mapping
        // that may already exist and simply could not be seen.
        var resolver = Resolver(
            new UnreachableCir(), register: true,
            rdl: new StubRdl(new RdlClass { Code = RdlKey, Uuid = Governed }));

        Assert.Null(await resolver.ResolveAsync(EcClass, EcClassId));
    }

    [Fact]
    public async Task Retries_registration_after_rdl_starts_holding_the_class()
    {
        // The failure this guards against, seen in Azure: a reset left RDL
        // briefly without the class, ENG cached the resulting null, and the
        // cross-reference then stayed missing for the whole cache duration even
        // though RDL was correct within seconds. Elements kept publishing on the
        // derived fallback throughout, so nothing looked broken.
        //
        // A null nobody settled is not an answer, so the next drain must ask
        // again rather than remember it.
        var cir = new RecordingCir();
        var rdl = new MutableRdl(null);
        var resolver = Resolver(cir, register: true, rdl: rdl);

        Assert.Null(await resolver.ResolveAsync(EcClass, EcClassId));
        Assert.Null(cir.Registered);

        // RDL finishes starting up, or its reference data is re-seeded.
        rdl.Holding = new RdlClass { Code = RdlKey, Name = "Streetlight", Uuid = Governed };

        Assert.Equal(Governed, await resolver.ResolveAsync(EcClass, EcClassId));
        Assert.NotNull(cir.Registered);
    }

    [Fact]
    public async Task Retries_registration_after_rdl_becomes_reachable()
    {
        // Same rule for the other RDL-side failure. An exception reaching the
        // library says nothing about whether the class is there.
        var cir = new RecordingCir();
        var rdl = new MutableRdl(null) { Throw = true };
        var resolver = Resolver(cir, register: true, rdl: rdl);

        Assert.Null(await resolver.ResolveAsync(EcClass, EcClassId));

        rdl.Throw = false;
        rdl.Holding = new RdlClass { Code = RdlKey, Name = "Streetlight", Uuid = Governed };

        Assert.Equal(Governed, await resolver.ResolveAsync(EcClass, EcClassId));
        Assert.NotNull(cir.Registered);
    }

    [Fact]
    public async Task Does_not_re_ask_for_a_class_that_is_simply_unmapped()
    {
        // The counterpart, and the reason this is not just "stop caching nulls".
        // A class with no OutboundRdlClassMap entry is a decision recorded in
        // configuration, not a transient state: it will read the same way on
        // every drain, so re-asking per element would restore exactly the round
        // trip the cache exists to avoid.
        var cir = new StubCir([]);

        var resolver = Resolver(
            cir, register: true,
            rdl: new StubRdl(new RdlClass { Code = RdlKey, Uuid = Governed }));

        await resolver.ResolveAsync("ENG.Unmapped", 99);
        await resolver.ResolveAsync("ENG.Unmapped", 99);
        await resolver.ResolveAsync("ENG.Unmapped", 99);

        Assert.Equal(1, cir.Calls);
    }

    /// <summary>
    /// An RDL whose answer can change between drains, which is the whole point:
    /// a provider that is still starting up or has just been re-seeded gives a
    /// different answer a moment later.
    /// </summary>
    private sealed class MutableRdl(RdlClass? holding) : RdlTaxonomyValidator(
        null!,
        Options.Create(new EngRdlOptions { Enabled = false }),
        Options.Create(new EngEngineOptions()),
        NullLogger<RdlTaxonomyValidator>.Instance)
    {
        public RdlClass? Holding { get; set; } = holding;

        public bool Throw { get; set; }

        public override Task<RdlClass?> FindClassAsync(string rdlKey, CancellationToken ct = default) =>
            Throw
                ? throw new HttpRequestException("RDL is not answering.")
                : Task.FromResult(rdlKey == RdlKey ? Holding : null);
    }

    private sealed class StubRdl(RdlClass? rdlClass) : RdlTaxonomyValidator(
        null!,
        Options.Create(new EngRdlOptions { Enabled = false }),
        Options.Create(new EngEngineOptions()),
        NullLogger<RdlTaxonomyValidator>.Instance)
    {
        public override Task<RdlClass?> FindClassAsync(string rdlKey, CancellationToken ct = default) =>
            Task.FromResult(rdlKey == RdlKey ? rdlClass : null);
    }

    private sealed class RecordingCir : ICirClient
    {
        public CreateRegistryRequest? Registered { get; private set; }

        public Task<IReadOnlyList<CirRegistry>> GetRegistryAsync(
            IReadOnlyList<CirFilter> filters, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CirRegistry>>([]);

        public Task<int> RegisterEntriesAsync(CreateRegistryRequest request, CancellationToken ct)
        {
            Registered = request;
            return Task.FromResult(1);
        }

        public Task<int> CancelEntriesAsync(IReadOnlyList<CirEntryIdentifier> entries, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StubCir(IReadOnlyList<CirRegistry> registries) : ICirClient
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<CirRegistry>> GetRegistryAsync(
            IReadOnlyList<CirFilter> filters, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(registries);
        }

        public Task<int> RegisterEntriesAsync(CreateRegistryRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<int> CancelEntriesAsync(IReadOnlyList<CirEntryIdentifier> entries, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class UnreachableCir : ICirClient
    {
        public Task<IReadOnlyList<CirRegistry>> GetRegistryAsync(
            IReadOnlyList<CirFilter> filters, CancellationToken ct) =>
            throw new HttpRequestException("CIR is not answering.");

        public Task<int> RegisterEntriesAsync(CreateRegistryRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<int> CancelEntriesAsync(IReadOnlyList<CirEntryIdentifier> entries, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
