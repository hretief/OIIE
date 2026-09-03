namespace RegLocationProvider.Application;

/// <summary>
/// The registry row every registered entity has in dbo.objects.
///
/// This is the registry's own bookkeeping: identity, federation GUID, scope,
/// flags and audit stamps. Every domain row below is paired with one of these,
/// and the schema's foreign keys make that pairing mandatory rather than
/// conventional.
/// </summary>
public sealed record RegObject(
    int ObjectId,
    int ObjectType,
    Guid? Guid,
    int ScopeId,
    byte HideFlags,
    byte LockFlags,
    DateTime? DateAdded,
    int? AddedBy,
    DateTime? DateChanged,
    int? ChangedBy);

/// <summary>
/// A scope: the boundary of the site a dataset applies to.
///
/// <see cref="ContextObjectId"/> and <see cref="ContextObjectType"/> are the
/// OPTIONAL business object whose context the scope stands for -- an
/// Organization, a Project, a Site. They are deliberately named Context here,
/// because in the table they are called object_id/object_type and are routinely
/// mistaken for the scope's own registry row, which they are not. Both are
/// supplied together or not at all.
/// </summary>
public sealed record RegScope(
    int ScopeId,
    string Name,
    int NamespaceId,
    int? ContextObjectId,
    int? ContextObjectType,
    int? ParentId,
    bool IsEnabled,
    int UsageCount);

/// <summary>
/// What a cascaded scope delete removed.
///
/// Reported rather than returning a bare 204 because the counts are the only
/// evidence the caller has of how much the request actually destroyed, and a
/// cascade that silently took more than expected is worth being able to see
/// after the fact.
/// </summary>
public sealed record ScopeCascadeResult(
    int ScopeId,
    int TagsDeleted,
    int SerialsDeleted,
    int ItemsDeleted,
    int ScopesDeleted);

/// <summary>A classification group.</summary>
public sealed record RegClassGroup(int GroupId);

/// <summary>A class an item or tag may be created on.</summary>
public sealed record RegClass(
    int ClassId,
    int GroupId,
    int NamespaceId);

/// <summary>A unit of measure.</summary>
public sealed record RegUnit(int UnitId);

/// <summary>A namespace.</summary>
public sealed record RegNamespace(int NamespaceId);

/// <summary>
/// A physical item: a kind of thing, not one of them.
///
/// Code, Description and ItemType are nullable because rows created before the
/// registry recorded them have no values to back-fill. SyncSites files a site
/// type here -- 'Highway' -- so a type with no code would be an integer nothing
/// can render.
/// </summary>
public sealed record RegItem(
    int ItemId,
    int NamespaceId,
    int UnitId,
    int TrnId,
    string? Code = null,
    string? Description = null,
    string? ItemType = null);

/// <summary>
/// A tag: what an operator reads off a plate in the field, and what the rest of
/// the world means when it says "functional location".
///
/// Code is unique per item and revision rather than globally, so a lookup by
/// code alone can legitimately return more than one row.
/// </summary>
public sealed record RegTag(
    int TagId,
    int ItemId,
    int ClassId,
    string Code,
    int Revision,
    string Name,
    string State);

/// <summary>
/// Where a tag stands with the steward who owns this registry.
///
/// A tag that arrived from engineering is a proposal about a plant that may not
/// be built as drawn. Approval is what admits it to the registry proper, and it
/// is an act of this system rather than of whatever sent the tag.
///
/// These are the strings the database stores, not an enum's ordinal, so a value
/// read back compares equal to what a query on the table would find.
/// </summary>
public static class RegTagState
{
    public const string Proposed = "Proposed";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    public static bool IsValid(string? state) =>
        state is Proposed or Approved or Rejected;
}

/// <summary>
/// A tag together with the registry row that carries its federation GUID and
/// scope. The two are always written as a pair, so they are read as one.
/// </summary>
public sealed record RegTagDetail(RegTag Tag, RegObject Object);

/// <summary>
/// A serialised item: one particular instance of the kind of thing an item
/// describes. The item says 'Highway'; this says 'US Route 202'.
///
/// EIS calls the table item_serial_nos and the key serial_id, and those names
/// are kept so the EIS material can be read against this schema without
/// translation.
/// </summary>
public sealed record RegSerial(
    int SerialId,
    int ItemId,
    string Name,
    string? Description);

/// <summary>
/// A serial together with its registry row, read as a pair for the same reason
/// <see cref="RegTagDetail"/> is.
///
/// The GUID on the registry row is what makes a serial findable from outside:
/// SyncSites gives the site instance the iTwin's federation ID, and the same
/// GUID also appears on the Scope. They are different object types, so both can
/// hold it -- but it does mean a caller searching by GUID has to say which of
/// the two it wants.
/// </summary>
public sealed record RegSerialDetail(RegSerial Serial, RegObject Object);
