using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Oiie.Ccom;
using Oiie.Ccom.Extensions;
using RdlEngine.Application;
using RdlEngine.Infrastructure.Rdl;
using Xunit;

namespace Oiie.Sandbox.Tests;

/// <summary>
/// The RDL engine's request-to-response mapping.
///
/// These exercise the decision the engine actually makes -- which classes go
/// into which set, and what a selector does -- against a stub library, so the
/// behaviour is pinned without needing RdlProvider or a broker running.
/// </summary>
public class TaxonomySetResponderTests
{
    /// <summary>
    /// Two namespaces, so set selection is actually exercised. Namespace 1 is
    /// the seeded sandbox slice; namespace 2 stands in for a second library
    /// that must not leak into the first's answer.
    /// </summary>
    private static readonly RdlClassDto[] Library =
    [
        new(1701, 1, 1, "rdl:Equipment", "Equipment", null, null),
        new(1702, 1, 1, "rdl:Instrument", "Instrument", null, 1701),
        new(1703, 1, 1, "rdl:LightingUnit", "Lighting Unit", "A street light.", 1701),
        new(2701, 1, 2, "other:Widget", "Widget", null, null)
    ];

    private sealed class StubRdlClient(IReadOnlyList<RdlClassDto> classes) : IRdlClient
    {
        public Task<IReadOnlyList<RdlClassDto>> GetClassesAsync(int? namespaceId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RdlClassDto>>(
                namespaceId is { } ns ? [.. classes.Where(c => c.NamespaceId == ns)] : classes);
    }

    private static TaxonomySetResponder Responder(IReadOnlyList<RdlClassDto>? classes = null)
    {
        var options = new RdlEngineOptions
        {
            TaxonomySetNames = { [1] = "ACME-RDL", [2] = "OTHER-RDL" },
            Version = "1.0"
        };

        return new TaxonomySetResponder(
            new StubRdlClient(classes ?? Library),
            Options.Create(options),
            NullLogger<TaxonomySetResponder>.Instance);
    }

    private static XDocument Request(params TaxonomySetSelector[] selectors)
        => TaxonomySetBods.GetTaxonomySet(selectors, "ENG", "bod-req-1");

    [Fact]
    public async Task Answers_a_request_naming_a_set()
    {
        var response = await Responder().RespondAsync(
            Request(new TaxonomySetSelector { ShortName = "ACME-RDL" }), default);

        Assert.NotNull(response);

        var classes = TaxonomySetBods.ParseShowTaxonomySet(response);
        Assert.Equal(3, classes.Count);
        Assert.All(classes, c => Assert.StartsWith("rdl:", c.Code));
    }

    /// <summary>
    /// The hierarchy has to survive the id-to-code translation: RDL stores the
    /// parent as an internal class id, but only the code travels.
    /// </summary>
    [Fact]
    public async Task Translates_the_parent_class_id_into_the_parent_code()
    {
        var response = await Responder().RespondAsync(
            Request(new TaxonomySetSelector { ShortName = "ACME-RDL" }), default);

        var classes = TaxonomySetBods.ParseShowTaxonomySet(response!);

        Assert.Equal("rdl:Equipment", classes.Single(c => c.Code == "rdl:LightingUnit").ParentCode);
        Assert.Null(classes.Single(c => c.Code == "rdl:Equipment").ParentCode);
    }

    /// <summary>
    /// A namespace is the set boundary, so another namespace's classes must not
    /// appear in this set's answer.
    /// </summary>
    [Fact]
    public async Task Does_not_leak_classes_from_another_namespace()
    {
        var response = await Responder().RespondAsync(
            Request(new TaxonomySetSelector { ShortName = "ACME-RDL" }), default);

        var classes = TaxonomySetBods.ParseShowTaxonomySet(response!);

        Assert.DoesNotContain(classes, c => c.Code == "other:Widget");
    }

    /// <summary>
    /// The BODID is the correlation, so the response must carry the request's
    /// own id rather than a fresh one.
    /// </summary>
    [Fact]
    public async Task Echoes_the_requests_bod_id()
    {
        var response = await Responder().RespondAsync(
            Request(new TaxonomySetSelector { ShortName = "ACME-RDL" }), default);

        var echoed = response!
            .Descendants(XName.Get("OriginalApplicationArea", Namespaces.Oagis))
            .Elements(XName.Get("BODID", Namespaces.Oagis))
            .Single();

        Assert.Equal("bod-req-1", echoed.Value);
    }

    /// <summary>
    /// Asking for a set the library does not hold is a question with the answer
    /// "nothing", not a fault. A consumer validating its mapping needs to tell
    /// that apart from the engine being broken.
    /// </summary>
    [Fact]
    public async Task Unknown_set_returns_an_empty_response_rather_than_faulting()
    {
        var response = await Responder().RespondAsync(
            Request(new TaxonomySetSelector { ShortName = "NO-SUCH-SET" }), default);

        Assert.NotNull(response);
        Assert.Empty(TaxonomySetBods.ParseShowTaxonomySet(response));
    }

    /// <summary>
    /// A document that is not a GetTaxonomySet returns null so the listener can
    /// leave someone else's message alone rather than consuming it.
    /// </summary>
    [Fact]
    public async Task Foreign_document_is_not_answered()
    {
        var foreign = new XDocument(new XElement("SomethingElse"));

        Assert.Null(await Responder().RespondAsync(foreign, default));
    }

    /// <summary>
    /// An empty selector means "everything you hold". With more than one set
    /// available the engine answers with one, deterministically ordered, rather
    /// than whichever the library happened to return first.
    /// </summary>
    [Fact]
    public async Task Empty_selector_answers_with_a_deterministic_set()
    {
        var response = await Responder().RespondAsync(Request(), default);

        var classes = TaxonomySetBods.ParseShowTaxonomySet(response!);

        Assert.Equal(3, classes.Count);
        Assert.Contains(classes, c => c.Code == "rdl:LightingUnit");
    }

    /// <summary>
    /// A namespace with no configured name is still answered. Dropping it would
    /// hide classes the library plainly holds, and the consumer could not tell
    /// an unconfigured namespace from an empty one.
    /// </summary>
    [Fact]
    public async Task Namespace_with_no_configured_name_is_still_answered()
    {
        var options = new RdlEngineOptions { DefaultSetName = "ACME-RDL" };

        var responder = new TaxonomySetResponder(
            new StubRdlClient([new(3701, 1, 9, "rdl:Pump", "Pump", null, null)]),
            Options.Create(options),
            NullLogger<TaxonomySetResponder>.Instance);

        var response = await responder.RespondAsync(
            Request(new TaxonomySetSelector { ShortName = "ACME-RDL-9" }), default);

        var classes = TaxonomySetBods.ParseShowTaxonomySet(response!);
        Assert.Equal("rdl:Pump", Assert.Single(classes).Code);
    }
}
