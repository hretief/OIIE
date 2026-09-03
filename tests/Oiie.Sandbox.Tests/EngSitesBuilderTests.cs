using Microsoft.Extensions.Options;
using EngEngine.Application;
using Oiie.Ccom.Oagis;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// The noun a SyncSites publication actually carries on the wire.
///
/// This exists because the receiving legs did not agree with the sender and
/// nothing caught it. <c>BodEnvelope</c> derives the noun by stripping the verb
/// from the root element name, so <c>SyncSites</c> is "Sites" -- but three site
/// ingestion services tested for the singular "Site", which matches nothing.
///
/// The failure was invisible by construction: a BOD whose noun is unrecognised
/// is skipped rather than failed, because a shared channel is expected to carry
/// messages a participant has no handler for. So the sites leg read its message,
/// discarded it, and reported a clean run with zero sites registered.
/// </summary>
public class EngSitesBuilderTests
{
    private static readonly Guid ITwinId = Guid.Parse("523099d2-4291-4d0f-ad7c-65429109ef81");
    private static readonly Guid SiteTypeUuid = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static EngITwin Twin() =>
        new(ITwinId,
            CreatedUtc: DateTime.UtcNow,
            Class: "Thing",
            SubClass: "Asset",

            // The twin's own free-text copy of the boundary name. Deliberately
            // different from the type row below: the published name must come
            // from the row that owns it, and identical values would not show
            // which one was used.
            Type: "district (as typed)",

            DisplayName: "9100 - District 1",
            Number: "9100",
            Status: "active",
            ITwinTypeId: SiteTypeUuid,
            ITwinTypeNumber: "District");

    [Fact]
    public void Sync_sites_carries_the_plural_noun_the_receiving_legs_match_on()
    {
        var builder = new EngSitesBuilder(Options.Create(new EngEngineOptions
        {
            SourceId = "ENG",
            LogicalId = "ENG"
        }));

        var envelope = BodEnvelope.Parse(
            builder.Build(Twin(), "corr-1").ToString());

        Assert.Equal("Sync", envelope.Verb);

        // Plural. The singular is the value that silently broke every site leg.
        Assert.Equal("Sites", envelope.Noun);
        Assert.True(envelope.Is("Sync", "Sites"));
        Assert.False(envelope.Is("Sync", "Site"));
    }

    [Fact]
    public void The_site_carries_the_twin_id_segments_register_against()
    {
        var builder = new EngSitesBuilder(Options.Create(new EngEngineOptions
        {
            SourceId = "ENG",
            LogicalId = "ENG"
        }));

        var envelope = BodEnvelope.Parse(
            builder.Build(Twin(), "corr-2").ToString());

        var site = envelope.NounsAs(e => new Oiie.Ccom.Types.Site(e)).Single();

        // The same value EngSegmentsBuilder puts in Segment.RegistrationSite.
        // If these two ever diverge, REG-LOCATION creates a scope under one guid
        // and then defers every segment that names the other.
        Assert.Equal(ITwinId, site.UUID);
    }

    [Fact]
    public void The_site_type_is_labelled_by_boundary_not_by_lifecycle()
    {
        var builder = new EngSitesBuilder(Options.Create(new EngEngineOptions
        {
            SourceId = "ENG",
            LogicalId = "ENG"
        }));

        var envelope = BodEnvelope.Parse(
            builder.Build(Twin(), "corr-3").ToString());

        var site = envelope.NounsAs(e => new Oiie.Ccom.Types.Site(e)).Single();

        // From dbo.iTwinType.Number, not the twin's free-text Type column. The
        // twin carries 'district (as typed)'; publishing that would let the
        // unguarded copy drift into the BOD and disagree with the UUID below.
        Assert.Equal("District", site.Type!.ShortName);

        // The sample carries no label for the boundary, and ENG has no source
        // for one, so nothing is invented here.
        Assert.Null(site.Type!.FullName);

        // The stored boundary id, carried through rather than derived from the
        // name: that is what keeps a renamed boundary from reclassifying the
        // sites already published under it.
        Assert.Equal(SiteTypeUuid, site.Type!.UUID);
        Assert.Equal(SiteTypeUuid.ToString(), site.Type!.IDInInfoSource);
    }
}
