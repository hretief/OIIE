using Microsoft.Extensions.Configuration;
using Oiie.Isbm.Client;
using SimHost.Application.Participants;
using SimHost.Application.Topology;
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
    TopologyRegistry topology,
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

        results.AddRange(await EnsureTopologyAsync(results, ct));

        return results;
    }

    /// <summary>
    /// Creates the enterprise-wide channels the topology declares.
    ///
    /// This is what makes a new scenario a file drop: declaring a channel in a
    /// topology file is enough to have it provisioned on the next start, with
    /// no personality pack edit and no code change.
    ///
    /// Per-iTwin channels are skipped here, because the twin ids are not known
    /// until an operator registers one -- those go through
    /// <see cref="EnsureForITwinAsync"/> instead.
    ///
    /// Channels already provisioned from a personality binding are skipped
    /// rather than created twice. Creation is idempotent, so this is about the
    /// report rather than the broker: the same channel listed twice with
    /// different owning participants reads like a misconfiguration.
    /// </summary>
    private async Task<IReadOnlyList<ChannelEnsureResult>> EnsureTopologyAsync(
        IReadOnlyList<ChannelEnsureResult> already, CancellationToken ct)
    {
        var results = new List<ChannelEnsureResult>();
        var enterprise = configuration["EngEngine:Enterprise"] ?? "acme";

        var seen = new HashSet<string>(
            already.Select(r => r.ChannelUri), StringComparer.Ordinal);

        foreach (var scenario in topology.Scenarios)
        {
            foreach (var channel in scenario.Channels)
            {
                if (channel.IsPerITwin)
                {
                    continue;
                }

                var uri = channel.Resolve(enterprise);

                if (!seen.Add(uri))
                {
                    continue;
                }

                // Created under the publisher's credentials: the participant
                // that posts to a channel is the one that must be able to.
                // Falling back to the first participant would provision a
                // channel the publisher has no rights on, which fails later at
                // the publish rather than here.
                if (!registry.All.Any(p => string.Equals(
                        p.ParticipantId, channel.Publisher, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(new ChannelEnsureResult(
                        channel.Publisher, uri, channel.Type, false,
                        $"Scenario {scenario.ScenarioId} names publisher '{channel.Publisher}', "
                        + "which is not a registered participant."));
                    continue;
                }

                var type = string.Equals(channel.Type, "Request", StringComparison.OrdinalIgnoreCase)
                    ? IsbmChannelType.Request
                    : IsbmChannelType.Publication;

                results.Add(await CreateAsync(
                    clients.For(channel.Publisher), channel.Publisher, uri, type,
                    $"OIIE Sandbox: {scenario.ScenarioId} {channel.Description}".TrimEnd(), ct));
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
        var enterprise = configuration["EngEngine:Enterprise"] ?? "acme";
        var domain = configuration["EngEngine:Domain"] ?? "engineering";

        var label = description is { Length: > 0 }
            ? $"OIIE Sandbox: {description}"
            : "OIIE Sandbox: iTwin publication";

        // Every per-iTwin channel the topology declares, deduplicated: SC01 and
        // SC02 both run on the engineering publication channel, in opposite
        // directions, and it is one channel.
        var declared = topology.AllChannels()
            .Where(c => c.IsPerITwin)
            .Select(c => (Uri: c.Resolve(enterprise, iTwinId), c.Type, c.Publisher))
            .GroupBy(c => c.Uri, StringComparer.Ordinal)
            .ToList();

        ChannelEnsureResult? conventional = null;

        foreach (var group in declared)
        {
            var first = group.First();
            var publisher = registry.All.Any(p => string.Equals(
                p.ParticipantId, first.Publisher, StringComparison.OrdinalIgnoreCase))
                ? first.Publisher
                : registry.All.First().ParticipantId;

            var type = string.Equals(first.Type, "Request", StringComparison.OrdinalIgnoreCase)
                ? IsbmChannelType.Request
                : IsbmChannelType.Publication;

            var result = await CreateAsync(
                clients.For(publisher), publisher, group.Key, type, label, ct);

            // The conventional channel is the one callers expect back, so it is
            // singled out from what may be several.
            if (string.Equals(group.Key, $"/{enterprise}/{iTwinId:D}/{domain}/publication",
                    StringComparison.Ordinal))
            {
                conventional = result;
            }
        }

        if (conventional is not null)
        {
            return conventional;
        }

        // No topology file declared a per-iTwin channel, so fall back to the
        // convention. Keeps twin registration working on a deployment whose
        // topology directory is empty, which is the same fallback the engines
        // make.
        var participant = registry.All.First();

        return await CreateAsync(
            clients.For(participant.ParticipantId), participant.ParticipantId,
            $"/{enterprise}/{iTwinId:D}/{domain}/publication",
            IsbmChannelType.Publication, label, ct);
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
