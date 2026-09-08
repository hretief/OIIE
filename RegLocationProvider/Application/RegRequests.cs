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
    Guid? Guid = null,
    string? Code = null,
    string? Description = null,
    string? ItemType = null);

/// <summary>
/// What a caller supplies to create a serialised item.
///
/// Name is required and Description is not: a serial with no name is a row that
/// can be counted but not recognised, whereas a missing description costs
/// nothing. Guid is optional and minted when absent, as it is for a tag.
/// </summary>
public sealed record CreateSerialRequest(
    int ItemId,
    string Name,
    int ScopeId,
    string? Description = null,
    Guid? Guid = null);

/// <summary>
/// What a caller supplies to update an existing serial.
///
/// ItemId is absent for the same reason it is absent from
/// <see cref="UpdateTagRequest"/>: moving an instance to a different type is not
/// an edit, it is a different thing.
/// </summary>
public sealed record UpdateSerialRequest(
    string Name,
    string? Description = null);

/// <summary>
/// Points a scope at the business object whose context it represents.
///
/// Separate from creating the scope because the two happen at different times:
/// SyncSites creates the Scope before the Serial exists, then links them once it
/// does. Both columns travel together -- the schema rejects a half-filled pair
/// rather than letting it slip past the composite foreign key.
/// </summary>
public sealed record SetScopeContextRequest(
    int ContextObjectId,
    int ContextObjectType);

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

/// <summary>
/// What a caller supplies to add a class to the registry's vocabulary.
///
/// ClassId is supplied rather than minted, unlike scopes and tags. A class id
/// has to agree with the id the same class carries in the participant's own
/// system, so the caller is the only party that can know it; a registry-assigned
/// id would name a class nothing else could recognise.
/// </summary>
public sealed record CreateClassRequest(
    int ClassId,
    int GroupId,
    int NamespaceId,
    string Code,
    string Name,
    string? Description = null,
    int? ParentClassId = null,
    Guid? Guid = null);

/// <summary>
/// What a caller supplies to update an existing class.
///
/// GroupId and NamespaceId are absent: moving a class into a different group or
/// namespace is not an edit to that class, it is a different class. Code stays
/// editable because a vocabulary can correct an identifier it got wrong, and
/// ParentClassId because a hierarchy is routinely filled in after the classes
/// themselves exist.
/// </summary>
public sealed record UpdateClassRequest(
    string Code,
    string Name,
    string? Description = null,
    int? ParentClassId = null);
