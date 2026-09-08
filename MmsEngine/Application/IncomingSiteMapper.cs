using Oiie.Ccom.Types;

namespace MmsEngine.Application;

/// <summary>
/// Why a site could not be recorded in MMS as a TAMS owner.
/// </summary>
public enum SiteRejection
{
    None = 0,

    /// <summary>No federation GUID, so the owner would have no shared identity.</summary>
    NoFederationId,

    /// <summary>Nothing usable as a name.</summary>
    NoName
}

/// <summary>
/// The outcome of translating one CCOM site into what MMS needs to resolve it
/// to a SETUP_OWNER row.
/// </summary>
/// <param name="Cirid">
/// The site's federation GUID, used to ask CIR which owner MMS already holds
/// for it. Not stored in MMS: SETUP_OWNER has no column for it, which is
/// exactly why the registry is consulted rather than a local column.
/// </param>
public sealed record SiteMappingResult(
    SiteRejection Rejection,
    Guid Cirid,
    string OwnerName)
{
    public bool IsMapped => Rejection == SiteRejection.None;

    public static SiteMappingResult Rejected(SiteRejection rejection) =>
        new(rejection, Guid.Empty, string.Empty);
}

/// <summary>
/// Translates an incoming CCOM Site into what MMS needs to record it as a TAMS
/// owner.
///
/// Pure and synchronous: every judgement about what an arriving site means is
/// made here where it can be tested without a broker or a provider, and the
/// service decides what to do with the answer.
///
/// A site maps onto SETUP_OWNER, not onto LIGHT_SYSTEM_INVENTORY. TAMS has no
/// table called Sites, but it does have the concept: an owner is the context a
/// work order is raised within, which is what a CCOM Site is. A light system is
/// an asset standing at a place -- inventory that exists within a site rather
/// than being one. Mapping a site onto a light system would have MMS assert a
/// lighting installation exists on the strength of a message that said nothing
/// about lighting, and would have required inventing a class code to satisfy a
/// NOT NULL constraint that only applies because the table was the wrong one.
///
/// Deliberately identical in shape to CmsEngine's mapper rather than shared.
/// The two engines are separate integrations onto separate customer systems,
/// and a common mapper would mean one system's mapping rule could not change
/// without changing the other's -- which is exactly the coupling these
/// boundaries exist to prevent.
/// </summary>
public sealed class IncomingSiteMapper
{
    public SiteMappingResult Map(Site site)
    {
        // The federated identity, which this leg cannot invent: a minted GUID
        // would look like it worked, and the same site would exist twice across
        // the federation with nothing to show they were ever one thing. It is
        // also the only key CIR can be asked about, so there is nothing to
        // resolve without it.
        if (site.UUID == Guid.Empty)
        {
            return SiteMappingResult.Rejected(SiteRejection.NoFederationId);
        }

        // FullName only: SETUP_OWNER.OWNER_NAME corresponds to Sites/Site/FullName.
        // ShortName is deliberately not a fallback -- it names a different thing,
        // so accepting it would resolve a site to the wrong owner, or create a
        // duplicate owner under a name the customer never used.
        var name = Trimmed(site.FullName);
        if (name is null)
        {
            return SiteMappingResult.Rejected(SiteRejection.NoName);
        }

        return new SiteMappingResult(
            SiteRejection.None,
            Cirid: site.UUID,
            OwnerName: name);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
