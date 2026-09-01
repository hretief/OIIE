namespace RegLocationProvider.Application;

/// <summary>
/// What the registry says happened, in the registry's own words.
///
/// Deliberately thin: identity, the code a person would recognise, who decided,
/// and when. No CCOM, no BOD, no channel. A notification carrying the whole tag
/// would also be a notification that goes stale between being written and being
/// read, and would quietly become a second, worse copy of the registry.
///
/// A receiver that needs the details reads them back through the API, which is
/// the only place they are true.
/// </summary>
public sealed record TagApprovedNotification(
    int TagId,
    string Code,
    int Revision,
    Guid? Guid,
    int ScopeId,
    string DecidedBy,
    DateTimeOffset DecidedAt);

/// <summary>
/// Tells whoever is listening that a steward made a decision.
///
/// This is the one seam where REG-LOCATION acknowledges anything outside itself,
/// and it is kept as narrow as possible: a customer registry does not publish to
/// a channel, but a real one does raise a change notification, and without one
/// there is no way for an integrator to learn of an approval except by polling.
///
/// Note what it still does not know: no ISBM, no BOD, no topic. It posts a fact
/// to a URL it was configured with. Translating that fact for a channel is the
/// engine's work.
/// </summary>
public interface IApprovalNotifier
{
    /// <summary>
    /// Announces an approval.
    ///
    /// Implementations must not throw for delivery failure. The approval is
    /// already committed by the time this is called, and failing the steward's
    /// request because a downstream listener was unreachable would make the
    /// registry's own availability depend on an integration it does not own.
    /// </summary>
    Task NotifyTagApprovedAsync(TagApprovedNotification notification, CancellationToken ct);
}

/// <summary>
/// The default: says nothing to no one.
///
/// Registered when no notification endpoint is configured, so REG-LOCATION runs
/// standalone exactly as it did before this seam existed. That is the honest
/// default -- a customer registry with nobody integrated to it genuinely has
/// nowhere to send this.
/// </summary>
public sealed class NullApprovalNotifier : IApprovalNotifier
{
    public Task NotifyTagApprovedAsync(TagApprovedNotification notification, CancellationToken ct)
        => Task.CompletedTask;
}
