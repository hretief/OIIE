namespace SimHost.Application.Participants;

/// <summary>
/// Everything that makes one configured participant. Instantiated once per
/// personality at startup — a participant is a configuration plus mappers, not a
/// separate codebase (spec §3.1).
/// </summary>
public sealed class ParticipantContext
{
    public ParticipantContext(PersonalityConfig config)
    {
        Config = config;
    }

    public PersonalityConfig Config { get; }

    public string ParticipantId => Config.ParticipantId;
}

public sealed class ParticipantRegistry
{
    private readonly Dictionary<string, ParticipantContext> _participants;

    public ParticipantRegistry(IEnumerable<PersonalityConfig> configs)
    {
        _participants = configs
            .Select(c => new ParticipantContext(c))
            .ToDictionary(p => p.ParticipantId, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ParticipantContext> All => _participants.Values;

    public ParticipantContext Get(string participantId) =>
        _participants.TryGetValue(participantId, out var value)
            ? value
            : throw new KeyNotFoundException($"No participant configured with id '{participantId}'.");

    public bool TryGet(string participantId, out ParticipantContext? context)
    {
        var found = _participants.TryGetValue(participantId, out var value);
        context = value;
        return found;
    }
}
