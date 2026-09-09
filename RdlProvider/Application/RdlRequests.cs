namespace RdlProvider.Application;

/// <summary>
/// Adds a class to the library.
///
/// <see cref="ClassId"/> is supplied by the caller rather than minted by the
/// store, so a curator can align ids with an external authority.
///
/// <see cref="ParentClassId"/> is optional; null makes the class a root.
/// </summary>
public sealed record CreateClassRequest(
    int ClassId,
    int GroupId,
    int NamespaceId,
    string Code,
    string Name,
    string? Description,
    int? ParentClassId);

/// <summary>
/// Edits an existing class.
///
/// Code is absent on purpose. The code is the governed identifier other
/// participants have already resolved against and may have cached; changing it
/// in place would silently invalidate their caches and rewrite the meaning of
/// data already published. Retiring a class and introducing a replacement is
/// the honest way to make that change.
/// </summary>
public sealed record UpdateClassRequest(
    int GroupId,
    string Name,
    string? Description,
    int? ParentClassId);
