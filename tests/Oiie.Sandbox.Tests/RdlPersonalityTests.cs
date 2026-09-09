using RdlEngine.Application;
using SimHost.Application.Participants;
using Xunit;

namespace Oiie.Sandbox.Tests;

/// <summary>
/// Guards the RDL request channel declaration against drift.
///
/// The channel URI and topic exist in two places that cannot read each other:
/// the personality pack, which IsbmChannelProvisioner uses to CREATE the
/// channel, and RdlEngine's own configuration, which it uses to OPEN a session
/// on it. They are separate deployables, so the duplication is not removable.
///
/// A divergence between them fails silently in the worst way. The provisioner
/// creates one channel, the engine subscribes to another, both report success,
/// and the engine drains cleanly forever while every request sits unanswered on
/// a channel nobody reads. Nothing in the logs says so -- an empty drain is
/// indistinguishable from no traffic. These tests are what makes that loud.
/// </summary>
public class RdlPersonalityTests
{
    private static PersonalityConfig Rdl()
    {
        var packs = Path.Combine(
            RepoRoot(), "Oiie.Sandbox.Core", "PersonalityPacks");

        var config = PersonalityLoader.LoadAll(packs)
            .SingleOrDefault(p => p.ParticipantId == "rdl");

        Assert.NotNull(config);
        return config;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenOM.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    /// <summary>
    /// RequestProvider is what makes the provisioner create a Request channel
    /// rather than a Publication. A Publication channel cannot carry a
    /// provider-request session, so the role being wrong surfaces as a session
    /// failure that names the channel but not the reason.
    /// </summary>
    [Fact]
    public void Rdl_binds_its_request_channel_as_a_request_provider()
    {
        var binding = Assert.Single(Rdl().Channels);

        Assert.Equal(ChannelRole.RequestProvider, binding.Role);
    }

    [Fact]
    public void The_personality_channel_uri_matches_the_engine_default()
    {
        var binding = Assert.Single(Rdl().Channels);

        Assert.Equal(new RdlIsbmOptions().RequestChannelUri, binding.ChannelUri);
    }

    [Fact]
    public void The_personality_topic_matches_the_engine_default()
    {
        var binding = Assert.Single(Rdl().Channels);

        Assert.Equal(RdlIsbmOptions.DefaultTopic, Assert.Single(binding.Topics));
    }

    /// <summary>
    /// The provisioner creates channels under the owning participant's own
    /// credentials, so RDL needs an ISBM endpoint of its own. Without one the
    /// channel is never created and the failure appears later, at the engine.
    /// </summary>
    [Fact]
    public void Rdl_has_an_isbm_endpoint_to_provision_against()
    {
        Assert.False(string.IsNullOrWhiteSpace(Rdl().Isbm.BaseUrl));
    }
}
