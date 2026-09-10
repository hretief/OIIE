namespace RdlProvider.Application;

/// <summary>
/// A namespace a class code is qualified by.
///
/// Id-only, because that is the shape of dbo.namespaces in the EIS database
/// this shares with REG-LOCATION. The namespace has no name column to read;
/// the qualification lives in the class code itself (rdl:Instrument).
/// </summary>
public sealed record RdlNamespace(int NamespaceId);

/// <summary>
/// A curation grouping. Id-only for the same reason as
/// <see cref="RdlNamespace"/>.
///
/// Editorial rather than semantic either way: it organises the library for a
/// human browsing it and carries no classification meaning. Callers classify
/// against <see cref="RdlClass.Code"/>, never against a group.
/// </summary>
public sealed record RdlClassGroup(int GroupId);

/// <summary>
/// A class in the common library.
///
/// Maps dbo.class_objects in the EIS database, the same table REG-LOCATION
/// reads. RDL is the curation surface for that vocabulary; REG-LOCATION
/// consumes it.
///
/// <see cref="Code"/> is the governed identifier participants classify against
/// (rdl:Instrument) and is the only part of this record that travels on the
/// wire. <see cref="Name"/> and <see cref="Description"/> are for people
/// reading the library, and <see cref="ClassId"/> is an internal key that
/// consumers should not persist as a foreign identifier -- resolve by code.
///
/// <see cref="ParentClassId"/> is the class this one specialises, or null for a
/// root. It is what lets a participant holding only a subset bind something
/// classified against a leaf it does not hold at the nearest ancestor it does.
///
/// <see cref="Uuid"/> is the class's federation identity, held in
/// dbo.objects.guid rather than in class_objects. It is what a CCOM consumer
/// keys on: <see cref="Code"/> is what a human reads in a mapping, but the
/// UUID is what ShowTaxonomySet quotes and what a participant stores against
/// its local copy of the vocabulary. Nullable because the shared EIS schema
/// permits it and older rows predate the seeding -- a null here means the
/// library has no identity to offer, which the responder must handle rather
/// than paper over.
/// </summary>
public sealed record RdlClass(
    int ClassId,
    int GroupId,
    int NamespaceId,
    string Code,
    string Name,
    string? Description,
    int? ParentClassId,
    Guid? Uuid = null);
