namespace RdlProvider.Application;

/// <summary>
/// Adds a class to the library.
///
/// <see cref="ClassId"/> is supplied by the caller rather than minted by the
/// store, so a curator can align ids with an external authority.
///
/// <see cref="ParentClassId"/> is optional; null makes the class a root.
///
/// <see cref="Uuid"/> is optional and exists for the case where the class
/// already has an identity somewhere else -- issued by a governing authority,
/// or carried over from a library being imported. Supplying it preserves that
/// identity instead of minting a competing one for the same concept. Omitting
/// it means this library is the origin of the class, and the store assigns a
/// fresh UUID rather than storing nothing: an object with no identity cannot
/// be quoted on the wire, and leaving the column NULL only defers that problem
/// to the responder.
/// </summary>
public sealed record CreateClassRequest(
    int ClassId,
    int GroupId,
    int NamespaceId,
    string Code,
    string Name,
    string? Description,
    int? ParentClassId,
    Guid? Uuid = null);

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
