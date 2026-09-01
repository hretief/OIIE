using Oiie.Ccom.Types;

namespace CmsEngine.Application;

/// <summary>
/// Why a site could not be recorded in CMS.
/// </summary>
public enum SiteRejection
{
    None = 0,

    /// <summary>No federation GUID, so the site would have no shared identity.</summary>
    NoFederationId,

    /// <summary>No ShortName, so CMS would have no code to refer to the site by.</summary>
    NoCode,

    /// <summary>Nothing usable as a name.</summary>
    NoName
}

/// <summary>
/// The outcome of translating one site into what CmsProvider's site upsert needs.
/// </summary>
public sealed record SiteMappingResult(
    SiteRejection Rejection,
    Guid SiteId,
    string SiteCode,
    string SiteName,
    string? Description,
    string? SiteType)
{
    public bool IsMapped => Rejection == SiteRejection.None;

    public static SiteMappingResult Rejected(SiteRejection rejection) =>
        new(rejection, Guid.Empty, string.Empty, string.Empty, null, null);
}

/// <summary>
/// Translates an incoming CCOM Site into what CMS needs to hold it.
///
/// Pure and synchronous: every judgement about what an arriving site means is
/// made here where it can be tested without a broker or a provider, and the
/// service decides what to do with the answer.
///
/// Far simpler than REG-LOCATION's equivalent, and legitimately so. That
/// registry turns one site into a scope, an item and a serial because it models
/// the site's type separately from the site itself. CMS has a flat Site table,
/// so the mapping is one row and the type is a column on it.
/// </summary>
public sealed class IncomingSiteMapper
{
    public SiteMappingResult Map(Site site)
    {
        // The federated identity, which this leg cannot invent: a minted GUID
        // would look like it worked, and the same site would exist twice across
        // the federation with nothing to show they were ever one thing. It is
        // also the provider's primary key, so there is nothing to upsert onto.
        if (site.UUID == Guid.Empty)
        {
            return SiteMappingResult.Rejected(SiteRejection.NoFederationId);
        }

        // ShortName is the code, per the agreed mapping. No fallback to
        // IDInInfoSource: the code is how an operator refers to the plant, and
        // quietly substituting the publisher's internal identifier would put a
        // foreign key space in a column people read.
        var code = Trimmed(site.ShortName);
        if (code is null)
        {
            return SiteMappingResult.Rejected(SiteRejection.NoCode);
        }

        // FullName is the name, falling back to the code rather than being
        // rejected. A site that knows what it is called by operators but has no
        // long-form name is still a usable site.
        var name = Trimmed(site.FullName) ?? code;

        return new SiteMappingResult(
            SiteRejection.None,
            SiteId: site.UUID,
            SiteCode: code,
            SiteName: name,
            Description: Trimmed(site.Description),

            // Optional, unlike REG-LOCATION's site type. There it is a row that
            // later sites are reconciled against, so a missing one is fatal;
            // here it is a descriptive column, and refusing the site over it
            // would reject a plant CMS could otherwise monitor.
            SiteType: Trimmed(site.Type?.ShortName));
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
