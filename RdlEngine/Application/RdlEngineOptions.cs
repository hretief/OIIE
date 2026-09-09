namespace RdlEngine.Application;

/// <summary>
/// Configuration for the RDL integration engine.
///
/// The set name is configurable rather than derived from the namespace id
/// because the id is an internal key of the EIS database and means nothing to a
/// participant. What travels on the wire is a name a consumer can ask for by
/// ShortName, so the mapping from id to name is a deployment decision.
/// </summary>
public sealed class RdlEngineOptions
{
    /// <summary>Base URL of RdlProvider, e.g. https://acme-api-rdl-dev.azurewebsites.net/api</summary>
    public string RdlBaseUrl { get; set; } = string.Empty;

    /// <summary>Function key for RdlProvider, sent as x-functions-key.</summary>
    public string? RdlApiKey { get; set; }

    /// <summary>
    /// Names each RDL namespace id is published under, keyed by id.
    ///
    /// A namespace with no entry here is still answered, under
    /// <see cref="DefaultSetName"/> suffixed with the id. Dropping it would
    /// hide classes the library plainly holds, and a consumer would have no way
    /// to tell an unconfigured namespace from an empty one.
    /// </summary>
    public Dictionary<int, string> TaxonomySetNames { get; set; } = [];

    /// <summary>
    /// Name used for the set when a request carries no ShortName selector, and
    /// the stem for namespaces missing from <see cref="TaxonomySetNames"/>.
    /// </summary>
    public string DefaultSetName { get; set; } = "ACME-RDL";

    /// <summary>
    /// Version reported on every set.
    ///
    /// A single value across all sets, because RDL has no per-namespace version
    /// column to read. It is reported rather than omitted so a consumer can at
    /// least detect that the library it validated against has been re-issued.
    /// </summary>
    public string? Version { get; set; } = "1.0";

    /// <summary>The LogicalID this engine identifies itself as in the BOD's ApplicationArea.</summary>
    public string SenderLogicalId { get; set; } = "RDL";

    /// <summary>
    /// Resolves the published name for a namespace id.
    /// </summary>
    public string ResolveSetName(int namespaceId) =>
        TaxonomySetNames.TryGetValue(namespaceId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : $"{DefaultSetName}-{namespaceId}";
}
