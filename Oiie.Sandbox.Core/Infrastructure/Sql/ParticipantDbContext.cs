using Microsoft.EntityFrameworkCore;
using SimHost.Application.Identity;
using SimHost.Domain.Common;
using SimHost.Domain.Eng;
using SimHost.Domain.Mms;
using SimHost.Domain.OmReliability;
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
    private readonly Guid? _twinId;

    public ParticipantDbContext(
        DbContextOptions<ParticipantDbContext> options, string schema, Guid? twinId = null)
        : base(options)
    {
        _schema = schema;
        _twinId = twinId;
    }

    public string Schema => _schema;

    /// <summary>
    /// The twin this context is scoped to, or null for an unscoped one.
    ///
    /// Null is not a default so much as a distinct mode: the outbox dispatcher, the
    /// reset endpoint and schema initialisation all operate across every twin, and a
    /// filter would hide rows they exist to act on.
    /// </summary>
    public Guid? ITwinId => _twinId;

    public DbSet<ITwin> ITwins => Set<ITwin>();

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

        modelBuilder.Entity<ITwin>(entity =>
        {
            entity.ToTable("ITwin");

            // The identity is adopted, not generated: an iTwin is created outside this
            // sandbox and minting a second identifier for it would defeat the purpose.
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Code).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(400);
            entity.HasIndex(e => e.Code).IsUnique();
        });

        // Twin isolation, applied to the model rather than left to each query.
        //
        // ENG has several read paths that legitimately select everything they find --
        // promotion gathers work-in-progress tags, the builders gather what a version
        // published -- and a scope those queries have to remember is a scope that will
        // eventually be forgotten. One missed WHERE would put another project's tags
        // into a release, and it would publish cleanly.
        //
        // ITwinId is read from the context property, never captured into the lambda:
        // the compiled model is cached and shared across instances, so a captured
        // field would freeze the first context's twin into every later one.
        modelBuilder.Entity<Tag>()
            .HasQueryFilter(e => ITwinId == null || e.ITwinId == ITwinId);

        modelBuilder.Entity<NamedVersion>()
            .HasQueryFilter(e => ITwinId == null || e.ITwinId == ITwinId);

        modelBuilder.Entity<TagRelationship>()
            .HasQueryFilter(e => ITwinId == null || e.ITwinId == ITwinId);

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

            // The allocator reads its high-water mark through this: a code series
            // belongs to one twin, so the next P- in one project must not be decided
            // by what another project has already issued.
            entity.HasIndex(e => new { e.ITwinId, e.ParticipantId, e.CodePrefix });
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

            case "om_reliability":
                ConfigureOmReliability(modelBuilder);
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
            entity.Property(e => e.ModelNumber).HasMaxLength(64);
            entity.HasIndex(e => e.EquipmentNumber).IsUnique();
            entity.HasIndex(e => e.FunctionalLocationNumber);

            // Unlike a functional location, MMS originates its assets, so every row
            // has a real identity and the constraint needs no empty-Guid exemption.
            entity.HasIndex(e => e.FederationId).IsUnique();
        });

        modelBuilder.Entity<LocationRelationshipRecord>(entity =>
        {
            entity.ToTable("LocationRelationshipRecord");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FromLocationId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ToLocationId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.TypeKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ForwardRole).HasMaxLength(128);
            entity.Property(e => e.InverseRole).HasMaxLength(128);
            entity.Property(e => e.ForeignSourceId).HasMaxLength(64);
            entity.HasIndex(e => new { e.FromLocationId, e.ToLocationId, e.TypeKey }).IsUnique();
            entity.HasIndex(e => e.FromLocationId);
            entity.HasIndex(e => e.ToLocationId);

            // Same exemption as FunctionalLocationRecord: MMS adopts identity rather
            // than minting it, so a sender that asserted none leaves this empty and
            // more than one such row must remain possible.
            entity.HasIndex(e => e.FederationId)
                .IsUnique()
                .HasFilter("[FederationId] <> '00000000-0000-0000-0000-000000000000'");
        });

        modelBuilder.Entity<WorkOrder>(entity =>
        {
            entity.ToTable("WorkOrder");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OrderNumber).HasMaxLength(32).IsRequired();
            entity.Property(e => e.EventKind).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.EquipmentNumber).HasMaxLength(32).IsRequired();
            entity.Property(e => e.FunctionalLocationNumber).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.PerformedBy).HasMaxLength(128);
            entity.Property(e => e.SignedOffBy).HasMaxLength(128);
            entity.HasIndex(e => e.OrderNumber).IsUnique();
            entity.HasIndex(e => e.State);
        });
    }

    /// <summary>
    /// The O&amp;M consumer in OIIE Scenario 11.
    ///
    /// It holds only the event log: no locations, no assets, no reference data of its
    /// own. That is the honest shape of a system being provisioned entirely by
    /// publication, and it is what makes the unmapped-value behaviour visible here
    /// rather than hidden behind a local model that happens to agree.
    /// </summary>
    private static void ConfigureOmReliability(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AssetInstallationEvent>(entity =>
        {
            entity.ToTable("AssetInstallationEvent");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventKind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.AssetIdInSource).HasMaxLength(200);
            entity.Property(e => e.AssetSerialNumber).HasMaxLength(64);
            entity.Property(e => e.AssetDesignation).HasMaxLength(400);
            entity.Property(e => e.LocationIdInSource).HasMaxLength(200);
            entity.Property(e => e.LocationDesignation).HasMaxLength(400);
            entity.Property(e => e.PerformedBy).HasMaxLength(128);
            entity.Property(e => e.WorkOrderNumber).HasMaxLength(32);
            entity.Property(e => e.SourceParticipant).HasMaxLength(64);

            // Unique on the event identity, not on asset+location: the same asset
            // legitimately returns to the same location after a workshop repair, and a
            // composite constraint would reject the second installation as a duplicate.
            entity.HasIndex(e => e.FederationId).IsUnique();
            entity.HasIndex(e => new { e.AssetFederationId, e.OccurredAt });
            entity.HasIndex(e => e.LocationFederationId);
            entity.HasIndex(e => e.Cirid);
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

        modelBuilder.Entity<LocationConnection>(entity =>
        {
            entity.ToTable("LocationConnection");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FromLocationCode).HasMaxLength(64);
            entity.Property(e => e.ToLocationCode).HasMaxLength(64);
            entity.Property(e => e.FromSourceIdentifier).HasMaxLength(128).IsRequired();
            entity.Property(e => e.ToSourceIdentifier).HasMaxLength(128).IsRequired();
            entity.Property(e => e.TypeKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ForwardRole).HasMaxLength(128);
            entity.Property(e => e.InverseRole).HasMaxLength(128);
            entity.Property(e => e.SourceParticipant).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.FederationId).IsUnique();

            // Keyed on what the sender said, not on the codes: the codes are null
            // until approval, so a unique index over them would let every unresolved
            // edge collide with every other one on (null, null, type).
            entity.HasIndex(e => new { e.SourceParticipant, e.FromSourceIdentifier, e.ToSourceIdentifier, e.TypeKey })
                .IsUnique();
            entity.HasIndex(e => e.FromLocationCode);
            entity.HasIndex(e => e.ToLocationCode);
            entity.HasIndex(e => e.IsResolved);
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

            // Scoped by twin, not global. A tag number identifies an instrument within
            // one plant; two projects may legitimately each have a TIC-106, and a global
            // constraint would make the second project's design an error.
            entity.HasIndex(e => new { e.ITwinId, e.TagNumber }).IsUnique();
            entity.HasIndex(e => e.Maturity);

            // Deliberately NOT scoped by twin. FederationId is minted per tag, so two
            // twins' tags already differ here, and this is the column MMS and CIR
            // correlate on. Scoping it would imply the same identity may recur across
            // twins, which is the opposite of what it means.
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
            entity.HasIndex(e => e.ITwinId);
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

        modelBuilder.Entity<TagRelationshipType>(entity =>
        {
            entity.ToTable("TagRelationshipType");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Key).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ForwardRole).HasMaxLength(128).IsRequired();
            entity.Property(e => e.InverseRole).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(400);
            entity.HasIndex(e => e.Key).IsUnique();
        });

        modelBuilder.Entity<TagRelationship>(entity =>
        {
            entity.ToTable("TagRelationship");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TypeKey).HasMaxLength(200).IsRequired();
            entity.HasIndex(e => e.FederationId).IsUnique();

            // One assertion per direction per kind. Re-sending the same edge is a
            // restatement, not a second relationship.
            //
            // No twin needed in this key: both endpoints are tag ids, which are
            // already unique to one twin, so an edge cannot span two of them.
            entity.HasIndex(e => new { e.FromTagId, e.ToTagId, e.TypeKey }).IsUnique();

            entity.HasIndex(e => e.ITwinId);

            // Reading an edge from its sink end is the "Supplied By" direction, and
            // is as common as reading it from its source, so both ends are indexed.
            entity.HasIndex(e => e.FromTagId);
            entity.HasIndex(e => e.ToTagId);
        });
    }
}
