using SimHost.Application.Participants;

namespace SimHost.Application.Bods;

/// <summary>
/// The mapper depends on a participant's classification source, which is rebuilt
/// whenever definitions change — including when they arrive over the bus. Resolving
/// it per call rather than holding one instance is what lets a definition published
/// by the RDL take effect without a restart.
/// </summary>
public sealed class CcomAttributeMapperFactory
{
    public CcomAttributeMapper For(ParticipantContext participant) =>
        new(participant.ClassificationSource);
}
