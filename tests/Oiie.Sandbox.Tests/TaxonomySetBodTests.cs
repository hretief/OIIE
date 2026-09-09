using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using NodaTime;
using Oiie.Ccom;
using Oiie.Ccom.Extensions;
using Xunit;

namespace Oiie.Sandbox.Tests;

/// <summary>
/// Validates GetTaxonomySet output against the reconstructed schema.
///
/// The schema is the whole point of these tests: the BOD is a reconstruction of
/// something MIMOSA named but never published, so the only thing keeping the
/// builder honest is that its output still validates against the corrected XSD.
/// Validation resolves CCOM and OAGIS from local disk — no network fetches.
/// </summary>
public class TaxonomySetBodTests
{
    private const string ExtNs = "http://www.openoandm.org/sandbox/extensions/1.0";

    private static XmlSchemaSet Schemas()
    {
        var sandbox = Path.Combine(RepoRoot(), "schemas", "sandbox");

        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.Add(ExtNs, Path.Combine(sandbox, "GetTaxonomySet.xsd"));
        schemas.Add(ExtNs, Path.Combine(sandbox, "ShowTaxonomySet.xsd"));
        schemas.Compile();
        return schemas;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenOM.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static List<string> Validate(XDocument document)
    {
        var errors = new List<string>();
        document.Validate(Schemas(), (_, e) => errors.Add(e.Message));
        return errors;
    }

    [Fact]
    public void Empty_selector_list_requests_every_set_and_validates()
    {
        var document = TaxonomySetBods.GetTaxonomySet([], "ENG", "bod-1");

        Assert.Empty(Validate(document));
    }

    [Fact]
    public void Selector_by_short_name_and_version_validates()
    {
        var document = TaxonomySetBods.GetTaxonomySet(
            [new TaxonomySetSelector { ShortName = "MIMOSA-RDL", Version = "1.0" }],
            "REG-LOCATION",
            "bod-2");

        Assert.Empty(Validate(document));
    }

    [Fact]
    public void Incremental_selector_validates()
    {
        var document = TaxonomySetBods.GetTaxonomySet(
            [
                new TaxonomySetSelector
                {
                    Uuid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
                    ChangedSince = Instant.FromUtc(2024, 5, 1, 0, 0)
                }
            ],
            "MMS",
            "bod-3");

        Assert.Empty(Validate(document));
    }

    /// <summary>
    /// The root must not sit in the CCOM namespace. A future official
    /// GetTaxonomySet would be indistinguishable from ours if it did, which is
    /// the whole reason for the extension namespace.
    /// </summary>
    [Fact]
    public void Root_is_in_the_extension_namespace_not_ccom()
    {
        var document = TaxonomySetBods.GetTaxonomySet([], "ENG", "bod-4");

        Assert.Equal(ExtNs, document.Root!.Name.NamespaceName);
        Assert.NotEqual(Namespaces.Ccom, document.Root.Name.NamespaceName);
    }

    [Fact]
    public void Correlation_id_travels_as_bodid()
    {
        var document = TaxonomySetBods.GetTaxonomySet([], "ENG", "bod-5");

        var bodId = document.Descendants(XName.Get("BODID", Namespaces.Oagis)).Single();
        Assert.Equal("bod-5", bodId.Value);
    }

    /// <summary>
    /// Proves the schema is not lax: the verb must precede the selector, so a
    /// document with them reversed has to fail.
    /// </summary>
    [Fact]
    public void Selector_placed_before_the_verb_fails_validation()
    {
        var document = TaxonomySetBods.GetTaxonomySet(
            [new TaxonomySetSelector { ShortName = "MIMOSA-RDL" }], "ENG", "bod-6");

        var dataArea = document.Root!.Element(XName.Get("DataArea", ExtNs))!;
        var verb = dataArea.Element(XName.Get("Get", Namespaces.Oagis))!;
        verb.Remove();
        dataArea.Add(verb);

        Assert.NotEmpty(Validate(document));
    }

    [Fact]
    public void Missing_release_id_fails_validation()
    {
        var document = TaxonomySetBods.GetTaxonomySet([], "ENG", "bod-7");
        document.Root!.Attribute("releaseID")!.Remove();

        Assert.NotEmpty(Validate(document));
    }

    // --- ShowTaxonomySet ---------------------------------------------------

    /// <summary>
    /// The sandbox slice as REG-LOCATION seeds it: rdl:LightingUnit and
    /// rdl:Instrument both specialise rdl:Equipment, which is itself a root.
    /// </summary>
    private static readonly RdlClass[] SandboxClasses =
    [
        new() { Code = "rdl:Equipment", Name = "Equipment" },
        new() { Code = "rdl:Instrument", Name = "Instrument", ParentCode = "rdl:Equipment" },
        new() { Code = "rdl:LightingUnit", Name = "Lighting Unit", ParentCode = "rdl:Equipment" }
    ];

    private static XDocument SandboxResponse() =>
        TaxonomySetBods.ShowTaxonomySet(
            "ACME-RDL", SandboxClasses, "RDL", "bod-100", "bod-1", version: "1.0");

    [Fact]
    public void Show_response_validates()
    {
        Assert.Empty(Validate(SandboxResponse()));
    }

    /// <summary>
    /// A set with no classes is a legitimate answer — a provider that holds
    /// nothing must still be able to say so rather than fault.
    /// </summary>
    [Fact]
    public void Show_response_with_no_classes_validates()
    {
        var document = TaxonomySetBods.ShowTaxonomySet(
            "ACME-RDL", [], "RDL", "bod-101", "bod-2");

        Assert.Empty(Validate(document));
    }

    /// <summary>
    /// The BODID echo is the only correlation between request and response, so
    /// it has to survive the round trip intact.
    /// </summary>
    [Fact]
    public void Show_response_echoes_the_original_bod_id()
    {
        var original = SandboxResponse()
            .Descendants(XName.Get("OriginalApplicationArea", Namespaces.Oagis))
            .Elements(XName.Get("BODID", Namespaces.Oagis))
            .Single();

        Assert.Equal("bod-1", original.Value);
    }

    /// <summary>
    /// From and To are typed against the abstract ccom:BaseType, so every one
    /// must name a concrete derivation. Without xsi:type the document cannot
    /// validate at all — this asserts the attribute is present rather than
    /// stripped as a serialiser artefact.
    /// </summary>
    [Fact]
    public void Every_base_type_element_carries_xsi_type()
    {
        var ccom = XNamespace.Get(Namespaces.Ccom);
        var xsiType = XName.Get("type", Namespaces.XmlSchemaInstance);

        var baseTypes = SandboxResponse()
            .Descendants(ccom + "Connection")
            .Elements()
            .Where(e => e.Name == ccom + "From" || e.Name == ccom + "To")
            .ToList();

        Assert.NotEmpty(baseTypes);
        Assert.All(baseTypes, e => Assert.NotNull(e.Attribute(xsiType)));
    }

    /// <summary>
    /// A root has no parent to pair with, so its connection carries From alone.
    /// This is the CCOM-sanctioned encoding; emitting a To that points at
    /// nothing, or dropping the root entirely, would both be wrong.
    /// </summary>
    [Fact]
    public void Root_class_is_carried_as_a_connection_with_no_to()
    {
        var ccom = XNamespace.Get(Namespaces.Ccom);

        var rootConnections = SandboxResponse()
            .Descendants(ccom + "Connection")
            .Where(c => c.Element(ccom + "To") is null)
            .ToList();

        var root = Assert.Single(rootConnections);
        Assert.Equal(
            "rdl:Equipment",
            root.Element(ccom + "From")!.Element(ccom + "ShortName")!.Value);
    }

    /// <summary>
    /// The round trip is what consumers depend on: the hierarchy has to come
    /// back out, not just a flat set of codes.
    /// </summary>
    [Fact]
    public void Round_trip_preserves_the_class_hierarchy()
    {
        var parsed = TaxonomySetBods.ParseShowTaxonomySet(SandboxResponse());

        Assert.Equal(3, parsed.Count);

        var lightingUnit = parsed.Single(c => c.Code == "rdl:LightingUnit");
        Assert.Equal("rdl:Equipment", lightingUnit.ParentCode);
        Assert.Equal("Lighting Unit", lightingUnit.Name);

        Assert.Null(parsed.Single(c => c.Code == "rdl:Equipment").ParentCode);
        Assert.Equal("rdl:Equipment", parsed.Single(c => c.Code == "rdl:Instrument").ParentCode);
    }

    /// <summary>
    /// Consumers fail closed. Handing the parser something that is not a
    /// ShowTaxonomySet must yield no classes rather than an exception, so the
    /// caller's "is my mapping in the list?" check simply answers no.
    /// </summary>
    [Fact]
    public void Parsing_a_foreign_document_yields_no_classes()
    {
        var request = TaxonomySetBods.GetTaxonomySet([], "ENG", "bod-8");

        Assert.Empty(TaxonomySetBods.ParseShowTaxonomySet(request));
    }

    /// <summary>
    /// A class naming a parent the provider did not include still has to
    /// appear. Dropping it would hide a class that is plainly held, which is
    /// the opposite of what a validating consumer needs.
    /// </summary>
    [Fact]
    public void Class_with_an_absent_parent_is_carried_as_a_root()
    {
        var document = TaxonomySetBods.ShowTaxonomySet(
            "ACME-RDL",
            [new RdlClass { Code = "rdl:LightingUnit", ParentCode = "rdl:Equipment" }],
            "RDL", "bod-102", "bod-3");

        Assert.Empty(Validate(document));

        var parsed = Assert.Single(TaxonomySetBods.ParseShowTaxonomySet(document));
        Assert.Equal("rdl:LightingUnit", parsed.Code);
        Assert.Null(parsed.ParentCode);
    }
}
