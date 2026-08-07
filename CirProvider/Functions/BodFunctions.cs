using System.Xml;
using System.Xml.Linq;
using CirProvider.Application;
using CirProvider.Domain.Bod;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace CirProvider.Functions;

/// <summary>
/// Annex A endpoint. The BOD model is defined for messaging environments such as
/// ws-ISBM, where these documents travel as channel message content. This HTTP
/// route exercises the same dispatcher synchronously, so the BOD layer can be
/// tested without an ISBM broker in the loop.
/// </summary>
public sealed class BodFunctions(IBodDispatcher dispatcher)
{
    [Function("PostBod")]
    public async Task<IActionResult> PostBod(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "bods")] HttpRequest req,
        CancellationToken ct)
    {
        XDocument document;
        try
        {
            document = await XDocument.LoadAsync(req.Body, LoadOptions.None, ct);
        }
        catch (XmlException ex)
        {
            return new BadRequestObjectResult(new
            {
                type = "https://www.openoandm.org/ws-cir/1.0/fault",
                title = "MalformedBod",
                status = 400,
                detail = ex.Message
            });
        }

        var response = await dispatcher.DispatchAsync(document, ct);

        // Cancel BODs and ChangeEntryCIRID define no response, and a
        // confirmation code of Never suppresses one. 202 says "accepted, nothing
        // to send back" rather than pretending an empty body is a BOD.
        if (response is null) return new AcceptedResult();

        return new ContentResult
        {
            Content = response.ToString(SaveOptions.None),
            ContentType = "application/xml",
            StatusCode = StatusCodes.Status200OK
        };
    }

    /// <summary>The Annex A catalogue, for discovery and conformance reporting.</summary>
    [Function("GetBodCatalogue")]
    public IActionResult GetBodCatalogue(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "bods/catalogue")] HttpRequest req)
    {
        return new OkObjectResult(new
        {
            releaseId = Infrastructure.Bod.BodXmlWriter.ReleaseId,
            versionId = Infrastructure.Bod.BodXmlWriter.VersionId,
            namespaces = new
            {
                cir = Infrastructure.Bod.CirNs.Cir.NamespaceName,
                oa = Infrastructure.Bod.CirNs.Oa.NamespaceName
            },
            requestBods = BodCatalogue.RequestBods.Select(b => new
            {
                bod = b.BodName,
                verb = b.Verb.ToString(),
                noun = b.Noun,
                responseBod = b.ResponseBod
            })
        });
    }
}
