namespace RdlProvider.Application;

/// <summary>
/// Raised when a write would violate the library's own rules -- a duplicate
/// class code, or a parent that does not exist. Distinct from a missing row,
/// which is expressed as a null return.
/// </summary>
public sealed class RdlConflictException(string message) : Exception(message);

/// <summary>
/// Persistence for the RDL class library.
///
/// The vocabulary is the whole of this system's data: namespace, group, class.
/// It knows nothing of CCOM, Segments, BODs or channels -- RDL emulates a
/// reference data library, and a library does not know who is asking or why.
/// Everything channel-facing lives in RdlEngine.
/// </summary>
public interface IRdlStore
{
    Task<IReadOnlyList<RdlNamespace>> GetNamespacesAsync(CancellationToken ct);

    Task<IReadOnlyList<RdlClassGroup>> GetClassGroupsAsync(CancellationToken ct);

    /// <summary>
    /// The library, optionally narrowed to one namespace.
    ///
    /// Returns the full set rather than paging. The library is small by
    /// design and consumers cache it whole; paging would add a cursor
    /// contract to the request/response flow for no benefit at this size.
    /// </summary>
    Task<IReadOnlyList<RdlClass>> GetClassesAsync(int? namespaceId, CancellationToken ct);

    Task<RdlClass?> FindClassAsync(int classId, CancellationToken ct);

    /// <summary>
    /// Looks a class up by its governed code.
    ///
    /// This is the lookup that matters: consumers arrive holding a code,
    /// because the code is what travels on the wire. Returns null when the
    /// library does not hold the code.
    /// </summary>
    Task<RdlClass?> FindClassByCodeAsync(string code, CancellationToken ct);

    /// <summary>
    /// Adds a class to the library.
    ///
    /// The class id comes from the request rather than being minted here, so
    /// that a curator can align ids with an external authority. Throws
    /// <see cref="RdlConflictException"/> if the id or code is already taken.
    /// </summary>
    Task<RdlClass> CreateClassAsync(CreateClassRequest request, CancellationToken ct);

    /// <summary>
    /// Edits an existing class. Returns null if no such class exists.
    /// </summary>
    Task<RdlClass?> UpdateClassAsync(int classId, UpdateClassRequest request, CancellationToken ct);
}
