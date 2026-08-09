using SimHost.Application.Participants;

namespace SimHost.Application.Scenarios;

/// <summary>
/// One participant's persisted rows for a set of identities, read from its own schema.
/// </summary>
/// <param name="Records">
/// The rows actually present in that participant's SQL store. Empty is meaningful: it
/// says the participant holds nothing for these identities, which for a downstream
/// participant means the BOD either never arrived or produced no row.
/// </param>
public sealed record ParticipantData(
    string ParticipantId,
    IReadOnlyList<RecordView> Records,
    string? Error);

/// <summary>
/// What each participant persisted, for the identities a run put into circulation.
///
/// This exists because an assertion is a claim and a row is evidence. "MMS received the
/// message" passing tells you the engine was satisfied; it does not put the equipment
/// number, the designation and the carried FederationId in front of someone. This reads
/// the MMS schema directly and shows what is in it.
///
/// Identities are taken from ENG rather than from the BOD, because ENG is where they are
/// minted: starting there means a participant that received nothing still appears, with
/// the identity it should have had and no row against it.
/// </summary>
public sealed class RunDataService(
    ParticipantRegistry registry,
    MessageTransformService transforms,
    ILogger<RunDataService> logger)
{
    /// <summary>Flow order, so the tab reads ENG to MMS as the data actually travelled.</summary>
    private static readonly string[] FlowOrder = ["eng", "reg-location", "mms"];

    /// <summary>
    /// Reads every participant's rows for the identities seen in this run.
    /// </summary>
    /// <param name="identities">
    /// Usually the FederationIds from the run's lineage. When empty the result is empty
    /// rather than every row in every store, since an unfiltered dump would not be
    /// evidence about this run.
    /// </param>
    public async Task<IReadOnlyList<ParticipantData>> GetAsync(
        IReadOnlyList<Guid> identities, CancellationToken ct = default)
    {
        if (identities.Count == 0)
        {
            return [];
        }

        var ordered = registry.All
            .Select(p => p.ParticipantId)
            .OrderBy(id => Array.IndexOf(FlowOrder, id) is var i && i >= 0 ? i : int.MaxValue)
            .ToList();

        var result = new List<ParticipantData>();

        foreach (var participantId in ordered)
        {
            try
            {
                var records = await transforms.ReadRecordsAsync(
                    participantId, identities, ct, includeAbsent: false);

                result.Add(new ParticipantData(participantId, records, null));
            }
            catch (Exception ex)
            {
                // A participant whose schema is unreadable is reported as such rather than
                // as holding nothing: "could not read MMS" and "MMS has no row" are
                // opposite findings and must not render identically.
                logger.LogWarning(
                    ex, "Run data: {ParticipantId} could not be read.", participantId);

                result.Add(new ParticipantData(participantId, [], ex.Message));
            }
        }

        return result;
    }
}
