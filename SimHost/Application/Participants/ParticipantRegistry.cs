using SimHost.Application.Classification;

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
        ClassificationSource = new InMemoryClassificationSource([], [], [], []);
        Resolver = new ClassificationResolver(ClassificationSource);
        Binder = new ClassBinder(ClassificationSource);
        Ingestor = new PropertyIngestor(ClassificationSource);
    }

    public PersonalityConfig Config { get; }

    public string ParticipantId => Config.ParticipantId;

    public string Schema => Config.ResolvedSchema;

    public IClassificationSource ClassificationSource { get; private set; }

    public ClassificationResolver Resolver { get; private set; }

    public ClassBinder Binder { get; private set; }

    public PropertyIngestor Ingestor { get; private set; }

    /// <summary>
    /// Rebuilt when definitions change — including when they arrive over the bus
    /// from the RDL participant, which is what clears unmapped chips without any
    /// data being re-sent.
    /// </summary>
    public void RefreshClassification(IClassificationSource source)
    {
        ClassificationSource = source;
        Resolver = new ClassificationResolver(source);
        Binder = new ClassBinder(source);
        Ingestor = new PropertyIngestor(source);
    }
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
