using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace EngEngine.Infrastructure.State;

using EngEngine.Application;

/// <summary>
/// What the engine remembers between drains.
///
/// Two fields, and they do different jobs. The watermark bounds how much of ENG
/// is re-read; the published set decides what is actually sent. Neither alone is
/// sufficient: a watermark on its own would double-publish everything inside the
/// overlap window, and a published set on its own would grow without bound and
/// still require reading the whole model each pass.
/// </summary>
public sealed record EngEngineState
{
    /// <summary>
    /// The high-water mark of marker ModifiedUtc seen so far, before overlap is
    /// applied. Null on a first run, which reads the iModel's whole history.
    /// </summary>
    public DateTime? Watermark { get; init; }

    /// <summary>
    /// Markers already posted, by VersionGuid rather than by ENG's local key.
    /// The engine's memory should survive ENG being rebuilt from its own source.
    /// </summary>
    public HashSet<Guid> PublishedVersions { get; init; } = [];
}

public interface IEngEngineStateStore
{
    Task<(EngEngineState State, ETag ETag)> ReadAsync(CancellationToken ct);

    /// <summary>
    /// Writes state back, but only if it has not changed since it was read.
    /// Returns false when another instance won the race, in which case this
    /// drain's work is already recorded or will be redone safely.
    /// </summary>
    Task<bool> TryWriteAsync(EngEngineState state, ETag expected, CancellationToken ct);

    /// <summary>
    /// Forgets the watermark and every published marker, unconditionally.
    ///
    /// For day zero, and specifically because PublishedVersions is keyed by
    /// VersionGuid so that the engine's memory survives ENG being rebuilt. That
    /// is the right behaviour normally and exactly wrong after a reset: ENG comes
    /// back with new versions, the engine still holds the old guids, and anything
    /// it recognises is silently treated as already sent. Clearing ENG's tables
    /// without clearing this leaves an engine that publishes nothing and reports
    /// no error.
    ///
    /// Unconditional rather than compare-and-swap: a reset is not racing a drain
    /// for correctness, and failing it over an ETag would leave the state behind.
    /// </summary>
    Task ClearAsync(CancellationToken ct);
}

public sealed class BlobEngEngineStateStore(
    BlobServiceClient blobs,
    IOptions<EngEngineOptions> options) : IEngEngineStateStore
{
    private readonly EngEngineOptions _options = options.Value;

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<(EngEngineState State, ETag ETag)> ReadAsync(CancellationToken ct)
    {
        var blob = await GetBlobAsync(ct);

        try
        {
            var result = await blob.DownloadContentAsync(ct);

            var state = JsonSerializer.Deserialize<EngEngineState>(
                result.Value.Content.ToString(), Json) ?? new EngEngineState();

            return (state, result.Value.Details.ETag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // First run. ETag.All would overwrite whatever appeared in the
            // meantime, so the "must not exist" precondition is expressed by
            // default(ETag) and honoured in TryWriteAsync.
            return (new EngEngineState(), default);
        }
    }

    public async Task<bool> TryWriteAsync(
        EngEngineState state, ETag expected, CancellationToken ct)
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
