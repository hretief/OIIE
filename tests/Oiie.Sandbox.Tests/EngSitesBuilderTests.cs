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
        new(ITwinId, "9100", null, DateTime.UtcNow, "9100 - District 1",
            "9100", "Thing", "Asset", "Asset");

    [Fact]
    public void Sync_sites_carries_the_plural_noun_the_receiving_legs_match_on()
    {
        var builder = new EngSitesBuilder(Options.Create(new EngEngineOptions
        {
            SourceId = "ENG",
            LogicalId = "ENG"
        }));

        var envelope = BodEnvelope.Parse(
            builder.Build(Twin(), SiteTypeUuid, "corr-1").ToString());

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
            builder.Build(Twin(), SiteTypeUuid, "corr-2").ToString());

        var site = envelope.NounsAs(e => new Oiie.Ccom.Types.Site(e)).Single();

        // The same value EngSegmentsBuilder puts in Segment.RegistrationSite.
        // If these two ever diverge, REG-LOCATION creates a scope under one guid
        // and then defers every segment that names the other.
        Assert.Equal(ITwinId, site.UUID);
    }
}
