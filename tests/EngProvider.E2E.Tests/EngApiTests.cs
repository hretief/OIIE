using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace EngProvider.E2E.Tests;

/// <summary>
/// End-to-end tests against the ENG REST surface.
///
/// Every assertion here is made through HTTP against a live host and a live
/// database. The suite deliberately concentrates on the seams that only an
/// end-to-end run can exercise: route templates, JSON shape on the wire, the
/// mapping from database rules to status codes, and the release workflow taken
/// as a whole rather than a step at a time.
/// </summary>
[Collection("eng-host")]
public sealed class EngApiTests(EngHostFixture host)
{
    private HttpClient Client => host.Client;

    // ---- Catalogue -------------------------------------------------------

    [Fact]
    public async Task Health_reports_healthy_and_counts_seeded_itwins()
    {
        using var response = await Client.GetAsync("api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("iTwins").GetInt32() >= 1,
            $"Bootstrap should have produced an iTwin. Host output:{Environment.NewLine}{host.HostOutput}");
    }

    /// <summary>
    /// Guards the serializer contract, not merely the payload. Program.cs opts
    /// into camelCase, so a property arriving as "IModelId" would mean the
    /// configuration silently stopped applying.
    /// </summary>
    [Fact]
    public async Task IModels_are_serialized_in_camel_case()
    {
        var raw = await Client.GetStringAsync("api/imodels");

        Assert.Contains("\"iModelId\"", raw);
        Assert.DoesNotContain("\"IModelId\"", raw);
    }

    [Fact]
    public async Task Classes_route_offers_only_concrete_classes()
    {
        var classes = await Client.GetFromJsonAsync<List<JsonElement>>("api/classes", EngHostFixture.Json);

        Assert.NotNull(classes);
        Assert.NotEmpty(classes);

        // Abstract classes are refused by the database, so offering one here
        // would be advertising a guaranteed failure.
        Assert.DoesNotContain(classes!, c =>
            string.Equals(c.GetProperty("classModifier").GetString(), "Abstract", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unknown_element_id_is_404_not_500()
    {
        using var response = await Client.GetAsync("api/elements/999999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_element_id_is_400()
    {
        using var response = await Client.GetAsync("api/elements/not-a-number");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_imodel_guid_is_400()
    {
        using var response = await Client.GetAsync("api/imodels/not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Elements --------------------------------------------------------

    [Fact]
    public async Task Element_can_be_created_and_read_back_by_code()
    {
        var version = await CreateDraftAsync("elements-roundtrip");
        var code = UniqueCode("P");

        var result = await UpsertAsync(version, new
        {
            ecClassId = host.ConcreteClassId,
            codeValue = code,
            userLabel = "Feed pump"
        });

        Assert.Empty(RejectionsOf(result));
        Assert.Equal(1, result.GetProperty("created").GetInt32());

        var element = await Client.GetFromJsonAsync<JsonElement>(
            $"api/imodels/{host.IModelId}/elements/by-code/{code}", EngHostFixture.Json);

        Assert.Equal(code, element.GetProperty("codeValue").GetString());
        Assert.Equal("Feed pump", element.GetProperty("userLabel").GetString());

        // Maturity is derived from the version, never stored on the element.
        Assert.False(element.GetProperty("isReleased").GetBoolean());
        Assert.Equal("Draft", element.GetProperty("namedVersionState").GetString());
    }

    /// <summary>
    /// Enum values must travel as names. An ordinal survives a schema reorder
    /// only by luck, and a caller reading 1 has no way to notice it changed.
    /// </summary>
    [Fact]
    public async Task Named_version_state_is_serialized_as_a_name()
    {
        var version = await CreateDraftAsync("enum-shape");
        var raw = await Client.GetStringAsync($"api/named-versions/{version}");

        Assert.Contains("\"state\":\"Draft\"", raw);
    }

    [Fact]
    public async Task Duplicate_code_is_rejected_without_losing_the_rest_of_the_batch()
    {
        var version = await CreateDraftAsync("duplicate-code");
        var duplicated = UniqueCode("DUP");
        var accepted = UniqueCode("OK");

        await UpsertAsync(version, new { ecClassId = host.ConcreteClassId, codeValue = duplicated });

        var result = await UpsertAsync(version,
            new { ecClassId = host.ConcreteClassId, codeValue = duplicated },
            new { ecClassId = host.ConcreteClassId, codeValue = accepted });

        var rejections = RejectionsOf(result);

        // The good row must still persist: a batch is not all-or-nothing here,
        // and losing valid work to one bad neighbour would be the worse failure.
        Assert.Single(rejections);
        Assert.Equal(duplicated, rejections[0].GetProperty("key").GetString());
        Assert.False(rejections[0].GetProperty("transient").GetBoolean());
        Assert.Equal(1, result.GetProperty("created").GetInt32());
    }

    [Fact]
    public async Task Empty_element_batch_is_400()
    {
        var version = await CreateDraftAsync("empty-batch");

        using var response = await PostJsonAsync($"api/named-versions/{version}/elements", "[]");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_element_body_is_400_not_500()
    {
        var version = await CreateDraftAsync("malformed-body");

        using var response = await PostJsonAsync($"api/named-versions/{version}/elements", "{ this is not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Named_version_requires_imodel_and_name()
    {
        using var response = await PostJsonAsync("api/named-versions", """{"name":"orphan"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- The release gate ------------------------------------------------

    /// <summary>
    /// The workflow this whole system exists to enforce, driven end to end:
    /// a draft with an open finding cannot be released; once the finding is
    /// resolved it can; and once released it is frozen.
    /// </summary>
    [Fact]
    public async Task Open_finding_blocks_release_until_resolved_then_version_is_frozen()
    {
        var version = await CreateDraftAsync("release-gate");
        var code = UniqueCode("GATE");

        await UpsertAsync(version, new { ecClassId = host.ConcreteClassId, codeValue = code });

        var finding = await Client.PostAsJsonAsync(
            $"api/named-versions/{version}/findings",
            new { codeValue = code, severity = "Error", message = "Missing datasheet." },
            EngHostFixture.Json);

        Assert.Equal(HttpStatusCode.Created, finding.StatusCode);

        var findingId = (await finding.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("findingId").GetInt64();

        // Refused: well-formed request, but server state says no. That is a 409,
        // not a 400 and emphatically not a 500.
        using (var blocked = await Client.PostAsync($"api/named-versions/{version}/release", null))
        {
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

            var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("released").GetBoolean());
            Assert.NotNull(body.GetProperty("reason").GetString());
        }

        using (var resolved = await Client.PostAsync($"api/findings/{findingId}/resolve", null))
        {
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        }

        using (var released = await Client.PostAsync($"api/named-versions/{version}/release", null))
        {
            Assert.Equal(HttpStatusCode.OK, released.StatusCode);

            var body = await released.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("released").GetBoolean());
            Assert.Equal("Released", body.GetProperty("state").GetString());
        }

        // The element's maturity follows the version without being restated.
        var element = await Client.GetFromJsonAsync<JsonElement>(
            $"api/imodels/{host.IModelId}/elements/by-code/{code}", EngHostFixture.Json);

        Assert.True(element.GetProperty("isReleased").GetBoolean());

        // A release is a handover record; what was handed over cannot be edited.
        var afterRelease = await UpsertAsync(version,
            new { ecClassId = host.ConcreteClassId, codeValue = UniqueCode("LATE") });

        Assert.NotEmpty(RejectionsOf(afterRelease));
        Assert.Equal(0, afterRelease.GetProperty("created").GetInt32());
    }

    [Fact]
    public async Task Empty_version_cannot_be_released()
    {
        var version = await CreateDraftAsync("empty-version");

        using var response = await Client.PostAsync($"api/named-versions/{version}/release", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Resolving_an_unknown_finding_is_404()
    {
        using var response = await Client.PostAsync("api/findings/999999999/resolve", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Findings_route_returns_only_open_findings_when_asked()
    {
        var version = await CreateDraftAsync("finding-filter");
        var code = UniqueCode("FIND");

        await UpsertAsync(version, new { ecClassId = host.ConcreteClassId, codeValue = code });

        using var raised = await Client.PostAsJsonAsync(
            $"api/named-versions/{version}/findings",
            new { codeValue = code, severity = "Warning", message = "Check line size." },
            EngHostFixture.Json);

        var findingId = (await raised.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("findingId").GetInt64();

        await Client.PostAsync($"api/findings/{findingId}/resolve", null);

        // The route filters on ?all=true; anything else means open-only.
        var open = await Client.GetFromJsonAsync<List<JsonElement>>(
            $"api/named-versions/{version}/findings", EngHostFixture.Json);

        Assert.DoesNotContain(open!, f => f.GetProperty("findingId").GetInt64() == findingId);

        var all = await Client.GetFromJsonAsync<List<JsonElement>>(
            $"api/named-versions/{version}/findings?all=true", EngHostFixture.Json);

        Assert.Contains(all!, f => f.GetProperty("findingId").GetInt64() == findingId);
    }

    [Fact]
    public async Task Invalid_finding_severity_is_400()
    {
        var version = await CreateDraftAsync("bad-severity");

        using var response = await Client.PostAsJsonAsync(
            $"api/named-versions/{version}/findings",
            new { severity = "Catastrophic", message = "Nope." },
            EngHostFixture.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Helpers ---------------------------------------------------------

    private static string UniqueCode(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}"[..12];

    private async Task<long> CreateDraftAsync(string name)
    {
        using var response = await Client.PostAsJsonAsync("api/named-versions", new
        {
            iModelId = host.IModelId,
            name = $"{name}-{Guid.NewGuid():N}"[..24],
            description = "Created by the end-to-end suite.",
            createdBy = "e2e"
        }, EngHostFixture.Json);

        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"Expected 201 creating a named version but got {(int)response.StatusCode}. " +
            $"Host output:{Environment.NewLine}{host.HostOutput}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("namedVersionId").GetInt64();
    }

    private async Task<JsonElement> UpsertAsync(long namedVersionId, params object[] elements)
    {
        using var response = await Client.PostAsJsonAsync(
            $"api/named-versions/{namedVersionId}/elements", elements, EngHostFixture.Json);

        Assert.True(response.IsSuccessStatusCode,
            $"Upsert returned {(int)response.StatusCode}. Host output:{Environment.NewLine}{host.HostOutput}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<JsonElement> RejectionsOf(JsonElement result) =>
        [.. result.GetProperty("rejections").EnumerateArray()];

    private Task<HttpResponseMessage> PostJsonAsync(string route, string json) =>
        Client.PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"));
}
