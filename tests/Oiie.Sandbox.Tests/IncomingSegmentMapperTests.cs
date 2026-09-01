using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Oiie.Ccom.Types;
using RegLocationEngine.Application;
using RegLocationEngine.Infrastructure.RegLocation;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// What an arriving segment becomes on the way into REG-LOCATION.
///
/// The contract pinned here is that arrival is not acceptance. ENG publishes a
/// design proposal; the registry must receive it as something a steward still
/// has to decide on. The mechanical expression of that is a single field --
/// State -- and getting it wrong produces an integration that looks perfect:
/// segments flow, tags appear, and every one of them is admitted to the registry
/// without a human ever seeing it. Nothing else in the system would notice.
///
/// The class mapping is pinned for a duller reason. ENG names its EC class and
/// REG-LOCATION numbers its own, and the two share no value that can be joined
/// on, so the correspondence is a table somebody maintains by hand. These tests
/// are what say out loud that an unmapped class is filed rather than dropped.
/// </summary>
public class IncomingSegmentMapperTests
{
    private static readonly Guid EngGuid = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    private static IncomingSegmentMapper Mapper(RegLocationEngineOptions? options = null) =>
        new(
            Options.Create(options ?? new RegLocationEngineOptions
            {
                InboundItemId = 42,
                InboundScopeId = 7,
                InboundFallbackClassId = 1001,
                InboundClassMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Functional:FunctionalComponentElement"] = 1701
                }
            }),
            NullLogger<IncomingSegmentMapper>.Instance);

    private static Segment Incoming(
        Guid? uuid = null,
        string? shortName = "TIC-101",
        string? fullName = "Temperature Indicator Controller",
        string? className = "Functional:FunctionalComponentElement") =>
        new()
        {
            UUID = uuid ?? EngGuid,
            IDInInfoSource = "0x20000000004",
            ShortName = shortName,
            FullName = fullName,
            Type = className is null ? null : new SegmentType { IDInInfoSource = className }
        };

    [Fact]
    public void An_incoming_segment_is_filed_as_proposed_never_approved()
    {
        var result = Mapper().Map(Incoming());

        // The gate. If this ever reads Approved, SC01's stewardship step has been
        // bypassed silently -- the tag would be published onward by this engine's
        // own outbound leg without any steward having decided anything.
        Assert.Equal("Proposed", result.Request!.State);
    }

    [Fact]
    public void The_federation_guid_travels_unaltered()
    {
        var result = Mapper().Map(Incoming());

        // The identity ENG and REG-LOCATION have in common. Re-minting it here
        // would fork the location into two identities across the federation.
        Assert.Equal(EngGuid, result.Request!.Guid);
    }

    [Fact]
    public void A_segment_without_a_federation_guid_is_refused()
    {
        var result = Mapper().Map(Incoming(uuid: Guid.Empty));

        // Refused rather than given a fresh GUID. Minting one would look like it
        // worked and would put an unfederated duplicate into the registry.
        Assert.False(result.IsMapped);
        Assert.Equal(SegmentRejection.NoFederationId, result.Rejection);
    }

    [Fact]
    public void The_short_name_becomes_the_tag_code()
    {
        var result = Mapper().Map(Incoming());

        // ENG.Element.CodeValue equates to REG-LOCATION.Tag.Code. This is the
        // correspondence the whole scenario rests on.
        Assert.Equal("TIC-101", result.Request!.Code);
    }

    [Fact]
    public void The_sender_identifier_is_the_code_of_last_resort()
    {
        var result = Mapper().Map(Incoming(shortName: null));

        // Not FullName: a label may repeat and may be edited, whereas the
        // sender's own record id at least identifies the thing in the sender.
        Assert.Equal("0x20000000004", result.Request!.Code);
    }

    [Fact]
    public void A_segment_with_nothing_to_name_it_is_refused()
    {
        var segment = Incoming(shortName: null, fullName: null);
        segment.IDInInfoSource = null;

        var result = Mapper().Map(segment);

        Assert.False(result.IsMapped);
        Assert.Equal(SegmentRejection.NoCode, result.Rejection);
    }

    [Fact]
    public void A_mapped_class_resolves_to_the_registry_class()
    {
        var result = Mapper().Map(Incoming());

        Assert.Equal(1701, result.Request!.ClassId);
        Assert.True(result.ClassWasMapped);
    }

    [Fact]
    public void An_unmapped_class_is_filed_under_the_fallback_rather_than_dropped()
    {
        var result = Mapper().Map(Incoming(className: "Physical:Pipe"));

        // A gap in the mapping table is not a defect in the proposal. Dropping
        // the segment would hide the location from the one person able to notice
        // the table is wrong.
        Assert.True(result.IsMapped);
        Assert.Equal(1001, result.Request!.ClassId);
        Assert.False(result.ClassWasMapped);
    }

    [Fact]
    public void Class_names_match_regardless_of_the_senders_casing()
    {
        var result = Mapper().Map(Incoming(className: "functional:functionalcomponentelement"));

        Assert.Equal(1701, result.Request!.ClassId);
    }

    [Fact]
    public void A_segment_with_no_class_at_all_still_becomes_a_proposal()
    {
        var result = Mapper().Map(Incoming(className: null));

        Assert.True(result.IsMapped);
        Assert.Equal(1001, result.Request!.ClassId);
    }

    [Fact]
    public void Proposals_enter_at_revision_zero_not_the_senders_revision()
    {
        var result = Mapper().Map(Incoming());

        // A proposal has not been through a revision cycle in this registry.
        // Inheriting ENG's numbering would assert a history REG-LOCATION cannot
        // vouch for.
        Assert.Equal(0, result.Request!.Revision);
    }

    [Fact]
    public void The_bootstrapped_item_and_scope_are_used()
    {
        var result = Mapper().Map(Incoming());

        // A CCOM Segment carries neither; they arrive as Site context in a
        // different message this leg does not handle, so they are configuration.
        Assert.Equal(42, result.Request!.ItemId);
        Assert.Equal(7, result.Request!.ScopeId);
    }

    [Fact]
    public void The_full_name_becomes_the_tag_name()
    {
        var result = Mapper().Map(Incoming());

        // What a steward reads when deciding whether to admit the location.
        Assert.Equal("Temperature Indicator Controller", result.Request!.Name);
    }
}
