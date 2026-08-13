using Azure.Storage.Blobs;

namespace SimHost.Infrastructure.Blob;

/// <summary>
/// BOD bodies live in Blob Storage; the Message row holds only a reference.
/// Payloads are XML and some are large, and the Message table is queried on every
/// SignalR push for the swimlane — nvarchar(max) bodies would degrade that
/// progressively over a session (spec §6.6).
/// </summary>
public interface IPayloadStore
{
    Task<string> SaveAsync(string participantId, string correlationId, string xml, CancellationToken ct = default);

    Task<string?> ReadAsync(string contentRef, CancellationToken ct = default);
}

public sealed class BlobPayloadStore : IPayloadStore
{
    private readonly BlobContainerClient _container;
    private readonly string _prefix;

    public BlobPayloadStore(BlobServiceClient service, IConfiguration configuration)
    {
        var containerName = configuration["Storage:PayloadContainer"] ?? "sandbox-payloads";
        _container = service.GetBlobContainerClient(containerName);

        // Per-developer prefix so a workstation session cannot collide with CI.
        _prefix = configuration["Storage:Prefix"] ?? "local";
    }

    public async Task<string> SaveAsync(
        string participantId, string correlationId, string xml, CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);

        var path = $"{_prefix}/{participantId}/{correlationId}/{Guid.NewGuid():N}.xml";
        var blob = _container.GetBlobClient(path);

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        await blob.UploadAsync(stream, overwrite: true, cancellationToken: ct);

        return path;
    }

    public async Task<string?> ReadAsync(string contentRef, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(contentRef);

        if (!await blob.ExistsAsync(ct))
        {
            return null;
        }

        var response = await blob.DownloadContentAsync(ct);
        return response.Value.Content.ToString();
    }
}

/// <summary>
/// Stands in when no storage account is configured.
///
/// Registered unconditionally so the outbox and inbox can run without storage: the
/// message archive row matters more than the payload body, and losing the ability
/// to exercise messaging entirely because a blob account is missing is a worse
/// trade than losing the wire view. The returned reference records why the body is
/// absent rather than pretending it was stored.
/// </summary>
public sealed class NullPayloadStore(ILogger<NullPayloadStore> logger) : IPayloadStore
{
    private bool _warned;

    public Task<string> SaveAsync(
        string participantId, string correlationId, string xml, CancellationToken ct = default)
    {
        if (!_warned)
        {
            _warned = true;
            logger.LogWarning(
                "No blob storage configured; BOD payload bodies are not being retained. " +
                "Set Storage:BlobServiceUri to enable the wire view.");
        }

        return Task.FromResult("unstored:no-storage-configured");
    }

    public Task<string?> ReadAsync(string contentRef, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
