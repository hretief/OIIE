using System.Xml.Linq;
using Azure;
using EngEngine.Application;
using EngEngine.Infrastructure.Eng;
using EngEngine.Infrastructure.State;
using Oiie.Isbm.Client;
using RegLocationEngine.Infrastructure.RegLocation;

namespace SimHost.Tests.Sc01;

/// <summary>
/// An ISBM broker that keeps messages in a dictionary.
///
/// Enough of the broker to make routing observable, and no more: a publication
/// lands under the channel URI it was posted to, and a subscriber reads only
/// from the channel it opened against. That is precisely the property SC01
/// depends on and the one a mismatched URI breaks, so a fake that ignored the
/// URI would pass whether or not the topology worked.
///
/// Topics are recorded but not filtered on. The engines assert on them
/// separately, and a fake that also implemented topic matching would be
/// asserting on its own behaviour.
/// </summary>
internal sealed class FakeIsbmBroker : IIsbmClient
{
    private readonly Dictionary<string, List<IsbmMessage>> _channels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionChannels = new(StringComparer.Ordinal);

    /// <summary>Channels a session was opened against, for assertions.</summary>
    public List<string> PublicationChannels { get; } = [];
    public List<string> SubscriptionChannels { get; } = [];

    /// <summary>Topics each publication was posted under, for assertions.</summary>
    public List<IReadOnlyList<string>> PostedTopics { get; } = [];
    public List<IReadOnlyList<string>> SubscribedTopics { get; } = [];

    public IReadOnlyList<IsbmMessage> MessagesOn(string channelUri) =>
        _channels.TryGetValue(channelUri, out var queue) ? queue : [];

    public Task<string> OpenPublicationSessionAsync(string channelUri, CancellationToken ct = default)
    {
        PublicationChannels.Add(channelUri);

        var sessionId = $"pub-{Guid.NewGuid():N}";
        _sessionChannels[sessionId] = channelUri;

        return Task.FromResult(sessionId);
    }

    public Task<string> PostPublicationAsync(
        string sessionId, XElement content, IReadOnlyList<string> topics,
        DateTimeOffset? expiry = null, CancellationToken ct = default)
    {
        var channelUri = _sessionChannels[sessionId];
        var messageId = $"msg-{Guid.NewGuid():N}";

        PostedTopics.Add(topics);

        if (!_channels.TryGetValue(channelUri, out var queue))
        {
            _channels[channelUri] = queue = [];
        }

        queue.Add(new IsbmMessage(messageId, content, content.ToString(), topics));

        return Task.FromResult(messageId);
    }

    public Task<string> OpenSubscriptionSessionAsync(
        string channelUri, IReadOnlyList<string> topics, CancellationToken ct = default,
        string? subscriberId = null)
    {
        SubscriptionChannels.Add(channelUri);
        SubscribedTopics.Add(topics);

        var sessionId = $"sub-{Guid.NewGuid():N}";
        _sessionChannels[sessionId] = channelUri;

        return Task.FromResult(sessionId);
    }

    public Task<IsbmMessage?> ReadPublicationAsync(string sessionId, CancellationToken ct = default)
    {
        var channelUri = _sessionChannels[sessionId];

        // Head of the queue without removing it, matching the real broker: the
        // reader removes explicitly once it has filed everything in the message.
        var message = _channels.TryGetValue(channelUri, out var queue) && queue.Count > 0
            ? queue[0]
            : null;

        return Task.FromResult(message);
    }

    public Task RemovePublicationAsync(string sessionId, CancellationToken ct = default)
    {
        var channelUri = _sessionChannels[sessionId];

        if (_channels.TryGetValue(channelUri, out var queue) && queue.Count > 0)
        {
            queue.RemoveAt(0);
        }

        return Task.CompletedTask;
    }

    // --- Not exercised by SC01 ---------------------------------------------

    public Task<IsbmChannel> CreateChannelAsync(
        string channelUri, IsbmChannelType channelType, string? description = null,
        IReadOnlyList<string>? securityTokens = null, CancellationToken ct = default)
    {
        _channels.TryAdd(channelUri, []);
        return Task.FromResult(new IsbmChannel(channelUri, channelType, description));
    }

    public Task<IsbmChannel?> GetChannelAsync(string channelUri, CancellationToken ct = default) =>
        Task.FromResult<IsbmChannel?>(
            _channels.ContainsKey(channelUri)
                ? new IsbmChannel(channelUri, IsbmChannelType.Publication, null)
                : null);

    public Task<IReadOnlyList<IsbmChannel>> GetChannelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IsbmChannel>>(
            [.. _channels.Keys.Select(u => new IsbmChannel(u, IsbmChannelType.Publication, null))]);

    public Task DeleteChannelAsync(string channelUri, CancellationToken ct = default)
    {
        _channels.Remove(channelUri);
        return Task.CompletedTask;
    }

    public Task<string> OpenConsumerRequestSessionAsync(string channelUri, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<string> PostRequestAsync(
        string sessionId, XElement content, IReadOnlyList<string> topics,
        DateTimeOffset? expiry = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IsbmMessage?> ReadResponseAsync(
        string sessionId, string requestMessageId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task RemoveResponseAsync(
        string sessionId, string requestMessageId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<string> OpenProviderRequestSessionAsync(
        string channelUri, IReadOnlyList<string> topics, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IsbmMessage?> ReadRequestAsync(string sessionId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task PostResponseAsync(
        string sessionId, string requestMessageId, XElement content, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task RemoveRequestAsync(string sessionId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<bool> SessionExistsAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult(_sessionChannels.ContainsKey(sessionId));

    public Task CloseSessionAsync(IsbmSessionKind kind, string sessionId, CancellationToken ct = default)
    {
        _sessionChannels.Remove(sessionId);
        return Task.CompletedTask;
    }
}

/// <summary>ENG with one iModel, one marker and the elements the test supplies.</summary>
internal sealed class FakeEngClient(
    Guid iModelId, Guid iTwinId, EngNamedVersion marker, IReadOnlyList<EngElement> elements)
    : IEngClient
{
    public Task<EngIModel?> GetIModelAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<EngIModel?>(
            id == iModelId
                ? new EngIModel(iModelId, iTwinId, "TEST-MODEL", "Test iModel", DateTime.UtcNow)
                : null);

    public Task<IReadOnlyList<EngITwin>> GetITwinsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EngITwin>>([]);

    public Task<IReadOnlyList<EngNamedVersion>> GetNamedVersionsAsync(
        Guid id, DateTime? modifiedSince, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EngNamedVersion>>(id == iModelId ? [marker] : []);

    public Task<IReadOnlyList<EngElement>> GetNamedVersionElementsAsync(
        long namedVersionId, CancellationToken ct) =>
        Task.FromResult(namedVersionId == marker.NamedVersionId ? elements : []);
}

/// <summary>Engine watermark state, held in memory.</summary>
internal sealed class FakeEngStateStore : IEngEngineStateStore
{
    private EngEngineState _state = new();

    public Task<(EngEngineState State, ETag ETag)> ReadAsync(CancellationToken ct) =>
        Task.FromResult((_state, new ETag("*")));

    public Task<bool> TryWriteAsync(EngEngineState state, ETag expected, CancellationToken ct)
    {
        _state = state;
        return Task.FromResult(true);
    }

    public Task ClearAsync(CancellationToken ct)
    {
        _state = new EngEngineState();
        return Task.CompletedTask;
    }
}

/// <summary>
/// REG-LOCATION as a registry that records what was filed into it.
///
/// Scopes are pre-seeded rather than created on demand, because SC01's inbound
/// leg deliberately never creates one: SyncSites establishes the scope, and a
/// fake that minted one here would hide a segment being filed against a site
/// the registry does not have.
/// </summary>
internal sealed class FakeRegLocationClient(Dictionary<Guid, int> scopesByGuid) : IRegLocationClient
{
    private int _nextTagId = 1;

    // Tag id -> the request currently backing it. Mutable so a correction
    // overwrites the row in place, the same way REG-LOCATION's UPDATE does.
    private readonly Dictionary<int, CreateTagRequest> _tags = [];

    public List<CreateTagRequest> Created { get; } = [];

    public List<(int TagId, UpdateTagRequest Request)> Corrected { get; } = [];

    public Task<IReadOnlyList<RegTagDetail>> FindTagsByGuidAsync(Guid guid, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RegTagDetail>>(
            [.. _tags
                .Where(kv => kv.Value.Guid == guid)
                .Select(kv => Detail(kv.Key, kv.Value))]);

    public Task<RegTagDetail> CreateTagAsync(CreateTagRequest request, CancellationToken ct)
    {
        Created.Add(request);
        var tagId = _nextTagId++;
        _tags[tagId] = request;
        return Task.FromResult(Detail(tagId, request));
    }

    public Task<RegTagDetail?> UpdateTagAsync(int tagId, UpdateTagRequest request, CancellationToken ct)
    {
        if (!_tags.TryGetValue(tagId, out var existing))
            return Task.FromResult<RegTagDetail?>(null);

        Corrected.Add((tagId, request));

        var updated = existing with
        {
            ClassId = request.ClassId,
            Code = request.Code,
            Revision = request.Revision,
            Name = request.Name
        };

        _tags[tagId] = updated;
        return Task.FromResult<RegTagDetail?>(Detail(tagId, updated));
    }

    public Task<RegScope?> FindScopeByGuidAsync(Guid guid, CancellationToken ct) =>
        Task.FromResult(
            scopesByGuid.TryGetValue(guid, out var scopeId)
                ? new RegScope(scopeId, "Test Site", 1, null, null, null, true, 0)
                : null);

    private static RegTagDetail Detail(int tagId, CreateTagRequest r) =>
        new(new RegTag(tagId, r.ItemId, r.ClassId, r.Code, r.Revision, r.Name, r.State ?? "Proposed"),
            new RegObject(tagId, 1, r.Guid, r.ScopeId, 0, 0, null, null, null, null));

    // --- Not exercised by the inbound leg ----------------------------------

    public Task<RegTagDetail?> GetTagAsync(int tagId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<RegTagDetail>> GetApprovedTagsAsync(int? scopeId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RegScope> CreateScopeAsync(CreateScopeRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<ScopeCascadeResult?> DeleteScopeCascadeAsync(int scopeId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task SetScopeContextAsync(int scopeId, SetScopeContextRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RegItem?> FindItemByGuidAsync(Guid guid, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RegItem> CreateItemAsync(CreateItemRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RegSerialDetail?> FindSerialByGuidAsync(Guid guid, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RegSerialDetail> CreateSerialAsync(CreateSerialRequest request, CancellationToken ct) =>
        throw new NotSupportedException();
}
