namespace SimHost.Application.Identity;

/// <summary>
/// Stands in for the tag identity service that CIR will eventually provide.
///
/// The model it emulates has two parts, and keeping them apart is the whole point:
///
/// FederationId is the identity. It is opaque, it is minted once at the moment the
/// entity first exists — conceptual design — and it does not change through early
/// design, detailed design, construction, commissioning or operations. Machines use
/// it. Nothing about the physical thing, and nothing anyone calls it, participates in
/// its value.
///
/// Code (CodeValue) is a label for humans. It is optional, because not every entity
/// needs one, and it is plural over time and across systems: the same pump is TIC-106
/// in the design tool, LOC-000412 in the registry and 234443 in maintenance. A code
/// is how people consume the identity; it is not the identity.
///
/// Only a master mints — the design tool, or REG-LOCATION. Every other participant
/// already holds legacy data under its own codes, and its job is to register those
/// codes against the FederationId it was given, never to invent a second one. That is
/// what CIR cross-references: codes, converging on one identity.
///
/// The minted value is deliberately opaque rather than derived. An earlier version of
/// this file hashed the source and the code together, which was wrong in a way worth
/// recording: it made a re-coded tag a different entity, so the mutable label became
/// the master. Identity cannot be a function of anything that can change.
/// </summary>
public interface ITagIdentityService
{
    /// <summary>
    /// Mints a new FederationId. Only a master of identity may call this: the design
    /// tool when a tag is first drawn, or REG-LOCATION for a location it originates.
    /// A participant that received the entity from somewhere else must adopt what it
    /// was sent instead.
    /// </summary>
    Guid Mint();
}

/// <inheritdoc cref="ITagIdentityService"/>
public sealed class EmulatedTagIdentityService : ITagIdentityService
{
    public Guid Mint() =>
        // Version 7: opaque, but time-ordered, so identities minted in sequence sort
        // in the order they were created and index without page fragmentation.
        Guid.CreateVersion7();
}
