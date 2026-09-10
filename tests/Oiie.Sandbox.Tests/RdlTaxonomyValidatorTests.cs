using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using EngEngine.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Oiie.Ccom.Extensions;
using Oiie.Isbm.Client;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// ENG checking its own outbound map against the reference library.
///
/// The map is a claim about someone else's data: it says "rdl:LightingUnit is a
/// class RDL holds". Nothing was testing that claim, and a wrong one is
/// invisible -- the segment publishes, the BOD validates, and only a subscriber
/// trying to resolve the type ever finds out.
///
/// These tests pin the three answers that matter and, in particular, that they
/// stay distinguishable: verified-and-clean, verified-and-missing, and
/// could-not-verify. Collapsing the last two would let RDL being down read as a
/// healthy mapping, which is the failure this whole path exists to prevent.
/// </summary>
public class RdlTaxonomyValidatorTests
{
    private const string LightingUnit = "rdl:LightingUnit";

    [Fact]
    public async Task Key_the_library_holds_is_reported_as_verified()
    {
        var validator = Validator(
            new FakeRdlResponder([LightingUnit, "rdl:Equipment"]),
            map: new() { ["ENG.Streetlight"] = LightingUnit });

        var result = await validator.ValidateAsync();

        Assert.True(result.Checked);
        Assert.Empty(result.MissingKeys);
    }

    [Fact]
    public async Task Key_the_library_does_not_hold_is_reported_as_missing()
    {
        // The realistic defect: a plausible key that is simply not there.
        var validator = Validator(
            new FakeRdlResponder([LightingUnit, "rdl:Equipment"]),
            map: new()
            {
                ["ENG.Streetlight"] = LightingUnit,
                ["ENG.Culvert"] = "rdl:Culvert"
            });

        var result = await validator.ValidateAsync();

        Assert.True(result.Checked);
        Assert.Equal(["rdl:Culvert"], result.MissingKeys);
    }

    [Fact]
    public async Task Silent_provider_is_reported_as_unverified_rather_than_clean()
    {
        // Distinct from a clean result on purpose. A validator that returned
        // "no missing keys" here would report a dead RDL as a healthy mapping.
        var validator = Validator(
            new FakeRdlResponder(codes: null),
            map: new() { ["ENG.Streetlight"] = LightingUnit });

        var result = await validator.ValidateAsync();

        Assert.False(result.Checked);
        Assert.Empty(result.MissingKeys);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task Library_is_fetched_once_and_reused_across_drains()
    {
        // The cache is the reason this is a singleton: without it every drain
        // would ask the same question at the poll interval.
        var broker = new FakeRdlResponder([LightingUnit]);
        var validator = Validator(broker, map: new() { ["ENG.Streetlight"] = LightingUnit });

        await validator.ValidateAsync();
        await validator.ValidateAsync();
        await validator.ValidateAsync();

        Assert.Equal(1, broker.RequestCount);
    }

    [Fact]
    public async Task Disabled_validator_does_not_contact_the_broker()
    {
        var broker = new FakeRdlResponder([LightingUnit]);

        var validator = Validator(
            broker,
            map: new() { ["ENG.Streetlight"] = LightingUnit },
            enabled: false);

        var result = await validator.ValidateAsync();

        Assert.False(result.Checked);
        Assert.Equal(0, broker.RequestCount);
    }

    [Fact]
    public async Task Session_is_closed_even_when_the_provider_never_answers()
    {
        // A session leaked per failed pass accumulates on the broker for as
        // long as RDL stays down, which is exactly when nobody is looking.
        var broker = new FakeRdlResponder(codes: null);

        var validator = Validator(broker, map: new() { ["ENG.Streetlight"] = LightingUnit });

        await validator.ValidateAsync();

        Assert.Equal(broker.OpenedSessions, broker.ClosedSessions);
    }

    private static RdlTaxonomyValidator Validator(
        FakeRdlResponder broker,
        Dictionary<string, string> map,
        bool enabled = true) =>
        new(broker,
            Options.Create(new EngRdlOptions
            {
                Enabled = enabled,
                // Short enough that the no-answer test does not stall the suite.
                ResponseTimeout = TimeSpan.FromMilliseconds(50),
                PollInterval = TimeSpan.FromMilliseconds(10)
            }),
            Options.Create(new EngEngineOptions
            {
                ParticipantId = "eng",
                OutboundRdlClassMap = new Dictionary<string, string>(
                    map, StringComparer.OrdinalIgnoreCase)
            }),
            NullLogger<RdlTaxonomyValidator>.Instance);
}

/// <summary>
/// An RDL that answers GetTaxonomySet with a fixed class list, or not at all.
///
/// Answers by actually building a ShowTaxonomySet through
/// <see cref="TaxonomySetBods"/> rather than by handing back a canned object,
/// so the round trip under test includes the real serialisation and parsing.
/// A fake that shortcut that would pass while the wire format drifted.
/// </summary>
internal sealed class FakeRdlResponder(IReadOnlyList<string>? codes) : IIsbmClient
{
    public int RequestCount { get; private set; }
    public int OpenedSessions { get; private set; }
    public int ClosedSessions { get; private set; }

    public Task<string> OpenConsumerRequestSessionAsync(
        string channelUri, CancellationToken ct = default)
    {
        OpenedSessions++;
        return Task.FromResult($"session-{OpenedSessions}");
    }

    public Task<string> PostRequestAsync(
        string sessionId,
        XElement content,
        IReadOnlyList<string> topics,
        DateTimeOffset? expiry = null,
        CancellationToken ct = default)
    {
        RequestCount++;

        // The request must be a GetTaxonomySet a real responder would accept;
        // asserting here keeps the fake honest about what it is answering.
        var request = TaxonomySetBods.ParseGetTaxonomySet(new XDocument(content));
        Assert.NotNull(request);

        return Task.FromResult($"request-{RequestCount}");
    }

    public Task<IsbmMessage?> ReadResponseAsync(
        string sessionId, string requestMessageId, CancellationToken ct = default)
    {
        if (codes is null) return Task.FromResult<IsbmMessage?>(null);

        var document = TaxonomySetBods.ShowTaxonomySet(
            setShortName: "rdl",
            classes: [.. codes.Select(c => new RdlClass { Code = c })],
            senderLogicalId: "RDL",
            correlationId: Guid.NewGuid().ToString("D"),
            originalBodId: requestMessageId);

        return Task.FromResult<IsbmMessage?>(new IsbmMessage(
            $"response-{requestMessageId}", document.Root, document.ToString(), []));
    }

    public Task RemoveResponseAsync(
        string sessionId, string requestMessageId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task CloseSessionAsync(
        IsbmSessionKind kind, string sessionId, CancellationToken ct = default)
    {
        ClosedSessions++;
        return Task.CompletedTask;
    }

    // --- Not exercised by these tests ---------------------------------------

    public Task<IsbmChannel> CreateChannelAsync(
        string channelUri,
        IsbmChannelType channelType,
        string? description = null,
        IReadOnlyList<string>? securityTokens = null,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IsbmChannel?> GetChannelAsync(string channelUri, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<IsbmChannel>> GetChannelsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task DeleteChannelAsync(string channelUri, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<string> OpenPublicationSessionAsync(
        string channelUri, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<string> PostPublicationAsync(
        string sessionId,
        XElement content,
        IReadOnlyList<string> topics,
        DateTimeOffset? expiry = null,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task<string> OpenSubscriptionSessionAsync(
        string channelUri,
        IReadOnlyList<string> topics,
        CancellationToken ct = default,
        string? subscriberId = null) => throw new NotSupportedException();

    public Task<IsbmMessage?> ReadPublicationAsync(string sessionId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task RemovePublicationAsync(string sessionId, CancellationToken ct = default) =>
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
        throw new NotSupportedException();
}
