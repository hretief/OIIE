namespace RdlProvider.Application;

/// <summary>
/// Configuration for the RDL emulation.
///
/// There are no AutoCreateSchema or AutoBootstrap flags here, unlike
/// RegLocationOptions. RDL shares the EIS database with REG-LOCATION and reads
/// the same dbo.class_objects table; RegLocationProvider owns that DDL and
/// applies it at startup. A second app applying the same scripts would race
/// the first on a cold start and let the two definitions drift apart.
/// </summary>
public sealed class RdlOptions
{
    /// <summary>
    /// Points at the shared EIS database (acme-db-eis-{env}), the same one
    /// RegLocationProvider uses.
    /// </summary>
    public string SqlConnectionString { get; set; } = string.Empty;
}
