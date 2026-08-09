using System.Security.Cryptography;
using System.Text;

namespace Oiie.Ccom.Types;

/// <summary>
/// Derives stable CCOM UUIDs from natural keys.
///
/// CCOM declares UUID with minOccurs="1" on Entity and on the nested reference-data
/// types, so an omitted UUID is a schema violation rather than a missing nicety.
/// Most nested objects — an InfoSource naming a system, a type pointing at an RDL
/// class — have no identity of their own in the Sandbox, only a key.
///
/// The id is derived from that key rather than generated, because the same key
/// denotes the same thing in every participant and every run. Random ids would be
/// equally valid against the schema and useless for correlation: MIMOSA-RDL would
/// arrive with a different UUID from each sender, and every re-run would churn the
/// identity view for no reason.
/// </summary>
public static class CcomUuid
{
    /// <summary>
    /// A stable id for <paramref name="key"/>, scoped by <paramref name="kind"/> so
    /// that an InfoSource named "ENG" and a class keyed "ENG" do not collide.
    /// </summary>
    public static Guid FromKey(string kind, string? key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        // MD5 is a hash-to-128-bits convenience here, never a security boundary.
        // It is the only algorithm that yields exactly the 16 bytes a Guid needs.
        var material = string.IsNullOrWhiteSpace(key) ? kind : $"{kind}\u001f{key}";
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>Stable id for the system that registered an entity.</summary>
    public static Guid ForInfoSource(string? shortName) => FromKey("InfoSource", shortName);

    /// <summary>Stable id for a reference-data class or property definition.</summary>
    public static Guid ForReferenceData(string? sourceId, string? key) =>
        FromKey("ReferenceData", $"{sourceId}\u001f{key}");

    /// <summary>
    /// Stable id for a value carried on an entity. Scoped by the owning entity so the
    /// same property on two entities does not resolve to one id.
    /// </summary>
    public static Guid ForValue(Guid owner, string? key) =>
        FromKey("Value", $"{owner:N}\u001f{key}");
}
