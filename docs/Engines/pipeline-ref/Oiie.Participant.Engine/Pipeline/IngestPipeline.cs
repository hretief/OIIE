using System.Xml.Linq;
using Oiie.Participant.Engine.Contracts;
using Oiie.Participant.Engine.Plan;
using Oiie.Participant.Engine.Resolution;

namespace Oiie.Participant.Engine.Pipeline;

/// E1–E8. The whole ingest leg, once. Grep this file for a participant name or a
/// CCOM noun and find nothing — that is the §1 acceptance test.
public sealed class IngestPipeline(
    IIsbmClient isbm,
    IMessageLedger ledger,          // E2, dedup on BODID
    IBodValidator validator,        // E3, XSD + Schematron(RDL)
    ResolutionRunner resolutions,   // E4
    IInboundTransform transform,    // P1  ← the plug point
    PlanExecutor executor,          // E5 + E6
    ILogger<IngestPipeline> log)
{
    public async Task DrainAsync(string sessionId, CancellationToken ct)
    {
        while (await isbm.ReadPublicationAsync(sessionId, ct) is { } message)
        {
            var outcome = await ProcessAsync(message, ct);

            switch (outcome)
            {
                case PersistOutcome.Applied or PersistOutcome.Rejected:
                    // E8 — remove ONLY after the verdict is recorded. Reversing
                    // these two loses the work silently on a crash between them.
                    await ledger.RecordAsync(message.BodId, outcome, ct);
                    await isbm.RemovePublicationAsync(sessionId, ct);
                    break;

                case PersistOutcome.Failed { Transient: true } f:
                    // Leave it on the channel and STOP. Skipping ahead reorders
                    // the stream; a permanently bad message needs manual removal.
                    log.LogWarning("transient failure on {Bod}: {Reason}", message.BodId, f.Reason);
                    return;

                case PersistOutcome.Failed { Transient: false } f:
                    await ledger.RecordAsync(message.BodId, f, ct);
                    await isbm.RemovePublicationAsync(sessionId, ct);
                    break;
            }
        }
    }

    private async Task<PersistOutcome> ProcessAsync(IsbmMessage message, CancellationToken ct)
    {
        // E2
        if (await ledger.SeenAsync(message.BodId, ct))
            return new PersistOutcome.Applied([]);

        var bod = XDocument.Parse(message.Payload);

        // E3 — XSD cannot express obligation conditioned on classification, so
        // Schematron generated from the RDL applicability matrix runs alongside it.
        if (await validator.ValidateAsync(bod, ct) is { IsValid: false } v)
            return new PersistOutcome.Rejected(v.Summary);

        // E4
        var resolution = await resolutions.RunAsync(transform.Resolutions, bod, ct);
        var context = resolution switch
        {
            ResolutionResult.Ok ok         => ok.Context,
            ResolutionResult.Reject r      => null,
            ResolutionResult.Transient t   => null,
            _ => throw new ArgumentOutOfRangeException()
        };

        if (context is null)
            return resolution is ResolutionResult.Transient tr
                ? new PersistOutcome.Failed(true, tr.Reason)
                : new PersistOutcome.Rejected(((ResolutionResult.Reject)resolution).Reason);

        // P1 — pure. No await, no cancellation token, nothing to mock.
        var plan = transform.Apply(bod, context);

        // E5–E6
        return await executor.ExecuteAsync(plan, context, ct);
    }
}
