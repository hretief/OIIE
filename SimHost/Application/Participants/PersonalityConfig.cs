namespace SimHost.Application.Participants;

public enum ReleaseMode { Manual, Auto }

public enum ChannelRole { Publisher, Subscriber, RequestProvider, RequestConsumer }

public sealed class PersonalityConfig
{
    /// <summary>Route segment and SQL schema name, e.g. "reg-asset" / "reg_asset".</summary>
    public string ParticipantId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>SQL schema. Defaults to ParticipantId with hyphens replaced.</summary>
    public string? Schema { get; set; }

    /// <summary>CIR Entry.SourceID for objects this participant owns.</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>CIR Entry.SourceOwnerID — the organisation being impersonated.</summary>
    public string SourceOwnerId { get; set; } = string.Empty;

    /// <summary>OAGIS ApplicationArea/Sender/LogicalID.</summary>
    public string LogicalId { get; set; } = string.Empty;

    /// <summary>Accent colour so tiled windows are distinguishable at a glance.</summary>
    public string AccentColour { get; set; } = "#4b5563";

    public ReleaseMode ReleaseMode { get; set; } = ReleaseMode.Manual;

    /// <summary>Identifier generator key, e.g. "isa-tag", "serial", "surrogate", "numeric".</summary>
    public string IdentifierStyle { get; set; } = "surrogate";

    public List<ChannelBinding> Channels { get; set; } = [];

    public IsbmCredentials Isbm { get; set; } = new();

    public CirSettings Cir { get; set; } = new();

    public string ResolvedSchema =>
        Schema ?? ParticipantId.Replace('-', '_');
}

public sealed class ChannelBinding
{
    public string ChannelUri { get; set; } = string.Empty;
    public ChannelRole Role { get; set; }
    public List<string> Topics { get; set; } = [];

    /// <summary>Push when a listener URI is configured; polling otherwise.</summary>
    public bool UseNotifications { get; set; }
}

public sealed class IsbmCredentials
{
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Key Vault secret name. Never a literal value.</summary>
    public string? TokenSecretName { get; set; }
}

public sealed class CirSettings
{
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// ISBM request channel the CIR provider listens on. Registration and resolution
    /// travel over the bus as Annex A BODs, so a participant needs one integration
    /// mechanism rather than two.
    ///
    /// Owned by the CIR provider, not by the Sandbox. Reset therefore ensures it
    /// exists but never deletes it: removing it would destroy the provider's
    /// long-lived session and stop it consuming until it was restarted.
    /// </summary>
    public string ChannelUri { get; set; } = string.Empty;

    /// <summary>
    /// Topic the CIR provider's request listener subscribes to.
    ///
    /// A single topic for the whole BOD family rather than one per BOD name. Topic
    /// per BOD would need the subscriber to enumerate all eleven request BODs, and a
    /// missing one fails silently — the request is accepted and never delivered. The
    /// provider's dispatcher already routes on the root element name, so the topic
    /// only has to get the message to it.
    /// </summary>
    public string RequestTopic { get; set; } = "ws-CIR";

    /// <summary>
    /// How long to wait for a response.
    ///
    /// Generous on purpose. The provider polls its request channel on a timer, and
    /// on a Consumption plan a cold app has to be woken by the scale controller
    /// first — so the first request after an idle period can take far longer than
    /// the poll interval suggests.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public string RegistryId { get; set; } = "OIIE-SANDBOX";

    /// <summary>Identity cache TTL. Short in demo mode so staleness is observable.</summary>
    public TimeSpan IdentityCacheTtl { get; set; } = TimeSpan.FromMinutes(5);
}
