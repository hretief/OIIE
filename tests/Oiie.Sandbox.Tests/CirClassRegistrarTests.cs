using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Oiie.Cir.Client;
using RegLocationEngine.Application;
using Xunit;

namespace Oiie.Sandbox.Tests;

/// <summary>
/// The inbound half of the class cross-reference.
///
/// The behaviour worth pinning is not that an entry gets written -- it is what
/// the registrar refuses to write. An inbound Segment always carries a
/// Type.UUID, and a publisher that could not resolve its class still sends one,
/// derived locally from the class key. Mirroring that would put an invented
/// identity into CIR under REG-LOCATION's name, and the next reader would take
/// it for governed. So the identity is verified against CIR before it is
/// mirrored, and the tests below are mostly about that refusal.
/// </summary>
public class CirClassRegistrarTests
{
    private static readonly Guid Governed = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task Registers_the_local_class_against_an_identity_CIR_already_holds()
    {
        // ENG's outbound leg has already registered this identity, which is what
        // makes it trustworthy: RDL minted it and a participant vouched for it.
        var cir = new RecordingCir(
            Entry("ENG", "Streetlight", Governed, "rdl:Streetlight"));

        await Registrar(cir).EnsureAsync(1703, Governed, "rdl:Streetlight");

        var request = Assert.Single(cir.Registered);
        var entry = request.Registry[0].Categories[0].Entries[0];

        Assert.Equal("RDL-CLASS", request.Registry[0].Categories[0].Id);
        Assert.Equal("REG-LOCATION", entry.SourceId);
        Assert.Equal("1703", entry.IdInSource);

        // The GUID is RDL's, carried through untouched. Asking CIR to mint one
        // would produce a second identity for a class that already has one, and
        // the two halves would no longer meet.
        Assert.Equal(Governed, entry.Cirid);
        Assert.False(request.CreateCirid);
    }

    [Fact]
    public async Task Refuses_an_identity_no_participant_has_registered()
    {
        // Nothing in CIR vouches for this GUID, so it is most likely a sender's
        // derived fallback. Writing it would launder it into the registry.
        var cir = new RecordingCir();

        await Registrar(cir).EnsureAsync(1703, Guid.NewGuid(), "rdl:Streetlight");

        Assert.Empty(cir.Registered);
    }

    [Fact]
    public async Task Does_nothing_when_the_segment_carried_no_class_identity()
    {
        var cir = new RecordingCir();

        await Registrar(cir).EnsureAsync(1001, null, null);

        Assert.Empty(cir.Queried);
        Assert.Empty(cir.Registered);
    }

    [Fact]
    public async Task Writes_once_for_a_drain_of_many_segments_of_one_class()
    {
        var cir = new RecordingCir(Entry("ENG", "Streetlight", Governed, "rdl:Streetlight"));
        var registrar = Registrar(cir);

        for (var i = 0; i < 5; i++)
        {
            await registrar.EnsureAsync(1703, Governed, "rdl:Streetlight");
        }

        Assert.Single(cir.Registered);
        Assert.Single(cir.Queried);
    }

    [Fact]
    public async Task Leaves_the_cross_reference_alone_when_it_is_already_ours()
    {
        var cir = new RecordingCir(Entry("REG-LOCATION", "1703", Governed, "rdl:Streetlight"));

        await Registrar(cir).EnsureAsync(1703, Governed, "rdl:Streetlight");

        Assert.Empty(cir.Registered);
    }

    [Fact]
    public async Task An_unreachable_CIR_is_not_cached_as_an_answer()
    {
        // A failed call and a genuine miss both leave the cross-reference
        // unmade, and only one of them is a fact. Caching the failure would
        // keep the mapping missing for the whole cache duration after CIR came
        // back up.
        var cir = new FailingCir();
        var registrar = Registrar(cir);

        await registrar.EnsureAsync(1703, Governed, "rdl:Streetlight");
        await registrar.EnsureAsync(1703, Governed, "rdl:Streetlight");

        Assert.Equal(2, cir.Attempts);
    }

    [Fact]
    public async Task Stays_out_of_CIR_when_registration_is_switched_off()
    {
        var cir = new RecordingCir();

        var registrar = new CirClassRegistrar(
            cir,
            Options.Create(new RegLocationEngineOptions
            {
                CirBaseUrl = "https://cir.example/api",
                RegisterClassIdentityInCir = false
            }),
            NullLogger<CirClassRegistrar>.Instance);

        await registrar.EnsureAsync(1703, Governed, "rdl:Streetlight");

        Assert.Empty(cir.Queried);
    }

    private static CirClassRegistrar Registrar(ICirClient cir) =>
        new(cir,
            Options.Create(new RegLocationEngineOptions
            {
                Enterprise = "acme",
                SourceId = "REG-LOCATION",
                CirBaseUrl = "https://cir.example/api"
            }),
            NullLogger<CirClassRegistrar>.Instance);

    private static CirEntry Entry(string sourceId, string idInSource, Guid cirid, string name) =>
        new(IdInSource: idInSource,
            SourceId: sourceId,
            Cirid: cirid,
            SourceOwnerId: "acme",
            Name: name,
            Description: null,
            Properties: []);

    private sealed class RecordingCir(params CirEntry[] entries) : ICirClient
    {
        public List<CreateRegistryRequest> Registered { get; } = [];

        public List<IReadOnlyList<CirFilter>> Queried { get; } = [];

        public Task<IReadOnlyList<CirRegistry>> GetRegistryAsync(
            IReadOnlyList<CirFilter> filters, CancellationToken ct)
        {
            Queried.Add(filters);

            IReadOnlyList<CirRegistry> result = entries.Length == 0
                ? []
                : [
                    new CirRegistry(
                        Id: "acme",
                        Description: [],
                        Categories:
                        [
                            new CirCategory(
                                Id: "RDL-CLASS",
                                SourceId: "RDL",
                                Description: [],
                                Entries: entries)
                        ])
                  ];

            return Task.FromResult(result);
        }

        public Task<int> RegisterEntriesAsync(CreateRegistryRequest request, CancellationToken ct)
        {
            Registered.Add(request);
            return Task.FromResult(1);
        }

        public Task<int> CancelEntriesAsync(
            IReadOnlyList<CirEntryIdentifier> entries, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FailingCir : ICirClient
    {
        public int Attempts { get; private set; }

        public Task<IReadOnlyList<CirRegistry>> GetRegistryAsync(
            IReadOnlyList<CirFilter> filters, CancellationToken ct)
        {
            Attempts++;
            throw new HttpRequestException("CIR is cold.");
        }

        public Task<int> RegisterEntriesAsync(CreateRegistryRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<int> CancelEntriesAsync(
            IReadOnlyList<CirEntryIdentifier> entries, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
