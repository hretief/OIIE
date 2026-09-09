using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using EngEngine.Application;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// The ENG composite key on the wire.
///
/// ENG's native identity is three values -- iModelId, ECInstanceId, CodeValue --
/// and none of them identifies an element alone: ECInstanceId is unique only
/// within one iModel, and CodeValue can repeat across the iModels of a single
/// iTwin. A receiver that holds a partial key holds something that resolves to
/// several elements or to none, and it cannot register ENG's identification
/// against the FederationGuid in the CIR.
///
/// These tests pin which BOD field carries which part. They exist because the
/// mapping is a wire contract with downstream participants that the compiler
/// cannot check: InfoSource.UUID previously carried a hash of the literal "ENG",
/// which is well-formed, plausible on inspection, and silently useless.
/// </summary>
public class EngSegmentsBuilderTests
{
    private static readonly Guid IModelId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ITwinId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FederationGuid = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");

    private static EngSegmentsBuilder Builder() =>
        new(Options.Create(new EngEngineOptions
        {
            IModelId = IModelId,
            SourceId = "ENG",
            LogicalId = "ENG"
        }),
        NullLogger<EngSegmentsBuilder>.Instance);

    // A builder whose outbound map carries one entry, so a test can exercise
    // the mapped path without depending on whichever classes the shipped
    // default happens to contain.
    private static EngSegmentsBuilder BuilderMapping(string ecClass, string rdlKey) =>
        new(Options.Create(new EngEngineOptions
        {
            IModelId = IModelId,
            SourceId = "ENG",
            LogicalId = "ENG",
            OutboundRdlClassMap = new(StringComparer.OrdinalIgnoreCase)
            {
                [ecClass] = rdlKey
            }
        }),
        NullLogger<EngSegmentsBuilder>.Instance);

    private static EngNamedVersion Marker() =>
        new(1, Guid.NewGuid(), IModelId, "Design Release 3", null,
            "abc", 3, "engineer", DateTime.UtcNow, DateTime.UtcNow, 1);

    private static EngElement Element(string? codeValue = "TIC-106") =>
        ElementWith(FederationGuid, codeValue);

    // Separate from Element so that passing null means null. A defaulted
    // parameter coalescing to the fixture guid cannot express "unfederated",
    // which is the case IsPublishable exists to catch.
    private static EngElement ElementWith(Guid? federationGuid, string? codeValue = "TIC-106") =>
        new(44732, IModelId, 99, "Bis:PhysicalElement", federationGuid,
            codeValue, "Temperature Indicator Controller", "TIC-106 Controller",
            null, 3, DateTime.UtcNow, DateTime.UtcNow);

    private static Segment FirstSegment(System.Xml.Linq.XElement bod)
    {
        // Parsed back through BodEnvelope rather than read off the builder's
        // objects: that is the path a receiving participant takes, so a field
        // that fails to serialise fails here too.
        var envelope = BodEnvelope.Parse(bod.ToString());
        return envelope.NounsAs(e => new Segment(e)).Single();
    }

    [Fact]
    public void Segment_uuid_is_the_federation_guid_unaltered()
    {
        var bod = Builder().Build(Marker(), [Element()], ITwinId, "corr-1");

        // The CIRID. Every participant registers its own key against this value,
        // so any transformation here silently splits one asset into two.
        Assert.Equal(FederationGuid, FirstSegment(bod).UUID);
    }

    [Fact]
    public void InfoSource_uuid_carries_the_imodel_id_not_a_hash_of_the_source_name()
    {
        var bod = Builder().Build(Marker(), [Element()], ITwinId, "corr-2");

        var infoSource = FirstSegment(bod).InfoSource;

        Assert.Equal(IModelId, infoSource?.UUID);

        // The specific value it must not be: a hash of "ENG" names the kind of
        // system rather than the instance, which leaves a receiver unable to say
        // which iModel to open element 44732 in.
        Assert.NotEqual(CcomUuid.ForInfoSource("ENG"), infoSource?.UUID);

        // ShortName still says who sent it, so the source stays readable.
        Assert.Equal("ENG", infoSource?.ShortName);
    }

    [Fact]
    public void InfoSource_uuid_follows_the_marker_not_the_configured_poll_filter()
    {
        // The configured id is a poll filter; the marker says what was actually
        // read. Every other test uses one guid for both, so it cannot tell which
        // of the two the builder used -- this one gives them different values.
        //
        // The case is real rather than theoretical: a provider reset regenerates
        // iModel ids, and the setting that names one goes stale. A stale id that
        // matches nothing fails the lookup and is obvious. A stale id that
        // matches a different live model publishes successfully with someone
        // else's provenance, which is not.
        var otherModel = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var builder = new EngSegmentsBuilder(Options.Create(new EngEngineOptions
        {
            IModelId = otherModel,
            SourceId = "ENG",
            LogicalId = "ENG"
        }),
        NullLogger<EngSegmentsBuilder>.Instance);

        var bod = builder.Build(Marker(), [Element()], ITwinId, "corr-3");

        Assert.Equal(IModelId, FirstSegment(bod).InfoSource?.UUID);
        Assert.NotEqual(otherModel, FirstSegment(bod).InfoSource?.UUID);
    }

    [Fact]
    public void Ec_instance_id_and_code_value_complete_the_composite_key()
    {
        var bod = Builder().Build(Marker(), [Element()], ITwinId, "corr-3");

        var segment = FirstSegment(bod);

        Assert.Equal("44732", segment.IDInInfoSource);
        Assert.Equal("TIC-106", segment.ShortName);
    }

    [Fact]
    public void Segment_type_does_not_reuse_the_imodel_info_source()
    {
        // A mapped class, because an unmapped one no longer produces a Type at
        // all -- see Unmapped_ec_class_publishes_without_a_segment_type.
        var bod = BuilderMapping("Bis:PhysicalElement", "rdl:LightingUnit")
            .Build(Marker(), [Element()], ITwinId, "corr-4");

        var segment = FirstSegment(bod);

        // The RDL class is not an element of the iModel the way a segment is.
        // Sharing the InfoSource would say ECInstanceId and the class key are
        // two identifiers within one source, and a receiver composing the
        // composite key from that pair would build one that resolves to nothing.
        Assert.NotNull(segment.Type);
        Assert.NotEqual(IModelId, segment.Type?.InfoSource?.UUID);
    }

    [Fact]
    public void Mapped_ec_class_publishes_the_rdl_key_not_the_ec_class()
    {
        var bod = BuilderMapping("Bis:PhysicalElement", "rdl:LightingUnit")
            .Build(Marker(), [Element()], ITwinId, "corr-6");

        var segment = FirstSegment(bod);

        // The point of DR-030: what travels is the governed key, not ENG's own
        // vocabulary. A receiver resolves this against the shared library, which
        // it could not do with "Bis:PhysicalElement".
        Assert.Equal("rdl:LightingUnit", segment.Type?.IDInInfoSource);
        Assert.Equal("MIMOSA-RDL", segment.Type?.InfoSource?.ShortName);
    }

    [Fact]
    public void Unmapped_ec_class_publishes_without_a_segment_type()
    {
        // The gap is left visible rather than filled in. Emitting the EC class
        // here would tell the receiver to resolve a key against a library that
        // has never heard of it; emitting a guessed RDL key would put a false
        // statement on the bus that every consumer then records as fact. The
        // segment still carries its identity, so nothing is lost but the class.
        var bod = Builder().Build(Marker(), [Element()], ITwinId, "corr-7");

        Assert.Null(FirstSegment(bod).Type);
    }

    [Fact]
    public void Registration_site_carries_the_itwin_not_the_imodel()
    {
        var bod = Builder().Build(Marker(), [Element()], ITwinId, "corr-5");

        var segment = FirstSegment(bod);

        // The twin, unaltered: REG-LOCATION files this as the Scope, and it has
        // to be the same value SyncSites registered the site under or the
        // segments land in a second scope for a plant that only has one.
        Assert.Equal(ITwinId, segment.RegistrationSite?.UUID);

        // The distinction this whole pairing exists to make. REG-LOCATION has no
        // concept of an iModel and takes segments from every model under a twin
        // equally, so the twin scopes it while the iModel stays in InfoSource for
        // whoever needs to resolve the element back in ENG.
        Assert.Equal(IModelId, segment.InfoSource?.UUID);
        Assert.NotEqual(segment.InfoSource?.UUID, segment.RegistrationSite?.UUID);
    }

    [Fact]
    public void Registration_site_is_omitted_when_the_twin_is_unknown()
    {
        var bod = Builder().Build(Marker(), [Element()], Guid.Empty, "corr-6");

        // Unscoped rather than scoped to Guid.Empty. An empty guid is a value a
        // receiver would file against, which would collect the segments of every
        // twin whose owner could not be resolved into one scope.
        Assert.Null(FirstSegment(bod).RegistrationSite);
    }

    [Fact]
    public void An_element_without_a_federation_guid_is_not_publishable()
    {
        // Publishing nothing is recoverable; publishing a fabricated identity is
        // not, because it looks correct until the element is really federated.
        Assert.False(EngSegmentsBuilder.IsPublishable(ElementWith(null)));
        Assert.True(EngSegmentsBuilder.IsPublishable(Element()));
    }
}
