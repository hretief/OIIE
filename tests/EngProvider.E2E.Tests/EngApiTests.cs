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
/// mapping from database rules to status codes, and baseline behaviour taken
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
        var code = UniqueCode("P");

        var result = await UpsertAsync(new
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

        // Position, not maturity: an element is simply somewhere in the model's
        // history, and whether a marker has passed over it is asked separately.
        Assert.True(element.GetProperty("changesetIndex").GetInt32() > 0);
    }

    /// <summary>
    /// A marker is pinned when it is created, not later. If the changeset were
    /// assigned by some subsequent act, there would be a window in which a
    /// named version existed without saying what it referred to.
    /// </summary>
    [Fact]
    public async Task Named_version_is_pinned_to_a_changeset_when_created()
    {
        var version = await CreateMarkerAsync("pinned-on-create");

        var body = await Client.GetFromJsonAsync<JsonElement>(
            $"api/named-versions/{version}", EngHostFixture.Json);

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("changesetId").GetString()));
        Assert.True(body.GetProperty("changesetIndex").GetInt32() > 0);
    }

    /// <summary>
    /// Membership is derived from position, so a marker cut after work exists
    /// contains that work without anything having been recorded against it.
    /// </summary>
    [Fact]
    public async Task Marker_contains_the_work_authored_before_it()
    {
        var before = UniqueCode("BEFORE");
        await UpsertAsync(new { ecClassId = host.ConcreteClassId, codeValue = before });

        var version = await CreateMarkerAsync("membership");

        var after = UniqueCode("AFTER");
        await UpsertAsync(new { ecClassId = host.ConcreteClassId, codeValue = after });

        var contents = await Client.GetFromJsonAsync<List<JsonElement>>(
            $"api/named-versions/{version}/elements", EngHostFixture.Json);

        Assert.Contains(contents!, e => e.GetProperty("codeValue").GetString() == before);
        Assert.DoesNotContain(contents!, e => e.GetProperty("codeValue").GetString() == after);
    }

    /// <summary>
    /// What a marker described has already been published, so it cannot be
    /// rewritten afterwards. Remediation is forward-only: new work, new marker.
    /// </summary>
    [Fact]
    public async Task Work_below_a_marker_can_no_longer_be_edited()
    {
        var code = UniqueCode("FROZEN");

        var created = await UpsertAsync(
            new { ecClassId = host.ConcreteClassId, codeValue = code });

        var elementId = created.GetProperty("elements")[0]
            .GetProperty("ecInstanceId").GetInt64();

        await CreateMarkerAsync("freeze");

        var late = await UpsertAsync(new
        {
            ecInstanceId = elementId,
            ecClassId = host.ConcreteClassId,
            codeValue = code,
            userLabel = "Edited after the baseline"
        });

        Assert.NotEmpty(RejectionsOf(late));
    }

    [Fact]
    public async Task Duplicate_code_is_rejected_without_losing_the_rest_of_the_batch()
    {
        var duplicated = UniqueCode("DUP");
        var accepted = UniqueCode("OK");

        await UpsertAsync(new { ecClassId = host.ConcreteClassId, codeValue = duplicated });

        var result = await UpsertAsync(
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
        using var response = await PostJsonAsync($"api/imodels/{host.IModelId}/elements", "[]");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_element_body_is_400_not_500()
    {
        using var response = await PostJsonAsync(
            $"api/imodels/{host.IModelId}/elements", "{ this is not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Named_version_requires_imodel_and_name()
    {
        using var response = await PostJsonAsync("api/named-versions", """{"name":"orphan"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Helpers ---------------------------------------------------------

    private static string UniqueCode(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}"[..12];

    private async Task<long> CreateMarkerAsync(string name)
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

    private async Task<JsonElement> UpsertAsync(params object[] elements)
    {
        using var response = await Client.PostAsJsonAsync(
            $"api/imodels/{host.IModelId}/elements", elements, EngHostFixture.Json);

        Assert.True(response.IsSuccessStatusCode,
            $"Upsert returned {(int)response.StatusCode}. Host output:{Environment.NewLine}{host.HostOutput}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<JsonElement> RejectionsOf(JsonElement result) =>
        [.. result.GetProperty("rejections").EnumerateArray()];

    private Task<HttpResponseMessage> PostJsonAsync(string route, string json) =>
        Client.PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"));
}
