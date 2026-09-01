namespace RegLocationProvider.Application;

/// <summary>
/// Configuration for the REG-LOCATION emulation.
/// </summary>
public sealed class RegLocationOptions
{
    public string SqlConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Applies the registry schema at startup. Left on for dev and demo; a real
    /// customer system would have its schema managed elsewhere.
    ///
    /// Safe to leave on permanently: schema.sql is re-runnable, so a cold
    /// start or a scale-out instance re-applying it is a no-op.
    /// </summary>
    public bool AutoCreateSchema { get; set; } = true;

    /// <summary>
    /// Applies the bootstrap seed after the schema. Separate from
    /// <see cref="AutoCreateSchema"/> because an empty-but-valid registry is a
    /// legitimate state: a customer restoring their own data wants the schema
    /// applied and the demo seed kept out.
    /// </summary>
    public bool AutoBootstrap { get; set; } = true;

    /// <summary>
    /// Where to announce a stewardship decision.
    ///
    /// Empty by default, and empty means silence rather than an error: a
    /// registry with nobody integrated to it has nowhere to send this, and that
    /// is a normal way to run, not a misconfiguration.
    /// </summary>
    public string ApprovalNotificationUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional key sent as x-functions-key with the notification, for when the
    /// listener is a Function app with its own authorization.
    /// </summary>
    public string ApprovalNotificationKey { get; set; } = string.Empty;
}
