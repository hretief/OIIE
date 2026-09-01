using Microsoft.Extensions.Configuration;
using Oiie.Isbm.Client;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Infrastructure.Isbm;

namespace Oiie.Sandbox.Api.Services;

/// <summary>
/// The outcome of trying to create one channel. Carries the failure rather than
/// throwing, because one unreachable channel should not stop the rest from being
/// provisioned: a partial result that says which channel failed is more useful
/// than an exception that says only that something did.
/// </summary>
public sealed record ChannelEnsureResult(
    string ParticipantId,
    string ChannelUri,
    string ChannelType,
    bool Created,
    string? Error = null);

/// <summary>
/// Creates every channel the registry says a participant is bound to.
///
/// Extracted from the /admin/isbm/channels/ensure handler so that startup and the
/// endpoint run the same code. They had drifted apart in practice: the endpoint
/// existed but nothing called it, so a deployment whose channels had never been
/// created failed at the first publish with a 404 that named the channel but not
/// the reason. Provisioning is idempotent, so running it on every start is safe
/// and makes the missing-channel case unreachable rather than merely recoverable.
/// </summary>
public sealed class IsbmChannelProvisioner(
    ParticipantRegistry registry,
    IIsbmClientAccessor clients,
    IConfiguration configuration)
{
    public async Task<IReadOnlyList<ChannelEnsureResult>> EnsureAllAsync(CancellationToken ct = default)
    {
        var results = new List<ChannelEnsureResult>();

        foreach (var participant in registry.All)
        {
            var client = clients.For(participant.ParticipantId);

            // Several participants may bind the same channel in different roles, and a
            // channel is created once regardless of how many bind it.
            var channels = participant.Config.Channels
                .GroupBy(c => c.ChannelUri, StringComparer.Ordinal)
                .ToList();

            // The CIR channel is not a peer binding, so it is not in Channels — but it
            // still has to exist before anything can register.
            var cirChannel = participant.Config.Cir.ChannelUri;

            foreach (var group in channels)
            {
                var isRequestChannel = group.Any(c =>
                    c.Role is ChannelRole.RequestProvider or ChannelRole.RequestConsumer);

                var type = isRequestChannel ? IsbmChannelType.Request : IsbmChannelType.Publication;

                results.Add(await CreateAsync(
                    client, participant.ParticipantId, group.Key, type,
                    $"OIIE Sandbox: {participant.ParticipantId}", ct));
            }

            if (!string.IsNullOrWhiteSpace(cirChannel)
                && !channels.Any(g => string.Equals(g.Key, cirChannel, StringComparison.Ordinal)))
            {
                results.Add(await CreateAsync(
                    client, participant.ParticipantId, cirChannel, IsbmChannelType.Request,
                    "OIIE Sandbox: ws-CIR", ct));
            }
        }

        return results;
    }

    /// <summary>
    /// Creates the publication channel belonging to one iTwin.
    ///
    /// Needed because <see cref="EnsureAllAsync"/> can only provision what the
    /// participant registry declares, and the registry is configuration read at
    /// startup. A twin added by an operator at runtime is therefore invisible to
    /// it: the twin registers, the carousel shows it, and the first publication
    /// fails against a channel nobody created. Provisioning at the moment the
    /// twin is added is what closes that gap.
    ///
    /// The URI follows the OIIE convention:
    ///
    ///     /{enterprise}/{itwin-federation-id}/{domain}/publication
    ///
    /// which is the same shape EngEngine derives in ChannelUriFor. The settings
    /// are read from EngEngine's own keys rather than a Sandbox-local copy, so
    /// the two cannot drift into creating and publishing to different URIs --
    /// a disagreement that would look like a successful publish on both sides
    /// while nothing was ever delivered.
    /// </summary>
    public async Task<ChannelEnsureResult> EnsureForITwinAsync(
        Guid iTwinId, string? description = null, CancellationToken ct = default)
    {
        var participant = registry.All.First();
        var client = clients.For(participant.ParticipantId);

        var enterprise = configuration["EngEngine:Enterprise"] ?? "acme";
        var domain = configuration["EngEngine:Domain"] ?? "engineering";

        var uri = $"/{enterprise}/{iTwinId:D}/{domain}/publication";

        return await CreateAsync(
            client, participant.ParticipantId, uri, IsbmChannelType.Publication,
            description is { Length: > 0 } ? $"OIIE Sandbox: {description}" : "OIIE Sandbox: iTwin publication",
            ct);
    }

    private static async Task<ChannelEnsureResult> CreateAsync(
        IIsbmClient client,
        string participantId,
        string channelUri,
        IsbmChannelType type,
        string description,
        CancellationToken ct)
    {
        try
        {
            await client.CreateChannelAsync(channelUri, type, description, null, ct);
            return new ChannelEnsureResult(participantId, channelUri, type.ToString(), true);
        }
        catch (Exception ex)
        {
            return new ChannelEnsureResult(participantId, channelUri, type.ToString(), false, ex.Message);
        }
    }
}
