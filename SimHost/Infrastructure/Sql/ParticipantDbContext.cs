using Microsoft.EntityFrameworkCore;
using SimHost.Application.Identity;
using SimHost.Domain.Common;
using SimHost.Domain.Eng;
using SimHost.Domain.Mms;
using SimHost.Domain.RegLocation;

namespace SimHost.Infrastructure.Sql;

/// <summary>
/// One context per participant, bound to that participant's SQL schema.
///
/// Isolation is enforced by schema *and* by a dedicated SQL login granted access
/// only to that schema (spec §6.2). Without the grants, a cross-schema join will
/// eventually be used to resolve a foreign identifier instead of a CIR call — it
/// will work, nobody will notice, and the demo will then prove nothing.
/// </summary>
public class ParticipantDbContext : DbContext
{
    private readonly string _schema;

    public ParticipantDbContext(DbContextOptions<ParticipantDbContext> options, string schema)
        : base(options)
    {
        _schema = schema;
    }

    public string Schema => _schema;

    public DbSet<MessageRecord> Messages => Set<MessageRecord>();
    public DbSet<CodeAssignment> Codes => Set<CodeAssignment>();
    public DbSet<ProvenanceEntry> Provenance => Set<ProvenanceEntry>();
    public DbSet<OutboxItem> Outbox => Set<OutboxItem>();
    public DbSet<IdentityMapEntry> IdentityMap => Set<IdentityMapEntry>();
    public DbSet<PendingWorkItem> PendingWork => Set<PendingWorkItem>();
    public DbSet<IsbmSessionRecord> IsbmSessions => Set<IsbmSessionRecord>();
    public DbSet<CirExchangeRecord> CirExchanges => Set<CirExchangeRecord>();

    public DbSet<ClassDefinition> Classes => Set<ClassDefinition>();
    public DbSet<PropertyDefinition> PropertyDefinitions => Set<PropertyDefinition>();
    public DbSet<ClassProperty> ClassProperties => Set<ClassProperty>();
    public DbSet<EntityClassification> Classifications => Set<EntityClassification>();
    public DbSet<EntityPropertyValue> PropertyValues => Set<EntityPropertyValue>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(_schema);

        modelBuilder.Entity<MessageRecord>(entity =>
        {
            entity.ToTable("Message");
            entity.HasKey(e => e.MessageId);
            entity.Property(e => e.Direction).HasConversion<string>().HasMaxLength(8);
            entity.Property(e => e.Pattern).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.ProcessingStatus).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.ChannelUri).HasMaxLength(400).IsRequired();
            entity.Property(e => e.Topic).HasMaxLength(400);
            entity.Property(e => e.Verb).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Noun).HasMaxLength(64).IsRequired();
            entity.Property(e => e.BodId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.CorrelationBodId).HasMaxLength(128);
            entity.Property(e => e.IsbmMessageId).HasMaxLength(128);
            entity.Property(e => e.IsbmSessionId).HasMaxLength(128);
            entity.Property(e => e.IsbmRequestId).HasMaxLength(128);
            entity.Property(e => e.CorrelationId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ContentRef).HasMaxLength(400).IsRequired();
            entity.Property(e => e.ValidationStatus).HasMaxLength(16).IsRequired();
            entity.HasIndex(e => e.CorrelationId);
            entity.HasIndex(e => new { e.ScenarioRunId, e.OccurredAt });
            entity.HasIndex(e => new { e.ChannelUri, e.OccurredAt });
        });

        modelBuilder.Entity<CirExchangeRecord>(entity =>
        {
            entity.ToTable("CirExchange");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ParticipantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Bod).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CorrelationId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ChannelUri).HasMaxLength(400).IsRequired();
            entity.Property(e => e.Topic).HasMaxLength(400).IsRequired();
            entity.Property(e => e.RequestMessageId).HasMaxLength(128);
            entity.Property(e => e.ConsumerSessionId).HasMaxLength(128);
            entity.Property(e => e.ResponseVerb).HasMaxLength(64);
            entity.Property(e => e.Outcome).HasMaxLength(24);

            // Deliberately unbounded. Truncating these would discard the one artifact
            // the table exists to keep, and would do it silently.
            entity.Property(e => e.RequestXml).IsRequired();
            entity.Property(e => e.ResponseXml);
            entity.Property(e => e.FaultsJson);

            entity.HasIndex(e => e.CorrelationId);
            entity.HasIndex(e => new { e.ParticipantId, e.SentUtc });
        });

        modelBuilder.Entity<ProvenanceEntry>(entity =>
        {
            entity.ToTable("Provenance");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Action).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.EntityType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.EntityKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Actor).HasMaxLength(128).IsRequired();
            entity.HasIndex(e => new { e.EntityType, e.EntityKey, e.At });
            entity.HasIndex(e => e.MessageId);
        });

        // Every participant holds codes, because every participant labels the things
        // it knows about, whether or not it originated them.
        modelBuilder.Entity<CodeAssignment>(entity =>
        {
            entity.ToTable("CodeAssignment");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ParticipantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Code).HasMaxLength(200).IsRequired();
            entity.HasIndex(e => e.FederationId);

            // One current code per participant per identity. Historic rows are
            // excluded from the constraint so re-coding can retain the old value.
            entity.HasIndex(e => new { e.FederationId, e.ParticipantId })
                .IsUnique()
                .HasFilter("[IsCurrent] = 1");

            // The reverse lookup that matters operationally: someone types a legacy
            // code and needs the identity behind it.
            entity.HasIndex(e => new { e.ParticipantId, e.Code });
        });

        modelBuilder.Entity<OutboxItem>(entity =>
        {
            entity.ToTable("Outbox");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ChangeKind).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Pattern).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.ContainerType).HasMaxLength(64);
            entity.Property(e => e.ContainerKey).HasMaxLength(200);
            entity.Property(e => e.EntityType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Verb).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Noun).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ChannelUri).HasMaxLength(400).IsRequired();
            entity.Property(e => e.Topic).HasMaxLength(400);
            entity.Property(e => e.CorrelationId).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => new { e.State, e.CreatedAt });
        });

        modelBuilder.Entity<IdentityMapEntry>(entity =>
        {
            entity.ToTable("IdentityMap");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.LocalEntityType).HasMaxLength(64);
            entity.Property(e => e.LocalKey).HasMaxLength(200);
            entity.Property(e => e.ForeignSourceId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ForeignIdInSource).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ForeignName).HasMaxLength(400);
            entity.Property(e => e.InvalidatedReason).HasMaxLength(400);
            entity.HasIndex(e => new { e.ForeignSourceId, e.ForeignIdInSource }).IsUnique();
            entity.HasIndex(e => e.Cirid);
        });

        modelBuilder.Entity<PendingWorkItem>(entity =>
        {
            entity.ToTable("PendingWork");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Kind).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Subject).HasMaxLength(400).IsRequired();
            entity.Property(e => e.DecidedBy).HasMaxLength(128);
        });

        modelBuilder.Entity<IsbmSessionRecord>(entity =>
        {
            entity.ToTable("IsbmSession");
            entity.HasKey(e => e.SessionId);
            entity.Property(e => e.SessionId).HasMaxLength(128);
            entity.Property(e => e.Kind).HasMaxLength(24).IsRequired();
            entity.Property(e => e.ChannelUri).HasMaxLength(400).IsRequired();
            entity.Property(e => e.ListenerUri).HasMaxLength(400);
            entity.Property(e => e.LastMessageId).HasMaxLength(128);
            entity.Ignore(e => e.IsOpen);
            entity.HasIndex(e => new { e.Kind, e.ChannelUri });
        });

        modelBuilder.Entity<ClassDefinition>(entity =>
        {
            entity.ToTable("ClassDefinition");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Origin).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.ClassKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.RdlSourceId).HasMaxLength(200);
            entity.Property(e => e.Version).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.AppliesTo).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ReceivedFrom).HasMaxLength(64);
            entity.HasIndex(e => new { e.ClassKey, e.Version }).IsUnique();
        });

        modelBuilder.Entity<PropertyDefinition>(entity =>
        {
            entity.ToTable("PropertyDefinition");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Origin).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.DataType).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.DefinitionKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.RdlSourceId).HasMaxLength(200);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.UnitOfMeasure).HasMaxLength(64);
            entity.Property(e => e.UomListId).HasMaxLength(128);
            entity.Property(e => e.CodeListId).HasMaxLength(128);
            entity.Property(e => e.ReceivedFrom).HasMaxLength(64);
            entity.HasIndex(e => e.DefinitionKey).IsUnique();
        });

        modelBuilder.Entity<ClassProperty>(entity =>
        {
            entity.ToTable("ClassProperty");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Requirement).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.DefaultUom).HasMaxLength(64);
            entity.Property(e => e.CodeListId).HasMaxLength(128);
            entity.Property(e => e.DisplayGroup).HasMaxLength(128);
            entity.Property(e => e.MinValue).HasPrecision(38, 10);
            entity.Property(e => e.MaxValue).HasPrecision(38, 10);
            entity.HasIndex(e => new { e.ClassId, e.DefinitionId }).IsUnique();
        });

        modelBuilder.Entity<EntityClassification>(entity =>
        {
            entity.ToTable("EntityClassification");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EntityType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.EntityKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.AssignedBy).HasMaxLength(128).IsRequired();
            entity.HasIndex(e => new { e.EntityType, e.EntityKey });
        });

        modelBuilder.Entity<EntityPropertyValue>(entity =>
        {
            entity.ToTable("EntityProperty");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EntityType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.EntityKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.NumericValue).HasPrecision(38, 10);
            entity.Property(e => e.UnitOfMeasure).HasMaxLength(64);
            entity.Property(e => e.CodeValue).HasMaxLength(200);
            entity.Property(e => e.CodeListId).HasMaxLength(128);
            entity.Property(e => e.BlobRef).HasMaxLength(400);
            entity.HasIndex(e => new { e.EntityType, e.EntityKey });
            entity.HasIndex(e => e.DefinitionId);
        });

        ConfigurePersonality(modelBuilder);
    }

    /// <summary>
    /// Personality-specific spine tables, mapped only into the schema that owns them.
    ///
    /// The model is already compiled per schema (see SchemaAwareModelCacheKeyFactory),
    /// so conditioning on the schema here gives each participant exactly its own
    /// tables — reg_asset never learns that eng.Tag exists, which matches the
    /// database grants rather than merely coexisting with them.
    /// </summary>
    private void ConfigurePersonality(ModelBuilder modelBuilder)
    {
        switch (_schema)
        {
            case "eng":
                ConfigureEng(modelBuilder);
                break;

            case "reg_location":
                ConfigureRegLocation(modelBuilder);
                break;

            case "mms":
                ConfigureMms(modelBuilder);
                break;
        }
    }

    private static void ConfigureMms(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FunctionalLocationRecord>(entity =>
        {
            entity.ToTable("FunctionalLocationRecord");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EquipmentNumber).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Designation).HasMaxLength(400);
            entity.Property(e => e.CostCentre).HasMaxLength(32);
            entity.Property(e => e.PlannerGroup).HasMaxLength(32);
            entity.Property(e => e.ForeignSourceId).HasMaxLength(200);
            entity.Property(e => e.ForeignIdInSource).HasMaxLength(200);
            entity.HasIndex(e => e.EquipmentNumber).IsUnique();
            entity.HasIndex(e => new { e.ForeignSourceId, e.ForeignIdInSource });
            entity.HasIndex(e => e.Cirid);

            // Filtered: a record MMS has not yet been told the identity of holds
            // Guid.Empty, and there can be many of those legitimately. Only real
            // identities must be unique — two rows under one FederationId would claim
            // MMS holds the same thing twice.
            entity.HasIndex(e => e.FederationId)
                .IsUnique()
                .HasFilter("[FederationId] <> '00000000-0000-0000-0000-000000000000'");
        });

        modelBuilder.Entity<EquipmentRecord>(entity =>
        {
            entity.ToTable("EquipmentRecord");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EquipmentNumber).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Designation).HasMaxLength(400);
            entity.Property(e => e.FunctionalLocationNumber).HasMaxLength(32);
            entity.Property(e => e.SerialNumber).HasMaxLength(64);
            entity.HasIndex(e => e.EquipmentNumber).IsUnique();
        });
    }

    private static void ConfigureRegLocation(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Location>(entity =>
        {
            entity.ToTable("Location");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.LocationCode).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(400);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.ClassKey).HasMaxLength(200);
            entity.Property(e => e.RequestedClassKey).HasMaxLength(200);
            entity.Property(e => e.Area).HasMaxLength(64);
            entity.Property(e => e.SourceParticipant).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SourceIdentifier).HasMaxLength(200).IsRequired();
            entity.HasIndex(e => e.LocationCode).IsUnique();
            entity.HasIndex(e => new { e.SourceParticipant, e.SourceIdentifier });
            entity.HasIndex(e => e.FederationId).IsUnique();
        });

        modelBuilder.Entity<LocationParent>(entity =>
        {
            entity.ToTable("LocationParent");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ParentLocationCode).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ChildLocationCode).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => new { e.ParentLocationCode, e.ChildLocationCode }).IsUnique();
        });

        modelBuilder.Entity<StewardshipItem>(entity =>
        {
            entity.ToTable("StewardshipItem");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceParticipant).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SourceIdentifier).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ProposedName).HasMaxLength(400);
            entity.Property(e => e.ProposedDescription).HasMaxLength(1000);
            entity.Property(e => e.RequestedClassKey).HasMaxLength(200);
            entity.Property(e => e.BoundClassKey).HasMaxLength(200);
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.DecidedBy).HasMaxLength(128);
            entity.Property(e => e.RejectReason).HasMaxLength(1000);
            entity.Property(e => e.LocationCode).HasMaxLength(64);
            entity.HasIndex(e => e.State);
            entity.HasIndex(e => e.FederationId);
        });
    }

    private static void ConfigureEng(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tag>(entity =>
        {
            entity.ToTable("Tag");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TagNumber).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ServiceDescription).HasMaxLength(400);
            entity.Property(e => e.PidReference).HasMaxLength(128);
            entity.Property(e => e.LineClass).HasMaxLength(64);
            entity.Property(e => e.DisciplineCode).HasMaxLength(16);
            entity.Property(e => e.UnitNumber).HasMaxLength(32);
            entity.Property(e => e.ClassKey).HasMaxLength(200);
            entity.Property(e => e.RangeMinimum).HasPrecision(38, 10);
            entity.Property(e => e.RangeMaximum).HasPrecision(38, 10);
            entity.Property(e => e.ControlAction).HasMaxLength(64);
            entity.Property(e => e.Maturity).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(e => e.TagNumber).IsUnique();
            entity.HasIndex(e => e.Maturity);
            entity.HasIndex(e => e.FederationId).IsUnique();
        });

        modelBuilder.Entity<NamedVersion>(entity =>
        {
            entity.ToTable("NamedVersion");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Scope).HasMaxLength(200);
            entity.Property(e => e.CreatedBy).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<ValidationFinding>(entity =>
        {
            entity.ToTable("ValidationFinding");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TagNumber).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Rule).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Detail).HasMaxLength(1000).IsRequired();
            entity.HasIndex(e => e.NamedVersionId);
        });
    }
}
