using System.Xml.Linq;
using CirProvider.Domain;
using CirProvider.Domain.Bod;
using CirProvider.Infrastructure.Bod;
using Microsoft.Extensions.Logging;

namespace CirProvider.Application;

public interface IBodDispatcher
{
    /// <summary>Parses a BOD document, executes it, and returns the response BOD, or null when none is defined.</summary>
    Task<XDocument?> DispatchAsync(XDocument document, CancellationToken ct = default);
}

/// <summary>
/// Annex A transport adapter. Every BOD maps onto an <see cref="ICirStore"/>
/// operation that already exists and is tested, so this layer only translates
/// and decides whether a response is due.
/// </summary>
public sealed class BodDispatcher(ICirStore store, ILogger<BodDispatcher> logger) : IBodDispatcher
{
    private static readonly ApplicationArea Self = new() { SenderLogicalId = "ws-CIR Provider" };

    public async Task<XDocument?> DispatchAsync(XDocument document, CancellationToken ct = default)
    {
        // Resolved before anything else: if the document names a BOD we know, a
        // failure anywhere after this point can still be reported back to the
        // sender as a fault on that BOD's response. Consuming a request and
        // discarding it silently is the worst available behaviour, because the
        // sender cannot tell it apart from a provider that is asleep.
        var definition = document.Root is { } root ? BodCatalogue.Find(root.Name.LocalName) : null;

        BodResult result;
        BodRequest? request = null;

        try
        {
            request = ParseEnvelope(document);
            result = await ExecuteAsync(request, ct);
        }
        catch (Exception ex) when (definition is not null && definition.ExpectsResponse)
        {
            logger.LogError(ex, "{Bod} could not be processed; replying with a fault.", definition.BodName);

            result = new BodResult
            {
                ResponseBodName = definition.ResponseBod,
                OriginalApplicationArea = request?.ApplicationArea ?? TryParseApplicationArea(document),
                Faults = [new CirFault(FaultCodeFor(definition, ex), Describe(ex))]
            };
        }

        if (result.ResponseBodName is null)
        {
            logger.LogInformation("{Bod} produced no response BOD.",
                request?.Definition.BodName ?? definition?.BodName ?? "unknown");
            return null;
        }

        return BodXmlWriter.Write(result, Self with
        {
            CreationDateTime = DateTimeOffset.UtcNow,
            BodId = Guid.NewGuid().ToString()
        });
    }

    /// <summary>
    /// Picks a fault code for a failure the specification has no code for — a
    /// malformed document, a missing element, an unexpected server error.
    ///
    /// The fault MUST be one the response BOD's schema declares, or the reply is
    /// invalid. The first declared code is used, with the real cause in the
    /// Description. Recorded as an interpretation in the conformance statement.
    /// </summary>
    private static CirFaultCode FaultCodeFor(BodDefinition definition, Exception ex) =>
        ex is CirFaultException fault && definition.FaultOrder.Contains(fault.Faults[0].Code)
            ? fault.Faults[0].Code
            : definition.FaultOrder.Count > 0
                ? definition.FaultOrder[0]
                : CirFaultCode.CreateRegistryFault;

    private static string Describe(Exception ex) => ex switch
    {
        CirFaultException fault => fault.Message,
        System.Xml.XmlException xml => $"The BOD could not be read: {xml.Message}",
        NotSupportedException nse => nse.Message,
        _ => $"The request could not be processed: {ex.Message}"
    };

    /// <summary>Best-effort, so a correlation can still be echoed on a malformed document.</summary>
    private static ApplicationArea? TryParseApplicationArea(XDocument document)
    {
        try { return document.Root is { } root ? ParseApplicationArea(root) : null; }
        catch { return null; }
    }

    // -----------------------------------------------------------------------

    private static BodRequest ParseEnvelope(XDocument document)
    {
        var root = document.Root
            ?? throw new System.Xml.XmlException("The BOD document is empty.");

        var definition = BodCatalogue.Find(root.Name.LocalName)
            ?? throw new NotSupportedException($"'{root.Name.LocalName}' is not a ws-CIR BOD.");

        var dataArea = root.Element(CirNs.Cir + "DataArea")
            ?? root.Elements().FirstOrDefault(e => e.Name.LocalName == "DataArea")
            ?? throw new System.Xml.XmlException("The BOD has no DataArea.");

        // The noun is the DataArea child that is not the verb element.
        var noun = dataArea.Elements()
            .FirstOrDefault(e => e.Name.LocalName == definition.Noun)
            ?? throw new System.Xml.XmlException($"The DataArea has no '{definition.Noun}' noun.");

        var accepted = BodVerbElements.Accepted(definition.Verb);
        var verbElement = dataArea.Elements()
            .FirstOrDefault(e => accepted.Contains(e.Name.LocalName));

        // Annex A: a server MUST support every acknowledgeCode and responseCode.
        // The recordSet* and maxItems attributes GetType and ShowType allow are
        // deliberately ignored — Annex A excludes result paging because of the
        // nested result structure. ActionCriteria on Cancel and Change verbs is
        // likewise ignored, as the noun is processed by the invoked service.
        var code = verbElement?.Attribute("acknowledgeCode")?.Value
                   ?? verbElement?.Attribute("responseCode")?.Value;

        return new BodRequest
        {
            Definition = definition,
            ApplicationArea = ParseApplicationArea(root),
            Confirmation = Enum.TryParse<ConfirmationCode>(code, ignoreCase: true, out var parsed)
                ? parsed
                : ConfirmationCode.Always,
            Noun = noun
        };
    }

    private static ApplicationArea ParseApplicationArea(XElement root)
    {
        var area = root.Elements().FirstOrDefault(e => e.Name.LocalName == "ApplicationArea");
        if (area is null) return new ApplicationArea();

        var sender = area.Elements().FirstOrDefault(e => e.Name.LocalName == "Sender");

        return new ApplicationArea
        {
            SenderLogicalId = sender?.Elements().FirstOrDefault(e => e.Name.LocalName == "LogicalID")?.Value,
            CreationDateTime = DateTimeOffset.TryParse(
                area.Elements().FirstOrDefault(e => e.Name.LocalName == "CreationDateTime")?.Value,
                out var created) ? created : DateTimeOffset.UtcNow,
            BodId = area.Elements().FirstOrDefault(e => e.Name.LocalName == "BODID")?.Value
                    ?? Guid.NewGuid().ToString()
        };
    }

    private async Task<BodResult> ExecuteAsync(BodRequest request, CancellationToken ct)
    {
        var definition = request.Definition;

        // Get verbs answer with data, so faults surface as transport errors
        // rather than as an empty Show BOD.
        if (definition.Verb == BodVerb.Get)
        {
            var registries = await ExecuteQueryAsync(definition.BodName, request.Noun, ct);
            return new BodResult
            {
                ResponseBodName = definition.ResponseBod,
                OriginalApplicationArea = request.ApplicationArea,
                Registries = registries
            };
        }

        // Process, Change and Cancel report through the noun's fault list.
        var faults = new List<CirFault>();
        try
        {
            await ExecuteCommandAsync(definition.BodName, request.Noun, ct);
        }
        catch (CirFaultException ex)
        {
            faults.AddRange(ex.Faults);
            logger.LogWarning("{Bod} faulted: {Detail}", definition.BodName, ex.Message);
        }

        return new BodResult
        {
            ResponseBodName = ShouldRespond(definition, request.Confirmation, faults.Count > 0)
                ? definition.ResponseBod
                : null,
            OriginalApplicationArea = request.ApplicationArea,
            Faults = faults
        };
    }

    /// <summary>
    /// OAGIS confirmation codes decide whether a response is produced at all.
    /// ResponseCodeContentType is a union with normalizedString, so an
    /// unrecognised value is legal and falls back to Always.
    /// </summary>
    private static bool ShouldRespond(BodDefinition definition, ConfirmationCode code, bool faulted) =>
        definition.ExpectsResponse && code switch
        {
            ConfirmationCode.Never => false,
            ConfirmationCode.OnError => faulted,
            _ => true
        };

    private Task<IReadOnlyList<Registry>> ExecuteQueryAsync(string bod, XElement noun, CancellationToken ct) => bod switch
    {
        "GetRegistry" => store.GetRegistryAsync(CirXmlReader.ReadFilters(noun), ct),

        "GetEquivalentEntries" => ExecuteGetEquivalentEntries(noun, ct),

        "GetEntriesByCIRID" => ExecuteGetEntriesByCirid(noun, ct),

        _ => throw new NotSupportedException($"'{bod}' is not a query BOD.")
    };

    private Task<IReadOnlyList<Registry>> ExecuteGetEquivalentEntries(XElement noun, CancellationToken ct)
    {
        var (ids, targets) = CirXmlReader.ReadGetEquivalentEntries(noun);
        return store.GetEquivalentEntriesAsync(ids, targets, ct);
    }

    private Task<IReadOnlyList<Registry>> ExecuteGetEntriesByCirid(XElement noun, CancellationToken ct)
    {
        var (cirid, targets) = CirXmlReader.ReadGetEntriesByCirid(noun);
        return store.GetEntriesByCiridAsync(cirid, targets, ct);
    }

    private Task ExecuteCommandAsync(string bod, XElement noun, CancellationToken ct) => bod switch
    {
        "ProcessRegistry" =>
            store.CreateRegistryAsync(CirXmlReader.ReadCreateRegistry(noun), ct),

        "ProcessEquivalentEntries" =>
            store.CreateEquivalentEntriesAsync(CirXmlReader.ReadCreateEquivalentEntries(noun), ct),

        "ChangeRegistry" =>
            store.UpdateRegistryAsync(CirXmlReader.ReadRegistries(noun), ct),

        "ChangeEntryCIRID" =>
            store.UpdateEntryCiridAsync(CirXmlReader.ReadUpdateEntryCirid(noun), ct),

        "CancelRegistry" =>
            store.DeleteRegistryAsync(
                noun.Element(CirNs.Cir + "RegistryID")?.Value
                ?? throw new System.Xml.XmlException("RegistryID is missing."), ct),

        "CancelCategory" =>
            store.DeleteCategoryAsync(CirXmlReader.ReadCategoryIdentifier(noun), ct),

        "CancelEntries" =>
            store.DeleteEntriesAsync(
                noun.Elements(CirNs.Cir + "EntryIdentifier").Select(CirXmlReader.ReadEntryIdentifier).ToList(), ct),

        "CancelProperties" =>
            store.DeletePropertiesAsync(
                noun.Elements(CirNs.Cir + "PropertyIdentifier").Select(CirXmlReader.ReadPropertyIdentifier).ToList(), ct),

        _ => throw new NotSupportedException($"'{bod}' is not a command BOD.")
    };
}
