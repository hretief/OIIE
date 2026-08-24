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

/// <summary>A physical item.</summary>
public sealed record RegItem(
    int ItemId,
    int NamespaceId,
    int UnitId,
    int TrnId);

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
    string Name);

/// <summary>
/// A tag together with the registry row that carries its federation GUID and
/// scope. The two are always written as a pair, so they are read as one.
/// </summary>
public sealed record RegTagDetail(RegTag Tag, RegObject Object);
