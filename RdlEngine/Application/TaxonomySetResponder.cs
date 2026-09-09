using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Oiie.Ccom.Extensions;
using RdlEngine.Infrastructure.Rdl;
using CcomRdlClass = Oiie.Ccom.Extensions.RdlClass;

namespace RdlEngine.Application;

/// <summary>
/// Answers a GetTaxonomySet with what RDL currently holds.
///
/// One taxonomy set per RDL namespace. A namespace is the unit a code is
/// qualified by, so it is the natural set boundary; collapsing every namespace
/// into one set would make ShortName useless as a selector, which is the only
/// selector this first cut honours.
/// </summary>
public sealed class TaxonomySetResponder(
    IRdlClient rdl,
    IOptions<RdlEngineOptions> options,
    ILogger<TaxonomySetResponder> logger)
{
    private readonly RdlEngineOptions _options = options.Value;

    /// <summary>
    /// Builds the ShowTaxonomySet for a request, or null if the document is not
    /// a GetTaxonomySet at all.
    ///
    /// A request naming a set this library does not hold gets an empty
    /// response, not a fault. The consumer asked a question — "what do you have
    /// under this name?" — and "nothing" answers it. A fault would be
    /// indistinguishable from the engine being broken, which is exactly the
    /// distinction a validating consumer needs to make.
    /// </summary>
    public async Task<XDocument?> RespondAsync(XDocument request, CancellationToken ct)
    {
        var parsed = TaxonomySetBods.ParseGetTaxonomySet(request);
        if (parsed is null) return null;

        var classes = await rdl.GetClassesAsync(namespaceId: null, ct);

        // Grouped by namespace, since that is the set boundary. Ordered so a
        // response is reproducible: an unordered response would make two
        // otherwise identical snapshots compare unequal.
        var sets = classes
            .GroupBy(c => c.NamespaceId)
            .Select(g => new
            {
                Name = _options.ResolveSetName(g.Key),
                Classes = g.OrderBy(c => c.ClassId).ToList()
            })
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        var wanted = parsed.Selectors
            .Select(s => s.ShortName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only ShortName is honoured in this first cut. A selector carrying
        // just a UUID or ChangedSince would otherwise silently behave as
        // "everything", which is a wrong answer rather than a partial one, so
        // it is logged where an operator can see it.
        var unsupported = parsed.Selectors
            .Where(s => string.IsNullOrWhiteSpace(s.ShortName)
                && (s.Uuid is { Length: > 0 } || s.ChangedSince is not null))
            .ToList();

        if (unsupported.Count > 0)
        {
            logger.LogWarning(
                "GetTaxonomySet BODID {BodId} carries {Count} selector(s) using UUID or ChangedSince, "
                + "which this engine does not yet support; they were ignored.",
                parsed.BodId, unsupported.Count);
        }

        if (wanted.Count > 0)
        {
            sets = sets.Where(s => wanted.Contains(s.Name)).ToList();
        }

        // The BOD carries one set. Several matching sets is a real possibility
        // once RDL holds more than one namespace, and answering with only the
        // first would quietly drop the rest, so it is surfaced rather than
        // hidden. Extending the response to several sets is schema-legal
        // (TaxonomySets is unbounded) and is the obvious next step.
        if (sets.Count > 1)
        {
            logger.LogWarning(
                "GetTaxonomySet BODID {BodId} matched {Count} sets ({Names}); answering with the first only.",
                parsed.BodId, sets.Count, string.Join(", ", sets.Select(s => s.Name)));
        }

        var chosen = sets.FirstOrDefault();
        var setName = chosen?.Name ?? wanted.FirstOrDefault() ?? _options.DefaultSetName;

        var payload = chosen is null
            ? []
            : ToCcomClasses(chosen.Classes);

        logger.LogInformation(
            "Answering GetTaxonomySet BODID {BodId} with set '{Set}' carrying {Count} class(es).",
            parsed.BodId, setName, payload.Count);

        return TaxonomySetBods.ShowTaxonomySet(
            setShortName: setName,
            classes: payload,
            senderLogicalId: _options.SenderLogicalId,
            correlationId: $"rdl-{Guid.NewGuid():N}",
            originalBodId: string.IsNullOrWhiteSpace(parsed.BodId) ? "unknown" : parsed.BodId,
            version: _options.Version,
            originalCreationDateTime: parsed.CreationDateTime);
    }

    /// <summary>
    /// Maps RDL rows onto the wire model.
    ///
    /// The parent link is translated from the internal class id to the parent's
    /// code, because the code is the only identifier that travels. A parent
    /// outside this namespace has no code in this set, so the child is emitted
    /// as a root — see the response builder, which does the same for any parent
    /// it cannot resolve.
    /// </summary>
    private static List<CcomRdlClass> ToCcomClasses(IReadOnlyList<RdlClassDto> classes)
    {
        var codeById = classes.ToDictionary(c => c.ClassId, c => c.Code);

        return [.. classes.Select(c => new CcomRdlClass
        {
            Code = c.Code,
            Name = c.Name,
            Description = c.Description,
            ParentCode = c.ParentClassId is { } parentId
                && codeById.TryGetValue(parentId, out var parentCode)
                    ? parentCode
                    : null
        })];
    }
}
