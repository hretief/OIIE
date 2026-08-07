using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using IsbmProvider.Abstractions;
using IsbmProvider.Models;

namespace IsbmProvider.Infrastructure;

/// <summary>
/// Azure Blob Storage claim-check for ISBM message payloads. Large CCOM BODs are offloaded
/// here before being sent through Service Bus; consumers get a time-limited SAS URL to retrieve them.
///
/// Flow:
///   Publish path:  ServiceBusMessageBroker.OffloadIfLargeAsync → StoreAsync (returns payloadRef)
///   Read path:     ReadPublication/ReadResponse → ResolveAsync (rehydrates InlineContent from Blob)
/// </summary>
public sealed class BlobPayloadStore : IPayloadStore
{
    private const string ContainerName = "isbm-payloads";
    private static readonly TimeSpan SasExpiry = TimeSpan.FromHours(4);

    private readonly BlobServiceClient _serviceClient;
    private readonly ILogger<BlobPayloadStore> _log;
    private BlobContainerClient? _container;

    public BlobPayloadStore(BlobServiceClient serviceClient, ILogger<BlobPayloadStore> log)
    {
        _serviceClient = serviceClient;
        _log = log;
    }

    public async Task<string> StoreAsync(MessageContent content, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct);
        var blobName = $"{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid()}";
        var blob = container.GetBlobClient(blobName);

        var data = Encoding.UTF8.GetBytes(content.InlineContent ?? "");
        var headers = new BlobHttpHeaders { ContentType = content.MediaType };

        await blob.UploadAsync(new BinaryData(data), new BlobUploadOptions { HttpHeaders = headers }, ct);
        _log.LogInformation("Payload stored: {BlobName} ({Bytes} bytes, {MediaType})", blobName, data.Length, content.MediaType);

        return blobName;
    }

    public async Task<MessageContent> RetrieveAsync(string payloadRef, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct);
        var blob = container.GetBlobClient(payloadRef);
        var response = await blob.DownloadContentAsync(ct);
        var body = response.Value.Content.ToString();
        var mediaType = response.Value.Details.ContentType ?? "application/octet-stream";

        return new MessageContent
        {
            MediaType = mediaType,
            InlineContent = body
        };
    }

    public async Task<MessageContent> ResolveAsync(MessageContent content, CancellationToken ct = default)
    {
        // Not claim-checked — pass through as-is.
        if (content.PayloadRef is null)
            return content;

        // Rehydrate: fetch from Blob and return as inline content.
        var retrieved = await RetrieveAsync(content.PayloadRef, ct);
        return content with
        {
            InlineContent = retrieved.InlineContent,
            MediaType = retrieved.MediaType
        };
    }

    private async Task<BlobContainerClient> GetContainerAsync(CancellationToken ct)
    {
        if (_container is not null) return _container;
        _container = _serviceClient.GetBlobContainerClient(ContainerName);
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        return _container;
    }
}
