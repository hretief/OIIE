using System.Xml.Linq;
using Oiie.Ccom.Cir;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// A CIR response must be attributable to the request it answers.
///
/// These cover a defect seen in the demo environment: a GetRegistry filtered on one
/// CIRID came back carrying a different CIRID's entries. The reply was well-formed,
/// so nothing rejected it, and the equivalence lookup concluded that an iTwin had no
/// related MMS owner when the registry plainly said it did. The only thing that
/// distinguished the foreign reply was the BODID it echoed.
/// </summary>
public class CirResponseCorrelationTests
{
    private static XDocument ShowRegistry(string originalBodId) => XDocument.Parse(
        $"""
        <cir:ShowRegistry xmlns:cir="http://www.openoandm.org/ws-cir/"
                          xmlns:oa="http://www.openapplications.org/oagis/9">
          <oa:ApplicationArea>
            <oa:BODID>a-freshly-minted-response-id</oa:BODID>
          </oa:ApplicationArea>
          <cir:DataArea>
            <oa:Show>
              <oa:OriginalApplicationArea>
                <oa:BODID>{originalBodId}</oa:BODID>
              </oa:OriginalApplicationArea>
            </oa:Show>
            <cir:GetRegistryResponse />
          </cir:DataArea>
        </cir:ShowRegistry>
        """);

    [Fact]
    public void Response_carries_the_bod_id_of_the_request_it_answers()
    {
        var response = CirResponse.Parse(ShowRegistry("corr-0001"));

        Assert.Equal("corr-0001", response.OriginalBodId);
    }

    /// <summary>
    /// The response's own BODID is newly minted per reply, so it can never identify
    /// the request. Asserting they differ keeps a future refactor from quietly
    /// pointing the correlation check at the wrong element, which would make every
    /// comparison fail rather than only the foreign ones.
    /// </summary>
    [Fact]
    public void Own_bod_id_is_distinct_from_the_original()
    {
        var response = CirResponse.Parse(ShowRegistry("corr-0001"));

        Assert.Equal("a-freshly-minted-response-id", response.BodId);
        Assert.NotEqual(response.BodId, response.OriginalBodId);
    }

    [Fact]
    public void A_reply_to_another_request_is_distinguishable()
    {
        var response = CirResponse.Parse(ShowRegistry("corr-9999"));

        Assert.NotEqual("corr-0001", response.OriginalBodId);
    }

    /// <summary>
    /// OriginalApplicationArea is optional. A provider that omits it yields null, and
    /// the client treats null as acceptable rather than discarding the response --
    /// otherwise every such provider would time out on every request.
    /// </summary>
    [Fact]
    public void Absent_original_area_yields_null_rather_than_throwing()
    {
        var document = XDocument.Parse(
            """
            <cir:ShowRegistry xmlns:cir="http://www.openoandm.org/ws-cir/"
                              xmlns:oa="http://www.openapplications.org/oagis/9">
              <oa:ApplicationArea>
                <oa:BODID>only-its-own-id</oa:BODID>
              </oa:ApplicationArea>
              <cir:DataArea>
                <oa:Show />
                <cir:GetRegistryResponse />
              </cir:DataArea>
            </cir:ShowRegistry>
            """);

        var response = CirResponse.Parse(document);

        Assert.Null(response.OriginalBodId);
    }
}
