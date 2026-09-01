using System.Linq;
using Microsoft.Extensions.Options;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using RegLocationEngine.Application;
using RegLocationEngine.Infrastructure.RegLocation;
using RegLocationEngine.Infrastructure.State;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// The REG-LOCATION composite key on the wire, and the gate that decides what
/// reaches it at all.
///
/// Two contracts are pinned here and neither is checkable by the compiler.
///
/// The first is the identity mapping. REG-LOCATION's native key is scope plus
/// tag_id plus code, and none of them identifies a location alone: tag_id is
/// unique only within one registry instance, and a code repeats across revisions
/// and scopes. A receiver holding a partial key cannot register REG-LOCATION's
/// identification against the CIRID.
///
/// The second is the stewardship gate. SC01 exists to make design releases wait
/// for a steward, and the only mechanical expression of that is the refusal to
/// build a segment for an unapproved tag. A regression there would look like a
/// working integration -- messages would flow, and they would simply be
/// carrying decisions nobody made.
/// </summary>
public class RegLocationSegmentsBuilderTests
{
    private const int ScopeId = 7;
    private static readonly Guid IModelId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RegistryGuid = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");

    private static RegLocationSegmentsBuilder Builder() =>
        new(Options.Create(new RegLocationEngineOptions
        {
            IModelId = IModelId,
            SourceId = "REG-LOCATION",
            LogicalId = "REG-LOCATION"
        }));

    private static RegTagDetail Tag(
        string state = "Approved", Guid? guid = null, int revision = 3, int tagId = 4102) =>
        new(
            new RegTag(tagId, 11, 22, "TIC-106", revision, "Temperature Indicator Controller", state),
            new RegObject(33, 1, guid, ScopeId, 0, 0, null, null, null, null));

    // Separate from the default so that passing null means null. A defaulted
    // parameter coalescing to the fixture guid cannot express "unfederated",
    // which is one of the two cases IsPublishable exists to catch.
    private static RegTagDetail Federated(string state = "Approved", int revision = 3, int tagId = 4102) =>
        Tag(state, RegistryGuid, revision, tagId);

    private static Segment FirstSegment(System.Xml.Linq.XElement bod)
    {
        // Parsed back through BodEnvelope rather than read off the builder's
        // objects: that is the path a receiving participant takes, so a field
        // that fails to serialise fails here too.
        var envelope = BodEnvelope.Parse(bod.ToString());
        return envelope.NounsAs(e => new Segment(e)).First();
    }

    [Fact]
    public void Segment_uuid_is_the_registry_guid_unaltered()
    {
        var bod = Builder().Build([Federated()], "steward", "corr-1");

        // The CIRID. Every participant registers its own key against this value,
        // so any transformation here silently splits one location into two -- and
        // in particular splits it from the ENG element it was released from.
        Assert.Equal(RegistryGuid, FirstSegment(bod).UUID);
    }

    [Fact]
    public void Segment_carries_the_registry_composite_key()
    {
        var bod = Builder().Build([Federated()], "steward", "corr-2");

        var segment = FirstSegment(bod);

        // tag_id and code. Without both, plus the scope on the InfoSource, a
        // receiver cannot navigate back to the row this described.
        Assert.Equal("4102", segment.IDInInfoSource);
        Assert.Equal("TIC-106", segment.ShortName);
    }

    [Fact]
    public void InfoSource_uuid_is_derived_from_the_scope_not_the_source_name_alone()
    {
        var bod = Builder().Build([Federated()], "steward", "corr-3");

        var infoSource = FirstSegment(bod).InfoSource;

        // A hash of "REG-LOCATION" alone would name the kind of system rather
        // than the instance, so a receiver holding tag 4102 could not say which
        // registry to open it in. This is the failure EngSegmentsBuilder already
        // had once, and it is well-formed and plausible on inspection.
        Assert.Equal(CcomUuid.ForInfoSource($"REG-LOCATION:{ScopeId}"), infoSource?.UUID);
        Assert.Equal("REG-LOCATION", infoSource?.ShortName);
    }

    [Fact]
    public void Approver_travels_as_the_sender_reference()
    {
        var bod = Builder().Build([Federated()], "j.mokoena", "corr-4");

        var envelope = BodEnvelope.Parse(bod.ToString());

        // "On whose authority is this in the registry" is the question this
        // publication exists to let a receiver ask.
        Assert.Equal("j.mokoena", envelope.ApplicationArea?.Sender?.ReferenceID);
    }

    [Fact]
    public void A_proposed_tag_is_not_publishable()
    {
        // The gate itself. A proposed tag on the channel means the steward's
        // decision was bypassed, which is precisely what this engine exists to
        // prevent.
        Assert.False(RegLocationSegmentsBuilder.IsPublishable(Federated(state: "Proposed")));
    }

    [Fact]
    public void A_rejected_tag_is_not_publishable()
    {
        Assert.False(RegLocationSegmentsBuilder.IsPublishable(Federated(state: "Rejected")));
    }

    [Fact]
    public void A_tag_without_a_registry_guid_is_not_publishable()
    {
        // Publishing nothing is recoverable. Deriving an identity from the code
        // would not be: it looks correct until a real GUID appears, and then the
        // same location arrives downstream twice with no way to tell it was ever
        // one thing.
        Assert.False(RegLocationSegmentsBuilder.IsPublishable(Tag(guid: null)));
    }

    [Fact]
    public void An_approved_federated_tag_is_publishable()
    {
        Assert.True(RegLocationSegmentsBuilder.IsPublishable(Federated()));
    }

    [Fact]
    public void Idempotency_key_distinguishes_revisions_of_one_location()
    {
        var revision1 = RegLocationEngineState.KeyFor(RegistryGuid, 1);
        var revision2 = RegLocationEngineState.KeyFor(RegistryGuid, 2);

        // The GUID deliberately survives revision, so keying on it alone would
        // mean revision 2 counted as already published the moment revision 1 was,
        // and the correction would never leave the building.
        Assert.NotEqual(revision1, revision2);
    }

    [Fact]
    public void Idempotency_key_is_stable_for_a_repeated_notification()
    {
        // The notification path has no delivery guarantee and the sweep re-reads
        // the same approved tags every pass, so the same tag arrives here
        // repeatedly by design. It must resolve to one key or nothing is ever
        // recognised as already sent.
        Assert.Equal(
            RegLocationEngineState.KeyFor(RegistryGuid, 3),
            RegLocationEngineState.KeyFor(RegistryGuid, 3));
    }
}
