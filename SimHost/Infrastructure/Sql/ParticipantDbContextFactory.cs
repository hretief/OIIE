using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SimHost.Application.Participants;

namespace SimHost.Infrastructure.Sql;

public interface IParticipantDbContextFactory
{
    ParticipantDbContext Create(string participantId);
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

    public ParticipantDbContext Create(string participantId)
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

        return new ParticipantDbContext(options, participant.Schema);
    }
}
