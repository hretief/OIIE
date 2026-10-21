using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MmsEngine.Infrastructure.Mms;
using Xunit;
using Provider = MmsProvider.Application;

namespace SimHost.Tests;

public class MmsRestClientTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(null)]
    [InlineData(42L)]
    public async Task Light_units_cross_the_provider_contract_and_return_the_assigned_key(long? systemId)
    {
        var unit = Unit() with
        {
            LightSystemId = systemId,
            LightUnitClassCodeId = 2,
            LightUnitStatusId = 3,
            OwnerId = 4,
            MndotAssetNumber = "MMS-123"
        };

        using var http = Http(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://mms.example/api/lightunits", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);

            var received = Assert.Single(
                (await request.Content.ReadFromJsonAsync<Provider.LightUnitUpsert[]>(Json, ct))!);
            Assert.Equal(unit.ExtAssetId, received.ExtAssetId);
            Assert.Equal(unit.LightUnitName, received.LightUnitName);
            Assert.Equal(systemId, received.LightSystemId);
            Assert.Equal(unit.LightUnitClassCodeId, received.LightUnitClassCodeId);
            Assert.Equal(unit.LightUnitStatusId, received.LightUnitStatusId);
            Assert.Equal(unit.OwnerId, received.OwnerId);
            Assert.Equal(unit.MndotAssetNumber, received.MndotAssetNumber);

            return Reply(new Provider.LightUnitUpsertResult(
                [new Provider.UpsertedLightUnit(123, received.ExtAssetId, true)], []));
        });

        var result = await new MmsRestClient(http).UpsertLightUnitsAsync([unit], CancellationToken.None);

        Assert.Empty(result.Rejections);
        Assert.Equal(new UpsertedLightUnit(123, unit.ExtAssetId, true), Assert.Single(result.Units));
    }

    [Fact]
    public void Provider_accepts_an_omitted_light_system_id()
    {
        var id = Guid.NewGuid();
        var unit = JsonSerializer.Deserialize<Provider.LightUnitUpsert>(
            $$"""{"extAssetId":"{{id}}","lightUnitName":"SL-001"}""", Json);

        Assert.NotNull(unit);
        Assert.Equal(id, unit.ExtAssetId);
        Assert.Null(unit.LightSystemId);
    }

    [Fact]
    public async Task Partial_success_preserves_updates_and_transient_rejections()
    {
        var first = Unit();
        var second = Unit();
        using var http = Http((_, _) => Task.FromResult(Reply(new Provider.LightUnitUpsertResult(
            [new Provider.UpsertedLightUnit(123, first.ExtAssetId, false)],
            [new Provider.UpsertRejection(second.ExtAssetId.ToString(), "Temporarily unavailable", true)]))));

        var result = await new MmsRestClient(http).UpsertLightUnitsAsync(
            [first, second], CancellationToken.None);

        Assert.False(Assert.Single(result.Units).Created);
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(second.ExtAssetId.ToString(), rejection.Key);
        Assert.True(rejection.Transient);
    }

    [Fact]
    public async Task Permanent_rejections_are_returned_without_claiming_a_write()
    {
        var unit = Unit();
        using var http = Http((_, _) => Task.FromResult(Reply(new Provider.LightUnitUpsertResult(
            [], [new Provider.UpsertRejection(unit.ExtAssetId.ToString(), "Unknown owner", false)]))));

        var result = await new MmsRestClient(http).UpsertLightUnitsAsync([unit], CancellationToken.None);

        Assert.Empty(result.Units);
        Assert.False(Assert.Single(result.Rejections).Transient);
    }

    [Fact]
    public async Task Empty_batches_do_not_send_a_request()
    {
        using var http = Http((_, _) => throw new InvalidOperationException("Unexpected HTTP request."));

        var result = await new MmsRestClient(http).UpsertLightUnitsAsync([], CancellationToken.None);

        Assert.Empty(result.Units);
        Assert.Empty(result.Rejections);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Http_failures_are_not_converted_to_empty_success(HttpStatusCode status)
    {
        using var http = Http((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("Provider rejected the request")
        }));

        var error = await Assert.ThrowsAsync<MmsClientException>(() =>
            new MmsRestClient(http).UpsertLightUnitsAsync([Unit()], CancellationToken.None));

        Assert.Contains(status == HttpStatusCode.ServiceUnavailable
            ? "transiently rejected"
            : ((int)status).ToString(), error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"units":[],"rejections":null}""")]
    [InlineData("""{"units":null,"rejections":[]}""")]
    [InlineData("""{"units":[],"rejections":[]}""")]
    public async Task Invalid_or_incomplete_success_bodies_are_rejected(string body)
    {
        using var http = Http((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

        await Assert.ThrowsAsync<MmsClientException>(() =>
            new MmsRestClient(http).UpsertLightUnitsAsync([Unit()], CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_is_forwarded_to_the_http_request()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = Http((_, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not forwarded.");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MmsRestClient(http).UpsertLightUnitsAsync([Unit()], cancellation.Token));
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("duplicate")]
    [InlineData("duplicate-native-id")]
    [InlineData("zero-id")]
    [InlineData("wrong-rejection")]
    public async Task Complete_counts_do_not_mask_invalid_identity_correlations(string invalid)
    {
        var first = Unit();
        var second = Unit();
        var result = invalid switch
        {
            "wrong-key" => new LightUnitUpsertResult(
                [new(1, Guid.NewGuid(), true), new(2, second.ExtAssetId, true)], []),
            "duplicate" => new LightUnitUpsertResult(
                [new(1, first.ExtAssetId, true), new(2, first.ExtAssetId, true)], []),
            "duplicate-native-id" => new LightUnitUpsertResult(
                [new(1, first.ExtAssetId, true), new(1, second.ExtAssetId, true)], []),
            "zero-id" => new LightUnitUpsertResult(
                [new(0, first.ExtAssetId, true), new(2, second.ExtAssetId, true)], []),
            _ => new LightUnitUpsertResult([new(1, first.ExtAssetId, true)],
                [new(Guid.NewGuid().ToString(), "Rejected", false)])
        };
        using var http = Http((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) }));
        await Assert.ThrowsAsync<MmsClientException>(() =>
            new MmsRestClient(http).UpsertLightUnitsAsync([first, second], CancellationToken.None));
    }

    private static LightUnitUpsert Unit() =>
        new(Guid.NewGuid(), "SL-001", null, null, null, null, null);

    private static HttpResponseMessage Reply(Provider.LightUnitUpsertResult result) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(result, options: Json) };

    private static HttpClient Http(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { BaseAddress = new Uri("https://mms.example/api/") };

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
