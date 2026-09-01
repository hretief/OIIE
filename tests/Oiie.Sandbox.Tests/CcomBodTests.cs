using NodaTime;
using Oiie.Ccom;
using Oiie.Ccom.Bods;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using System.Xml.Serialization;
using Oiie.Ccom.Xml;
using Xunit;
using CcomAttribute = Oiie.Ccom.Types.Attribute;

namespace SimHost.Tests;

public class CcomBodTests
{
    private static SyncSegments BuildSyncSegments()
    {
        var bod = new SyncSegments(ActionCodes.Add);
        bod.ApplicationArea.Sender = new Sender
        {
            LogicalID = "urn:oiie-sandbox:eng",
            ComponentID = "SimHost",
            ReferenceID = "Rev C — Unit 101 reroute"
        };
        bod.ApplicationArea.BODID = "corr-0001";
        bod.ApplicationArea.CreationDateTime = Instant.FromUtc(2026, 8, 1, 9, 14, 0);

        bod.With(new Segment
        {
            UUID = CcomUuid.FromKey("Test", "TIC-106"),
            IDInInfoSource = "TIC-106",
            InfoSource = new InfoSource
            {
                UUID = CcomUuid.ForInfoSource("ENG"),
                ShortName = "ENG"
            },
            ShortName = "TIC-106",
            FullName = "Top temperature control",
            Type = new SegmentType
            {
                UUID = CcomUuid.ForReferenceData("MIMOSA-RDL", "rdl:TemperatureIndicatingController"),
                IDInInfoSource = "rdl:TemperatureIndicatingController",
                InfoSource = new InfoSource
                {
                    UUID = CcomUuid.ForInfoSource("MIMOSA-RDL"),
                    ShortName = "MIMOSA-RDL"
                },
                ShortName = "Temperature indicating controller"
            },
            AttributeSetForEntity =
            [
                new AttributeSetForEntity
                {
                    UUID = CcomUuid.FromKey("Test", "set-for-entity"),
                    AttributeSet = new AttributeSet
                    {
                        UUID = CcomUuid.FromKey("Test", "set"),
                        ShortName = "Instrument",
                        Type = new AttributeSetType
                        {
                            UUID = CcomUuid.ForReferenceData("MIMOSA-RDL", "rdl:Instrument"),
                            IDInInfoSource = "rdl:Instrument",
                            InfoSource = new InfoSource
                            {
                                UUID = CcomUuid.ForInfoSource("MIMOSA-RDL"),
                                ShortName = "MIMOSA-RDL"
                            },
                            ShortName = "Instrument"
                        },
                        SetAttribute =
                        [
                            new CcomAttribute
                            {
                                UUID = CcomUuid.FromKey("Test", "range-max"),
                                ShortName = "Range maximum",
                                Type = new AttributeType
                                {
                                    UUID = CcomUuid.ForReferenceData(null, "rdl:RangeMaximum"),
                                    IDInInfoSource = "rdl:RangeMaximum"
                                },
                                ValueContent = new MeasureContent { Value = 250m, UnitOfMeasure = "degC" }
                            }
                        ]
                    }
                }
            ]
        });

        return bod;
    }

    [Fact]
    public void Root_element_takes_the_concrete_class_name_and_ccom_namespace()
    {
        var document = BuildSyncSegments().CreateDocument();

        Assert.Equal("SyncSegments", document.Root!.Name.LocalName);
        Assert.Equal(Namespaces.Ccom, document.Root.Name.NamespaceName);
    }

    [Fact]
    public void Data_area_uses_the_verb_name_and_plural_noun_wrapper()
    {
        var document = BuildSyncSegments().CreateDocument();

        Assert.NotNull(document.Root!.Child("DataArea/Sync"));
        Assert.NotNull(document.Root!.Child("DataArea/Segments/Segment"));
    }

    [Fact]
    public void Action_expression_points_at_the_documents_own_data_area()
    {
        var document = BuildSyncSegments().CreateDocument();
        var expression = document.Root!.Child("DataArea/Sync/ActionCriteria/ActionExpression");

        Assert.Equal("/SyncSegments/DataArea/Segments", expression.SafeValue());
        Assert.Equal("Add", expression.SafeAttributeValue("actionCode"));
        Assert.Equal("Xpath", expression.SafeAttributeValue("expressionLanguage"));
    }

    [Fact]
    public void Xsi_type_is_stripped_from_value_content()
    {
        var document = BuildSyncSegments().CreateDocument();

        // XmlSerializer emits xsi:type on the polymorphic ValueContent. CCOM rejects
        // it — the concrete child element is already the discriminator — so
        // CleanUpDocument removes it.
        var valueContent = document.Descendants()
            .Single(e => e.Name.LocalName == "ValueContent");

        Assert.DoesNotContain(valueContent.Attributes(),
            a => a.Name.NamespaceName == Namespaces.XmlSchemaInstance);

        // The concrete child element is the only discriminator left, so every
        // ValueContent subtype must expose exactly one property named for its key.
        Assert.Equal("250", valueContent.Child("Measure/Value").SafeValue());
        Assert.Equal("degC", valueContent.Child("Measure/UnitOfMeasure/ShortName").SafeValue());
    }

    [Fact]
    public void Envelope_parses_verb_noun_and_application_area()
    {
        var envelope = BodEnvelope.Parse(BuildSyncSegments().ToXmlString());

        Assert.Equal("SyncSegments", envelope.RootName);
        Assert.Equal("Sync", envelope.Verb);
        Assert.Equal("Segments", envelope.Noun);
        Assert.Equal("corr-0001", envelope.BodId);
        Assert.Equal("urn:oiie-sandbox:eng", envelope.SenderLogicalId);
        Assert.Equal("Rev C — Unit 101 reroute", envelope.SenderReferenceId);
        Assert.Equal("Add", envelope.ActionCode);
    }

    [Fact]
    public void Envelope_locates_nouns_by_following_the_action_expression()
    {
        var envelope = BodEnvelope.Parse(BuildSyncSegments().ToXmlString());

        Assert.Single(envelope.NounElements);
        Assert.Equal("Segment", envelope.NounElements[0].Name.LocalName);
    }

    [Fact]
    public void Round_trip_preserves_identity_class_and_measured_property()
    {
        var envelope = BodEnvelope.Parse(BuildSyncSegments().ToXmlString());
        var segment = envelope.NounsAs(e => new Segment(e)).Single();

        Assert.Equal("TIC-106", segment.IDInInfoSource);
        Assert.Equal("ENG", segment.InfoSource?.ShortName);
        Assert.Equal("rdl:TemperatureIndicatingController", segment.Type?.IDInInfoSource);

        var set = Assert.Single(segment.AttributeSetForEntity).AttributeSet;
        Assert.Equal("rdl:Instrument", set?.Type?.IDInInfoSource);

        var attribute = Assert.Single(set!.SetAttribute);
        var measure = Assert.IsType<MeasureContent>(attribute.ValueContent);
        Assert.Equal(250m, measure.Value);
        Assert.Equal("degC", measure.UnitOfMeasure);
    }

    [Fact]
    public void Attribute_with_null_text_is_not_serialised_as_an_empty_value_content()
    {
        var attribute = new CcomAttribute { ValueContent = new TextContent { Text = null } };

        // An empty <ValueContent /> is a schema error.
        Assert.False(attribute.ShouldSerializeValueContent());
    }

    /// <summary>
    /// A site names the iTwin a design belongs to. It carries its own identity and
    /// its type, and nothing else -- the registered content CCOM allows on a Site is
    /// deliberately not modelled, so a round trip has to preserve exactly this much.
    /// </summary>
    private static SyncSites BuildSyncSites()
    {
        var bod = new SyncSites(ActionCodes.Add);
        bod.ApplicationArea.Sender = new Sender
        {
            LogicalID = "urn:oiie-sandbox:eng",
            ComponentID = "SimHost"
        };
        bod.ApplicationArea.BODID = "corr-site-0001";
        bod.ApplicationArea.CreationDateTime = Instant.FromUtc(2026, 8, 1, 9, 14, 0);

        bod.With(new Site
        {
            UUID = Guid.Parse("ce40a55a-6954-4de3-85c7-f796c3e423d9"),
            IDInInfoSource = "ce40a55a-6954-4de3-85c7-f796c3e423d9",
            InfoSource = new InfoSource
            {
                UUID = CcomUuid.ForInfoSource("ENG"),
                ShortName = "ENG"
            },
            ShortName = "US Route 202",
            FullName = "US Route 202 — HWYUSR202",
            Type = new SegmentType
            {
                UUID = Guid.Parse("f7e8d9c0-1111-2222-3333-444455556666"),
                IDInInfoSource = "Highway",
                InfoSource = new InfoSource
                {
                    UUID = CcomUuid.ForInfoSource("ENG"),
                    ShortName = "ENG"
                },
                ShortName = "Highway",
                FullName = "Highway Project"
            }
        });

        return bod;
    }

    [Fact]
    public void SyncSites_round_trip_preserves_the_site_and_its_type()
    {
        var envelope = BodEnvelope.Parse(BuildSyncSites().ToXmlString());

        // Site is a Segment specialisation, so the noun element is named for the
        // concrete type rather than for the base it inherits from.
        Assert.Equal("Site", Assert.Single(envelope.NounElements).Name.LocalName);

        var site = envelope.NounsAs(e => new Site(e)).Single();

        Assert.Equal(Guid.Parse("ce40a55a-6954-4de3-85c7-f796c3e423d9"), site.UUID);
        Assert.Equal("US Route 202", site.ShortName);
        Assert.Equal("US Route 202 — HWYUSR202", site.FullName);
        Assert.Equal(Guid.Parse("f7e8d9c0-1111-2222-3333-444455556666"), site.Type?.UUID);
        Assert.Equal("Highway", site.Type?.ShortName);
        Assert.Equal("Highway Project", site.Type?.FullName);
    }

    [Fact]
    public void Distinct_bod_types_do_not_share_a_cached_serializer()
    {
        // A static serializer field on the open generic would be shared by every
        // concrete class closing over the same verb and noun, so the first one to
        // initialise would define the root element name for all of them.
        var segments = new SyncSegments(ActionCodes.Add).CreateDocument();
        var assets = new SyncAssets(ActionCodes.Add).CreateDocument();

        Assert.Equal("SyncSegments", segments.Root!.Name.LocalName);
        Assert.Equal("SyncAssets", assets.Root!.Name.LocalName);
    }

    [Fact]
    public void Every_value_content_subtype_declares_its_discriminator_element()
    {
        // Regression guard. A subtype whose serialised shape does not begin with an
        // element matching its dispatch key writes fine and then fails to parse —
        // which is exactly how MeasureContent broke.
        foreach (var (key, subtype) in new (string, Type)[]
                 {
                     ("Text", typeof(TextContent)),
                     ("Number", typeof(NumberContent)),
                     ("Measure", typeof(MeasureContent)),
                     ("Boolean", typeof(BooleanContent)),
                     ("UUID", typeof(UUIDContent)),
                     ("UTCDateTime", typeof(UTCDateTimeContent)),
                     ("EnumerationItem", typeof(EnumerationItemContent)),
                     ("URI", typeof(UriContent))
                 })
        {
            var serialised = subtype.GetProperties()
                .Where(p => !p.GetCustomAttributes(typeof(XmlIgnoreAttribute), true).Any())
                .Select(p => p.GetCustomAttributes(typeof(XmlElementAttribute), true)
                    .Cast<XmlElementAttribute>()
                    .Select(a => a.ElementName)
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? p.Name)
                .ToList();

            Assert.Contains(key, serialised);
        }
    }

    [Fact]
    public void Unknown_namespace_yields_not_validated_rather_than_valid()
    {
        var validator = new BodValidator();
        var result = validator.Validate(BuildSyncSegments().CreateDocument());

        Assert.Equal(BodValidationStatus.NotValidated, result.Status);
    }

    /// <summary>
    /// CCOM declares UUID with minOccurs="1" on Entity and on the nested
    /// reference-data types, so omitting it is a schema violation. The failure
    /// surfaces as "invalid child element 'ShortName' ... expected 'UUID'", which
    /// reads like an ordering fault and is easy to misdiagnose. This asserts the
    /// document a builder actually produces, against the real schema package.
    /// </summary>
    [Fact]
    public void Built_bod_satisfies_the_ccom_schema()
    {
        var schemaDirectory = FindSchemaDirectory();
        Assert.True(schemaDirectory is not null, "schemas/ccom was not found from the test output directory.");

        var validator = new BodValidator();
        validator.LoadDirectory(schemaDirectory!);

        var result = validator.Validate(BuildSyncSegments().CreateDocument());

        Assert.Null(result.Detail);
        Assert.Equal(BodValidationStatus.Valid, result.Status);
    }

    private static string? FindSchemaDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "schemas", "ccom");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
