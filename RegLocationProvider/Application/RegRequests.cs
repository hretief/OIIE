namespace RegLocationProvider.Application;

/// <summary>
/// What a caller supplies to create a tag.
///
/// TagId is absent: the registry allocates it. Callers that could choose their
/// own id would eventually collide with the allocator, and the resulting
/// duplicate-key failures would surface far from the caller that caused them.
///
/// Guid is optional. When omitted the registry mints one, because a federation
/// identifier that only sometimes exists is not a federation identifier.
/// </summary>
public sealed record CreateTagRequest(
    int ItemId,
    int ClassId,
    string Code,
    int Revision,
    string Name,
    int ScopeId,
    Guid? Guid = null,
    string? State = null);

/// <summary>
/// A steward's decision on a proposed tag.
///
/// Carries who decided and why. A registry that recorded only the outcome could
/// not answer the question stewardship exists to answer -- who admitted this to
/// the registry, and on what grounds -- and that question is usually asked long
/// after the person has moved on.
/// </summary>
public sealed record ApproveTagRequest(
    string DecidedBy,
    string? Note = null);

/// <summary>
/// What a caller may change on an existing tag.
///
/// ItemId is not here. Moving a tag to a different item is not an edit, it is a
/// different tag: the code/revision uniqueness is scoped to the item, so a move
/// can silently collide or silently free up a code someone else has taken.
/// </summary>
public sealed record UpdateTagRequest(
    int ClassId,
    string Code,
    int Revision,
    string Name);

/// <summary>What a caller supplies to create an item.</summary>
public sealed record CreateItemRequest(
    int NamespaceId,
    int UnitId,
    int TrnId,
    int ScopeId,
    Guid? Guid = null);

/// <summary>
/// What a caller supplies to create a scope.
///
/// ContextObjectId/ContextObjectType are the optional business object the scope
/// represents. They must be supplied together; the schema rejects a half-filled
/// pair rather than letting it bypass the foreign key.
/// </summary>
public sealed record CreateScopeRequest(
    string Name,
    int NamespaceId,
    int? ParentId = null,
    int? ContextObjectId = null,
    int? ContextObjectType = null,
    Guid? Guid = null);
