using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SimHost.Application.Participants;

namespace SimHost.Infrastructure.Sql;

public interface IParticipantDbContextFactory
{
    /// <summary>
    /// Opens a context for a participant, optionally scoped to one iTwin.
    ///
    /// Omitting the twin yields an unscoped context that sees every twin's rows.
    /// That is what the outbox dispatcher, the reset endpoint and schema
    /// initialisation need: they act on the schema as a whole, and a filter would
    /// hide exactly the rows they exist to process.
    /// </summary>
    ParticipantDbContext Create(string participantId, Guid? twinId = null);
}

/// <summary>
/// Builds a context bound to a participant's schema, connecting as that
/// participant's own contained user.
/// </summary>
public sealed class ParticipantDbContextFactory : IParticipantDbContextFactory
{
    private readonly ParticipantRegistry _registry;
    private readonly IParticipantConnectionStringProvider _connectionStrings;

    public ParticipantDbContextFactory(
        ParticipantRegistry registry,
        IParticipantConnectionStringProvider connectionStrings)
    {
        _registry = registry;
        _connectionStrings = connectionStrings;
    }

    public ParticipantDbContext Create(string participantId, Guid? twinId = null)
    {
        var participant = _registry.Get(participantId);

        var options = new DbContextOptionsBuilder<ParticipantDbContext>()
            .UseSqlServer(
                _connectionStrings.For(participantId),
                sql => sql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null))
            .ReplaceService<IModelCacheKeyFactory, SchemaAwareModelCacheKeyFactory>()
            .Options;

        return new ParticipantDbContext(options, participant.Schema, twinId);
    }
}
