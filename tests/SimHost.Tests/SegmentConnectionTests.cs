using NodaTime;
using Oiie.Ccom;
using Oiie.Ccom.Bods;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using Oiie.Ccom.Xml;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// Guards the relationship BOD.
///
/// CCOM has no envelope for a free-standing SegmentConnection — every BOD that
/// carries connections requires a SegmentMesh to hold them — so these tests fix
/// both the wrapping and the element ordering that the wrapping depends on.
/// </summary>
public class SegmentConnectionTests
{
    private static Segment Node(string id, string name) => new()
    {
        UUID = CcomUuid.FromKey("Test", id),
        IDInInfoSource = id,
        InfoSource = new InfoSource
        {
            UUID = CcomUuid.ForInfoSource("ENG"),
            ShortName = "ENG"
        },
        ShortName = id,
        FullName = name
    };

    private static SyncSegmentMeshConnections BuildBod()
    {
        var bod = new SyncSegmentMeshConnections(ActionCodes.Add);
        bod.ApplicationArea.Sender = new Sender
        {
            LogicalID = "urn:oiie-sandbox:eng",
            ComponentID = "SimHost"
        };
        bod.ApplicationArea.BODID = "corr-cms-0001";
        bod.ApplicationArea.CreationDateTime = Instant.FromUtc(2026, 8, 1, 9, 14, 0);

        bod.With(new SegmentMesh
        {
            UUID = CcomUuid.FromKey("Test", "eng:DesignRelationships"),
            IDInInfoSource = "eng:DesignRelationships",
            InfoSource = new InfoSource
            {
                UUID = CcomUuid.ForInfoSource("ENG"),
                ShortName = "ENG"
            },
            ShortName = "ENG design relationships",
            Connection =
            [
                new SegmentConnection
                {
                    UUID = CcomUuid.FromKey("Test", "BBFQ0032->P-101"),
                    IDInInfoSource = "BBFQ0032->P-101",
                    InfoSource = new InfoSource
                    {
                        UUID = CcomUuid.ForInfoSource("ENG"),
                        ShortName = "ENG"
                    },
                    Type = new ConnectionType
                    {
                        UUID = CcomUuid.ForReferenceData("ENG", "eng:Supplies"),
                        IDInInfoSource = "eng:Supplies",
                        InfoSource = new InfoSource
                        {
                            UUID = CcomUuid.ForInfoSource("ENG"),
                            ShortName = "ENG"
                        },
                        ShortName = "Supplies",
                        Description = "Supplied By"
                    },
                    From = Node("BBFQ0032", "RCP 1D MTR CLG FL LOOP POWER SUPPLY"),
                    To = Node("P-101", "Cooling water pump")
                }
            ]
        });

        return bod;
    }

    [Fact]
    public void Connection_travels_inside_a_mesh_because_ccom_has_no_bare_connection()
    {
        var document = BuildBod().CreateDocument();

        Assert.Equal("SyncSegmentMeshConnections", document.Root!.Name.LocalName);
        Assert.NotNull(document.Root.Child("DataArea/SegmentMeshConnections/SegmentMesh/Connection"));
    }

    [Fact]
    public void Connection_elements_follow_the_schema_sequence()
    {
        // The CCOM sequence is Type, Network, From, To, Order. Writing From before
        // Type serialises cleanly and is then rejected by the schema, which is how
        // the Asset noun's Model/SerialNumber ordering defect presented.
        var document = BuildBod().CreateDocument();
        var connection = document.Root!.Child("DataArea/SegmentMeshConnections/SegmentMesh/Connection");

        var order = connection!.Elements()
            .Select(e => e.Name.LocalName)
            .Where(n => n is "Type" or "From" or "To")
            .ToList();

        Assert.Equal(["Type", "From", "To"], order);
    }

    [Fact]
    public void Direction_is_preserved_across_a_round_trip()
    {
        var envelope = BodEnvelope.Parse(BuildBod().ToXmlString());
        var mesh = envelope.NounsAs(e => new SegmentMesh(e)).Single();
        var connection = Assert.Single(mesh.Connection);

        // The supplier is the source and the supplied is the sink. Reversing these
        // would still round-trip, so the assertion names both ends explicitly.
        Assert.Equal("BBFQ0032", connection.From?.IDInInfoSource);
        Assert.Equal("P-101", connection.To?.IDInInfoSource);

        // One stored edge, two readings: the inverse is carried by the type rather
        // than by a second connection pointing the other way.
        Assert.Equal("eng:Supplies", connection.Type?.IDInInfoSource);
        Assert.Equal("Supplies", connection.Type?.ShortName);
        Assert.Equal("Supplied By", connection.Type?.Description);
    }

    [Fact]
    public void Envelope_noun_matches_what_the_builder_and_handler_register()
    {
        // Routing is by verb and noun, and the noun is derived from the root element
        // by stripping the verb — not from DataAreaNodeName. They agree here only
        // because the root happens to end in the wrapper name; a BOD where they
        // diverged would build and publish, then find no handler at the far end.
        var envelope = BodEnvelope.Parse(BuildBod().ToXmlString());

        Assert.Equal("Sync", envelope.Verb);
        Assert.Equal("SegmentMeshConnections", envelope.Noun);
        Assert.True(envelope.Is("Sync", "SegmentMeshConnections"));
    }

    [Fact]
    public void Built_bod_satisfies_the_ccom_schema()
    {
        var schemaDirectory = FindSchemaDirectory();
        Assert.True(schemaDirectory is not null, "schemas/ccom was not found from the test output directory.");

        var validator = new BodValidator();
        validator.LoadDirectory(schemaDirectory!);

        var result = validator.Validate(BuildBod().CreateDocument());

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
