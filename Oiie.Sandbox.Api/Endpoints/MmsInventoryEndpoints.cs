using Microsoft.AspNetCore.Http.HttpResults;
using Oiie.Sandbox.Api.Providers;

namespace Oiie.Sandbox.Api.Endpoints;

public static class MmsInventoryEndpoints
{
    public static WebApplication MapMmsInventoryEndpoints(this WebApplication app)
    {
        app.MapGet("/admin/mms/lightunits", async Task<Results<Ok<MmsInventoryResponse>, ProblemHttpResult>> (
            IMmsInventorySource source, Guid twin, CancellationToken ct) =>
        {
            if (twin == Guid.Empty)
                return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "A non-empty iTwin UUID is required.");
            try
            {
                return TypedResults.Ok(await source.ListAsync(twin, ct));
            }
            catch (ProviderUnavailableException ex)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway,
                    title: $"{ex.Provider} is unavailable.", detail: ex.Message);
            }
        })
        .WithName("GetMmsLightUnits")
        .WithSummary("Lists live MMS light units for the selected iTwin.")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status502BadGateway);
        return app;
    }
}
