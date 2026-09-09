namespace RdlEngine.Application;

/// <summary>
/// Channel settings for the RDL request listener.
///
/// Separate from <see cref="Oiie.Isbm.Client.IsbmClientOptions"/>, which carries
/// only connection details. This adds what the listener needs: which channel to
/// read, and whether to read at all.
/// </summary>
public sealed class RdlIsbmOptions
{
    /// <summary>Base URL of the ws-ISBM Service Provider. Mirrors IsbmClientOptions.BaseUrl.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Channel carrying GetTaxonomySet requests.</summary>
    public string RequestChannelUri { get; set; } = "/OIIE/RDL/Request";

    /// <summary>
    /// Topics to open the session against.
    ///
    /// Deliberately empty by default: configuration binding ADDS to an existing
    /// collection rather than replacing it, so a non-empty initialiser plus an
    /// Isbm__Topics__0 setting yields a duplicated list.
    /// <see cref="EffectiveTopics"/> supplies the fallback instead.
    /// </summary>
    public List<string> Topics { get; set; } = [];

    public const string DefaultTopic = "OIIE:S35:V1.0/CCOM:GetTaxonomySet:R1.0";

    /// <summary>Configured topics, de-duplicated, falling back to the default.</summary>
    public IReadOnlyList<string> EffectiveTopics =>
        Topics.Count == 0 ? [DefaultTopic] : Topics.Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Set false to keep the listener dormant, e.g. before the channel exists.</summary>
    public bool Enabled { get; set; }

    /// <summary>Messages drained per tick. Bounded so one tick cannot run past the timer.</summary>
    public int MaxMessagesPerPoll { get; set; } = 20;
}
