using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using SimHost.Domain.Sandbox;

namespace SimHost.Infrastructure.Sql;

/// <summary>
/// Scenario orchestration state, in the shared <c>sandbox</c> schema.
///
/// Deliberately separate from <see cref="ParticipantDbContext"/>. A run spans every
/// participant, so putting its record inside one participant's schema would make that
/// participant privileged in a model whose whole point is that none of them are.
///
/// The schema and its principal already exist: deploy/sandbox/sql/01-schemas-and-grants.sql
/// creates <c>sb_orchestrator</c> with <c>DEFAULT_SCHEMA = [sandbox]</c> and grants every
/// participant SELECT/INSERT/UPDATE on <c>SCHEMA::sandbox</c> — so a participant can stamp
/// its own rows with a run id, while only the orchestrator writes the run itself.
/// </summary>
public class SandboxDbContext : DbContext
{
    public const string SchemaName = "sandbox";

    public SandboxDbContext(DbContextOptions<SandboxDbContext> options)
        : base(options)
    {
    }

    public DbSet<ScenarioRun> ScenarioRuns => Set<ScenarioRun>();
    public DbSet<ScenarioStepRun> ScenarioSteps => Set<ScenarioStepRun>();
    public DbSet<AssertionResult> Assertions => Set<AssertionResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<ScenarioRun>(entity =>
        {
            entity.ToTable("ScenarioRun");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ScenarioId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(400);
            entity.Property(e => e.Mode).HasConversion<string>().HasMaxLength(8);
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.AbortReason).HasMaxLength(1000);
            entity.HasIndex(e => new { e.ScenarioId, e.StartedUtc });
        });

        modelBuilder.Entity<ScenarioStepRun>(entity =>
        {
            entity.ToTable("ScenarioStep");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.StepId).HasMaxLength(64);
            entity.Property(e => e.ParticipantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Action).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(16);

            // Unbounded: an action's result is what later assertions read, and a
            // truncated JSON document fails to parse rather than losing a tail.
            entity.Property(e => e.ArgsJson);
            entity.Property(e => e.ResultJson);
            entity.Property(e => e.Error);

            entity.HasIndex(e => new { e.ScenarioRunId, e.Ordinal });
        });

        modelBuilder.Entity<AssertionResult>(entity =>
        {
            entity.ToTable("Assertion");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Assertion).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ParticipantId).HasMaxLength(64);
            entity.Property(e => e.Severity).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Owner).HasConversion<string>().HasMaxLength(16);

            // Also unbounded. These two are the entire diagnostic value of a failure —
            // truncating the evidence to fit a column would defeat the reason the
            // columns exist.
            entity.Property(e => e.Observed);
            entity.Property(e => e.Suggests);
            entity.Property(e => e.ArgsJson);

            entity.HasIndex(e => new { e.ScenarioRunId, e.Ordinal });
            entity.HasIndex(e => new { e.ScenarioRunId, e.Severity });
        });
    }
}

public interface ISandboxDbContextFactory
{
    SandboxDbContext Create();
}

/// <summary>
/// Creates the orchestration tables, and clears them on request.
///
/// Separate from <see cref="IParticipantSchemaInitializer"/> because the sandbox
/// schema is not a participant's: it is shared, and dropping every table in it the
/// way a participant reset does would destroy the run currently writing to it.
/// </summary>
public interface ISandboxSchemaInitializer
{
    /// <returns>True when tables were created; false when they already existed.</returns>
    Task<bool> EnsureTablesAsync(CancellationToken ct = default);

    /// <summary>Deletes all run history. Returns the number of runs removed.</summary>
    Task<int> PurgeRunsAsync(CancellationToken ct = default);
}

public sealed class SandboxSchemaInitializer(
    ISandboxDbContextFactory factory,
    ILogger<SandboxSchemaInitializer> logger) : ISandboxSchemaInitializer
{
    public async Task<bool> EnsureTablesAsync(CancellationToken ct = default)
    {
        await using var db = factory.Create();

        var exists = await db.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS Value
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = 'sandbox' AND t.name = 'ScenarioRun'
                """)
            .SingleAsync(ct);

        if (exists > 0)
        {
            return false;
        }

        var creator = db.Database.GetService<IRelationalDatabaseCreator>();
        await creator.CreateTablesAsync(ct);

        logger.LogInformation("Created scenario orchestration tables in schema {Schema}.",
            SandboxDbContext.SchemaName);

        return true;
    }

    public async Task<int> PurgeRunsAsync(CancellationToken ct = default)
    {
        await using var db = factory.Create();

        // Children first: there are no foreign keys between these tables, so an
        // interrupted purge that removed runs but left assertions would leave rows
        // that no query reaches and no reset clears.
        await db.Assertions.ExecuteDeleteAsync(ct);
        await db.ScenarioSteps.ExecuteDeleteAsync(ct);

        return await db.ScenarioRuns.ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// Connects as <c>sb_orchestrator</c>, which reset and seeding already use. That
/// principal is a db_owner because reset truncates every participant schema, so it
/// is the one connection in the system that is deliberately not confined.
/// </summary>
public sealed class SandboxDbContextFactory : ISandboxDbContextFactory
{
    private readonly IParticipantConnectionStringProvider _connectionStrings;

    public SandboxDbContextFactory(IParticipantConnectionStringProvider connectionStrings)
    {
        _connectionStrings = connectionStrings;
    }

    public SandboxDbContext Create()
    {
        var options = new DbContextOptionsBuilder<SandboxDbContext>()
            .UseSqlServer(
                _connectionStrings.ForService("orchestrator", SandboxDbContext.SchemaName),
                sql => sql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null))
            .Options;

        return new SandboxDbContext(options);
    }
}
