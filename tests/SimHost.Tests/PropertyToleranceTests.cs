using Oiie.Ccom.Bods;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// CCOM supersedes the attribute constructs with property constructs, and both are
/// present in the current schema. The Sandbox writes attributes deliberately, but a
/// participant running a newer stack may send properties — and silently dropping
/// them would be the worst possible behaviour for a tool whose point is that
/// receivers retain what they do not fully understand.
/// </summary>
public class PropertyToleranceTests
{
    private const string PropertyShapedSegment = """
        <SyncSegments xmlns="http://www.mimosa.org/ccom4"
                      xmlns:oa="http://www.openapplications.org/oagis/9"
                      releaseID="1.0">
          <oa:ApplicationArea>
            <oa:Sender><oa:LogicalID>ModernSystem</oa:LogicalID></oa:Sender>
            <oa:BODID>corr-property-0001</oa:BODID>
          </oa:ApplicationArea>
          <DataArea>
            <oa:Sync>
              <oa:ActionCriteria>
                <oa:ActionExpression actionCode="Add" expressionLanguage="Xpath">/SyncSegments/DataArea/Segments</oa:ActionExpression>
              </oa:ActionCriteria>
            </oa:Sync>
            <Segments>
              <Segment>
                <UUID>11111111-2222-3333-4444-555555555555</UUID>
                <IDInInfoSource>TIC-207</IDInInfoSource>
                <InfoSource><ShortName>ModernSystem</ShortName></InfoSource>
                <Property>
                  <Type><ShortName>Design pressure</ShortName><IDInInfoSource>rdl:DesignPressure</IDInInfoSource></Type>
                  <ValueContent>
                    <Measure>
                      <Value>16.5</Value>
                      <UnitOfMeasure><ShortName>bar</ShortName></UnitOfMeasure>
                    </Measure>
                  </ValueContent>
                </Property>
                <PropertySetForEntity>
                  <PropertySet>
                    <Type><ShortName>Instrument</ShortName><IDInInfoSource>rdl:Instrument</IDInInfoSource></Type>
                    <SetProperty>
                      <Type><ShortName>Range maximum</ShortName><IDInInfoSource>rdl:RangeMaximum</IDInInfoSource></Type>
                      <ValueContent><Text>250 degC</Text></ValueContent>
                    </SetProperty>
                  </PropertySet>
                </PropertySetForEntity>
                <ShortName>TIC-207</ShortName>
              </Segment>
            </Segments>
          </DataArea>
        </SyncSegments>
        """;

    private static Segment ParsePropertyShaped() =>
        BodEnvelope.Parse(PropertyShapedSegment).NounsAs(e => new Segment(e)).Single();

    [Fact]
    public void Loose_properties_are_read_not_discarded()
    {
        var segment = ParsePropertyShaped();

        Assert.Empty(segment.Attribute);
        var property = Assert.Single(segment.Property);
        Assert.Equal("rdl:DesignPressure", property.Type?.IDInInfoSource);

        var measure = Assert.IsType<MeasureContent>(property.ValueContent);
        Assert.Equal(16.5m, measure.Value);
        Assert.Equal("bar", measure.UnitOfMeasure);
    }

    [Fact]
    public void Property_sets_are_read_as_classification()
    {
        var segment = ParsePropertyShaped();

        Assert.Empty(segment.AttributeSetForEntity);
        var setForEntity = Assert.Single(segment.PropertySetForEntity);

        Assert.Equal("rdl:Instrument", setForEntity.AttributeSet?.Type?.IDInInfoSource);
        var member = Assert.Single(setForEntity.AttributeSet!.SetAttribute);
        Assert.Equal("rdl:RangeMaximum", member.Type?.IDInInfoSource);
    }

    [Fact]
    public void Combined_accessors_present_both_shapes_uniformly()
    {
        // Ingestion reads through these, so a handler never has to know which
        // construct the sender used.
        var segment = ParsePropertyShaped();

        Assert.Single(segment.AllLooseValues);
        Assert.Single(segment.AllValueSets);
    }

    [Fact]
    public void Properties_are_never_written_back()
    {
        // Send-conservative, enforced by XmlIgnore rather than by convention: a
        // round trip through the writer must not turn a received property into an
        // emitted one, or the Sandbox would be claiming a construct it does not
        // otherwise support.
        var segment = ParsePropertyShaped();

        var bod = new SyncSegments(ActionCodes.Add);
        bod.With(segment);
        var xml = bod.ToXmlString();

        Assert.DoesNotContain("<Property>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("PropertySetForEntity", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("SetProperty", xml, StringComparison.Ordinal);
    }
}
