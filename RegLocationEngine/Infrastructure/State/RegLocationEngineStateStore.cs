using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using RegLocationEngine.Application;

namespace RegLocationEngine.Infrastructure.State;

/// <summary>
/// What the engine remembers between approvals.
///
/// One field, unlike EngEngine's two. There is no watermark here because there is
/// nothing to poll a window of: an approval arrives as a notification, and the
/// sweep exists only to catch the ones that did not arrive. The question the
/// sweep asks is "which approved tags have I not published", which the published
/// set answers directly and a timestamp would answer only approximately.
/// </summary>
public sealed record RegLocationEngineState
{
    /// <summary>
    /// Tags already published, keyed by registry GUID and revision.
    ///
    /// By GUID rather than tag_id so the engine's memory survives the registry
    /// being rebuilt from its own source -- tag_id is an allocator's number and
    /// would be reassigned. With the revision, because the GUID deliberately
    /// survives revision: keying on the GUID alone would mean revision 2 of
    /// TIC-101 counted as already published the moment revision 1 was, and the
    /// correction would never leave the building.
    /// </summary>
    public HashSet<string> PublishedTags { get; init; } = [];

    /// <summary>
    /// The key form used in <see cref="PublishedTags"/>. Centralised so the two
    /// callers that build it cannot disagree about the separator.
    /// </summary>
    public static string KeyFor(Guid federationId, int revision) => $"{federationId:D}:{revision}";
}

public interface IRegLocationEngineStateStore
{
    Task<(RegLocationEngineState State, ETag ETag)> ReadAsync(CancellationToken ct);

    /// <summary>
    /// Writes state back, but only if it has not changed since it was read.
    /// Returns false when another instance won the race, in which case this
    /// pass's work is already recorded or will be redone safely -- the BOD is a
    /// Replace, so a second publication of the same tag is a no-op downstream.
    /// </summary>
    Task<bool> TryWriteAsync(RegLocationEngineState state, ETag expected, CancellationToken ct);

    /// <summary>
    /// For day zero, and specifically because PublishedTags is keyed by
    /// federation GUID and revision so that the engine's memory survives
    /// REG-LOCATION being rebuilt. That is the right behaviour normally and
    /// exactly wrong after a reset: the registry comes back empty, its ids
    /// restart from 1, and the first tags approved afterwards are recognised as
    /// already published and never sent. Clearing REG-LOCATION's database
    /// without clearing this leaves an engine that publishes nothing and
    /// reports no error.
    ///
    /// Unconditional rather than compare-and-swap: a reset is not racing a
    /// sweep for correctness, and failing it over an ETag would leave the state
    /// behind.
    /// </summary>
    Task ClearAsync(CancellationToken ct);
}

public sealed class BlobRegLocationEngineStateStore(
    BlobServiceClient blobs,
    IOptions<RegLocationEngineOptions> options) : IRegLocationEngineStateStore
{
    private readonly RegLocationEngineOptions _options = options.Value;

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<(RegLocationEngineState State, ETag ETag)> ReadAsync(CancellationToken ct)
    {
        var blob = await GetBlobAsync(ct);

        try
        {
            var result = await blob.DownloadContentAsync(ct);

            var state = JsonSerializer.Deserialize<RegLocationEngineState>(
                result.Value.Content.ToString(), Json) ?? new RegLocationEngineState();

            return (state, result.Value.Details.ETag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // First run. ETag.All would overwrite whatever appeared in the
            // meantime, so the "must not exist" precondition is expressed by
            // default(ETag) and honoured in TryWriteAsync.
            return (new RegLocationEngineState(), default);
        }
    }

    public async Task<bool> TryWriteAsync(
        RegLocationEngineState state, ETag expected, CancellationToken ct)
    {
        var blob = await GetBlobAsync(ct);

        var conditions = expected == default
            ? new BlobRequestConditions { IfNoneMatch = ETag.All }
            : new BlobRequestConditions { IfMatch = expected };

        var payload = BinaryData.FromBytes(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, Json)));

        try
        {
            await blob.UploadAsync(
                payload,
                new BlobUploadOptions { Conditions = conditions },
                ct);

            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return false;
        }
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        // Deleted rather than overwritten with an empty state: ReadAsync already
        // treats a missing blob as a first run, so removal and reinitialisation
        // are the same thing and there is no stale ETag left to reason about.
        var blob = await GetBlobAsync(ct);
        await blob.DeleteIfExistsAsync(cancellationToken: ct);
    }

    private async Task<BlobClient> GetBlobAsync(CancellationToken ct)
    {
        var container = blobs.GetBlobContainerClient(_options.StateContainer);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);

        return container.GetBlobClient(_options.StateBlob);
    }
}
