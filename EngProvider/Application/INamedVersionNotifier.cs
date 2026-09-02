namespace EngProvider.Application;

/// <summary>
/// What ENG says happened, in ENG's own words.
///
/// Deliberately thin: which model, which marker, and the changeset it was cut
/// at. No elements, no CCOM, no BOD, no channel. A notification carrying the
/// marker's contents would go stale between being written and being read, and
/// would quietly become a second, worse copy of the design.
///
/// A receiver that needs the elements reads them back through
/// GET named-versions/{id}/elements, which is the only place they are true --
/// and which derives them on demand from changeset position, so the answer is
/// the same however long after the fact it is asked for.
/// </summary>
public sealed record NamedVersionCreatedNotification(
    long NamedVersionId,
    Guid VersionGuid,
    Guid IModelId,
    string Name,
    int ChangesetIndex,
    DateTime CreatedUtc)
{
    /// <summary>
    /// Names the shape rather than the sender, so a receiver can dispatch on it.
    ///
    /// Mirrors the platform event ENG emulates, so that if this is ever replaced
    /// by the real iModels.namedVersionCreated.v1 the receiver does not change.
    /// </summary>
    public string EventType { get; init; } = "iModels.namedVersionCreated.v1";
}

/// <summary>
/// Tells whoever is listening that a marker was cut.
///
/// This is the one seam where ENG acknowledges anything outside itself, and it
/// is kept as narrow as possible: an engineering tool does not publish to a
/// channel, but a real one does raise a change notification, and without one
/// there is no way for an integrator to learn of a release except by polling.
///
/// Note what it still does not know: no ISBM, no BOD, no topic. It posts a fact
/// to a URL it was configured with. Translating that fact for a channel is the
/// engine's work.
/// </summary>
public interface INamedVersionNotifier
{
    /// <summary>
    /// Announces a named version.
    ///
    /// Implementations must not throw for delivery failure. The marker is
    /// already committed by the time this is called, and failing the author's
    /// request because a downstream listener was unreachable would make ENG's
    /// own availability depend on an integration it does not own.
    /// </summary>
    Task NotifyNamedVersionCreatedAsync(NamedVersionCreatedNotification notification, CancellationToken ct);
}

/// <summary>
/// Does nothing, for the standalone case where no listener is configured.
///
/// ENG is usable on its own; a missing notification URL means nobody asked to
/// be told, not that something is misconfigured.
/// </summary>
public sealed class NullNamedVersionNotifier : INamedVersionNotifier
{
    public Task NotifyNamedVersionCreatedAsync(
        NamedVersionCreatedNotification notification, CancellationToken ct) => Task.CompletedTask;
}
