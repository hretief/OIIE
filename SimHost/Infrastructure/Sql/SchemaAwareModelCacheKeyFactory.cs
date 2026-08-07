using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SimHost.Infrastructure.Sql;

/// <summary>
/// EF Core caches the compiled model per context type. ParticipantDbContext
/// varies its schema per instance, so without a schema-aware cache key every
/// participant after the first would silently inherit the first one's schema —
/// which would look like working software right up until two participants
/// disagreed about whose data they were reading.
/// </summary>
public sealed class SchemaAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        var schema = context is ParticipantDbContext participant
            ? participant.Schema
            : string.Empty;

        return (context.GetType(), schema, designTime);
    }
}
