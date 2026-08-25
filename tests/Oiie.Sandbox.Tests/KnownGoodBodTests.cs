using System.Xml.Linq;
using Oiie.Ccom;
using Oiie.Ccom.Bods;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using Oiie.Ccom.Xml;
using Xunit;
using CcomAttribute = Oiie.Ccom.Types.Attribute;

namespace SimHost.Tests;

/// <summary>
/// Conformance against a published SyncSegments document rather than against our own
/// output. Round-tripping our own serialiser proves only self-consistency; this is
/// the only test in the suite that can catch the framework being confidently wrong
/// about the wire format.
/// </summary>
public class KnownGoodBodTests
{
    private static XDocument Fixture() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SyncSegmentsWithAttributes.xml"));

    private static Segment ParseSegment()
    {
        var envelope = BodEnvelope.Parse(Fixture());
        return envelope.NounsAs(e => new Segment(e)).Single();
    }

    [Fact]
    public void Envelope_reads_a_published_document()
    {
        var envelope = BodEnvelope.Parse(Fixture());

        Assert.Equal("SyncSegments", envelope.RootName);
        Assert.Equal("Sync", envelope.Verb);
        Assert.Equal("Segments", envelope.Noun);
        Assert.Equal("Replace", envelope.ActionCode);
        Assert.Equal("62a1a6dd-a0f8-4609-970c-a2cadd75c740", envelope.BodId);
        Assert.Equal("SourceSystem", envelope.SenderLogicalId);
        Assert.Single(envelope.NounElements);
    }

    [Fact]
    public void Segment_identity_and_info_source_are_read()
    {
        var segment = ParseSegment();

        Assert.Equal(Guid.Parse("499cf321-69b2-4e69-b3a0-e696c3ca8c3b"), segment.UUID);
        Assert.Equal("5680", segment.IDInInfoSource);
        Assert.Equal("SourceSystemName", segment.InfoSource?.ShortName);
        Assert.Equal("http://SourceSystem/Segments/4567", segment.InfoSource?.URL);
    }

    [Fact]
    public void All_attributes_are_read_with_their_types()
    {
        var segment = ParseSegment();

        // Commented-out attributes in the source must not be picked up.
        Assert.Equal(11, segment.Attribute.Count);
        Assert.All(segment.Attribute, a => Assert.NotNull(a.Type));

        // Every published value shape must dispatch to something. A null here means
        // ValueContent.Parse has no factory for a variant that appears in the wild.
        Assert.All(segment.Attribute, a => Assert.NotNull(a.ValueContent));
    }

    /// <summary>
    /// The full set of value shapes the published document exercises. Keyed on the
    /// attribute label purely to locate the row; what is being asserted is that the
    /// ValueContent discriminator dispatches to the right type.
    /// </summary>
    [Theory]
    [InlineData("Duration", typeof(MeasureContent))]
    [InlineData("Percentage", typeof(PercentageContent))]
    [InlineData("Probability", typeof(ProbabilityContent))]
    [InlineData("Uri", typeof(UriContent))]
    [InlineData("Utc data time", typeof(UTCDateTimeContent))]
    [InlineData("unique id", typeof(UUIDContent))]
    [InlineData("Coordinates", typeof(CoordinateContent))]
    [InlineData("IsBoolean", typeof(BooleanContent))]
    [InlineData("Atex Category", typeof(TextContent))]
    [InlineData("Unit", typeof(EnumerationItemContent))]
    [InlineData("Total Cost", typeof(NumberContent))]
    public void Value_content_dispatch_matches_the_published_shapes(string attributeLabel, Type expected)
    {
        var segment = ParseSegment();
        var attribute = segment.Attribute.Single(a => a.Type?.ShortName == attributeLabel);

        Assert.IsType(expected, attribute.ValueContent);
    }

    [Fact]
    public void Measure_carries_value_and_named_unit()
    {
        var segment = ParseSegment();
        var duration = segment.Attribute.First(a => a.Type?.ShortName == "Duration");

        var measure = Assert.IsType<MeasureContent>(duration.ValueContent);
        Assert.Equal(1.0m, measure.Value);
        Assert.Equal("hr", measure.UnitOfMeasure);
        Assert.Equal(
            Guid.Parse("93770CEC-F196-7D47-BB4D-0BB6B8381CD5"),
            measure.Measure?.UnitOfMeasure?.UUID);
    }

    [Fact]
    public void Coordinate_carries_three_axes()
    {
        var segment = ParseSegment();
        var attribute = segment.Attribute.First(a => a.ValueContent is CoordinateContent);

        var coordinate = Assert.IsType<CoordinateContent>(attribute.ValueContent).Coordinate;
        Assert.Equal(5.53m, coordinate?.X);
        Assert.Equal(6.37m, coordinate?.Y);
        Assert.Equal(36.5m, coordinate?.Z);
    }

    [Fact]
    public void Enumeration_item_carries_identity_not_just_a_label()
    {
        var segment = ParseSegment();
        var attribute = segment.Attribute.First(a => a.ValueContent is EnumerationItemContent);

        var item = Assert.IsType<EnumerationItemContent>(attribute.ValueContent).EnumerationItem;
        Assert.Equal("Enum1", item?.ShortName);
        Assert.Equal(Guid.Parse("65556630-6c50-5958-b56b-65564a70694c"), item?.UUID);
    }

    [Fact]
    public void Uri_value_preserves_its_resource_name_attribute()
    {
        var segment = ParseSegment();
        var attribute = segment.Attribute.First(a => a.ValueContent is UriContent);

        var uri = Assert.IsType<UriContent>(attribute.ValueContent).URI;
        Assert.Equal("http://www.fakeurl.com", uri?.Value);
        Assert.Equal("foo", uri?.ResourceName);
    }

    [Fact]
    public void Our_writer_places_entity_members_before_segment_members()
    {
        // The published document orders Segment children as
        //   UUID, IDInInfoSource, InfoSource, Attribute*, ShortName, FullName, ...
        // so Attribute genuinely belongs to the Entity base and base-before-derived
        // emission is correct rather than a hazard.
        var published = SegmentChildNames(Fixture());
        var ours = SegmentChildNames(BuildEquivalent().CreateDocument());

        Assert.True(
            published.IndexOf("Attribute") < published.IndexOf("ShortName"),
            "Published document puts Attribute before ShortName.");

        Assert.True(
            ours.IndexOf("Attribute") < ours.IndexOf("ShortName"),
            "Our writer must place Attribute before ShortName to match.");
    }

    [Fact]
    public void Our_writer_matches_the_published_envelope_conventions()
    {
        var document = BuildEquivalent().CreateDocument();
        var root = document.Root!;

        Assert.Equal(Namespaces.Ccom, root.Name.NamespaceName);
        Assert.Equal("1.0", root.Attribute("releaseID")?.Value);
        Assert.Null(root.Attribute("versionID"));

        Assert.Equal(Namespaces.Oagis, root.Child("ApplicationArea")!.Name.NamespaceName);
        Assert.Equal(Namespaces.Ccom, root.Child("DataArea")!.Name.NamespaceName);
        Assert.Equal(Namespaces.Oagis, root.Child("DataArea/Sync")!.Name.NamespaceName);
        Assert.Equal(Namespaces.Ccom, root.Child("DataArea/Segments")!.Name.NamespaceName);

        var expression = root.Child("DataArea/Sync/ActionCriteria/ActionExpression");
        Assert.Equal("Xpath", expression.SafeAttributeValue("expressionLanguage"));
        Assert.Equal("/SyncSegments/DataArea/Segments", expression.SafeValue());
    }

    [Fact]
    public void Our_writer_reproduces_the_measure_shape()
    {
        var document = BuildEquivalent().CreateDocument();
        var valueContent = document.Descendants()
            .First(e => e.Name.LocalName == "ValueContent");

        Assert.Equal("1.0", valueContent.Child("Measure/Value").SafeValue());
        Assert.Equal("hr", valueContent.Child("Measure/UnitOfMeasure/ShortName").SafeValue());
    }

    private static SyncSegments BuildEquivalent()
    {
        var bod = new SyncSegments(ActionCodes.Replace);
        bod.ApplicationArea.Sender = new Sender
        {
            LogicalID = "SourceSystem",
            ComponentID = "IModelHub",
            TaskID = "Segment Creation"
        };
        bod.ApplicationArea.BODID = "62a1a6dd-a0f8-4609-970c-a2cadd75c740";

        bod.With(new Segment
        {
            UUID = Guid.Parse("499cf321-69b2-4e69-b3a0-e696c3ca8c3b"),
            IDInInfoSource = "5680",
            InfoSource = new InfoSource
            {
                UUID = Guid.Parse("7384187c-59a2-44d7-9302-14b31ae6a10e"),
                ShortName = "SourceSystemName",
                URL = "http://SourceSystem/Segments/4567"
            },
            ShortName = "Zone 1",
            Attribute =
            [
                new CcomAttribute
                {
                    UUID = Guid.Parse("683BEFEC-24BB-F94F-BBFB-AE393A81E538"),
                    Type = new AttributeType { ShortName = "Duration" },
                    ValueContent = new MeasureContent { Value = 1.0m, UnitOfMeasure = "hr" }
                }
            ]
        });

        return bod;
    }

    private static List<string> SegmentChildNames(XDocument document) =>
        document.Descendants()
            .First(e => e.Name.LocalName == "Segment")
            .Elements()
            .Select(e => e.Name.LocalName)
            .ToList();
}
