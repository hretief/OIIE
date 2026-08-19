using Microsoft.EntityFrameworkCore;
using SimHost.Application.Identity;
using SimHost.Domain.Common;
using SimHost.Domain.Eng;
using SimHost.Domain.Mms;
using SimHost.Domain.Cms;
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

            // Covers the dispatcher's "has this item already been posted?" lookup,
            // which runs before every publication attempt and must not degrade into
            // a scan as the message log grows.
            entity.HasIndex(e => new { e.CorrelationId, e.Direction, e.Verb, e.Noun });

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

            case "cms":
                ConfigureCms(modelBuilder);
                break;
        }
    }

    /// <summary>
    /// The maintenance system, mapped to the customer's actual schema.
    ///
    /// Table and column names are the customer's, not ours, and no column exists
    /// here that they did not define. In particular there is no FederationId and no
    /// Cirid: MMS stores no shared identity at all, and everything cross-system is
    /// resolved through ws-CIR against LIGHT_SYSTEM_ID at read time.
    ///
    /// The SQL schema is deliberately left to HasDefaultSchema rather than pinned to
    /// dbo. In the customer's database these live in dbo; in the sandbox each
    /// participant is isolated into its own schema and connects as a contained user
    /// granted only on that schema, so pinning dbo here would make every MMS query
    /// fail on permissions. The names are what fidelity requires; the schema is
    /// deployment context.
    /// </summary>
    private static void ConfigureMms(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LightSystemInventory>(entity =>
        {
            entity.ToTable("LIGHT_SYSTEM_INVENTORY");

            // Assigned, never generated: the sandbox allocates the next value
            // explicitly so the insert and its CIR registration stay together.
            entity.HasKey(e => e.LightSystemId);
            entity.Property(e => e.LightSystemId)
                .HasColumnName("LIGHT_SYSTEM_ID")
                .ValueGeneratedNever();

            entity.Property(e => e.LightSystemName)
                .HasColumnName("LIGHT_SYSTEM_NAME").HasMaxLength(100).IsRequired();
            entity.Property(e => e.LightSystemClassCodeId)
                .HasColumnName("LIGHT_SYSTEM_CLASS_CODE_ID").IsRequired();
            entity.Property(e => e.LightSystemStatusId)
                .HasColumnName("LIGHT_SYSTEM_STATUS_ID");
            entity.Property(e => e.OwnerId)
                .HasColumnName("OWNER_ID");
        });

        modelBuilder.Entity<LightSystemClassCode>(entity =>
        {
            entity.ToTable("LIGHT_SYSTEM_CLASS_CODE");
            entity.HasKey(e => e.LightSystemClassCodeId);
            entity.Property(e => e.LightSystemClassCodeId)
                .HasColumnName("LIGHT_SYSTEM_CLASS_CODE_ID").ValueGeneratedNever();
            entity.Property(e => e.LightSystemClassCodeName)
                .HasColumnName("LIGHT_SYSTEM_CLASS_CODE_NAME").HasMaxLength(200).IsRequired();
            entity.Property(e => e.ActiveFlag).HasColumnName("ACTIVE_FLAG").IsRequired();
            entity.Property(e => e.UserUpdate).HasColumnName("USER_UPDATE").HasMaxLength(100);
            entity.Property(e => e.DateUpdate).HasColumnName("DATE_UPDATE").HasPrecision(3);
        });

        modelBuilder.Entity<SetupAssetStatus>(entity =>
        {
            entity.ToTable("SETUP_ASSET_STATUS");
            entity.HasKey(e => e.AssetStatusId);
            entity.Property(e => e.AssetStatusId)
                .HasColumnName("ASSET_STATUS_ID").ValueGeneratedNever();
            entity.Property(e => e.AssetStatusName)
                .HasColumnName("ASSET_STATUS_NAME").HasMaxLength(200).IsRequired();
            entity.Property(e => e.ActiveFlag).HasColumnName("ACTIVE_FLAG").IsRequired();
            entity.Property(e => e.UserUpdate).HasColumnName("USER_UPDATE").HasMaxLength(100);
            entity.Property(e => e.DateUpdate).HasColumnName("DATE_UPDATE").HasPrecision(3);
        });

        modelBuilder.Entity<SetupOwner>(entity =>
        {
            entity.ToTable("SETUP_OWNER");
            entity.HasKey(e => e.OwnerId);
            entity.Property(e => e.OwnerId)
                .HasColumnName("OWNER_ID").ValueGeneratedNever();
            entity.Property(e => e.OwnerName)
                .HasColumnName("OWNER_NAME").HasMaxLength(200).IsRequired();
            entity.Property(e => e.ActiveFlag).HasColumnName("ACTIVE_FLAG").IsRequired();
            entity.Property(e => e.UserUpdate).HasColumnName("USER_UPDATE").HasMaxLength(100);
            entity.Property(e => e.DateUpdate).HasColumnName("DATE_UPDATE").HasPrecision(3);
        });

        // Sandbox-only below this line: no customer table has been supplied for
        // equipment, relationships or work orders, so these keep their original
        // shape rather than pretending to a fidelity they do not have.
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

            // Same exemption as EquipmentRecord: MMS adopts identity rather
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
    /// CMS holds the event log plus the asset and location records it builds from
    /// that log. It has no reference data of its own: everything it knows arrives by
    /// publication, which is what makes the unmapped-value behaviour visible here
    /// rather than hidden behind a local model that happens to agree.
    /// </summary>
    private static void ConfigureCms(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ContextOwnerRecord>(entity =>
        {
            entity.ToTable("ContextOwner");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OwnerCode).HasMaxLength(32).IsRequired();
            entity.Property(e => e.OwnerName).HasMaxLength(200).IsRequired();

            // Unique on CMS's own code, mirroring the OWNER_ID uniqueness a real
            // O&M system enforces on its owner domain table.
            entity.HasIndex(e => e.OwnerCode).IsUnique();

            // Deliberately not unique: until equivalence is asserted every row here
            // carries a null Cirid, and a unique constraint would permit exactly one
            // unresolved owner.
            entity.HasIndex(e => e.Cirid);
        });

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
            entity.Property(e => e.OwnerCode).HasMaxLength(32);
            entity.Property(e => e.ForeignOwnerSourceId).HasMaxLength(200);
            entity.Property(e => e.ForeignOwnerIdInSource).HasMaxLength(200);

            // Unique on the event identity, not on asset+location: the same asset
            // legitimately returns to the same location after a workshop repair, and a
            // composite constraint would reject the second installation as a duplicate.
            entity.HasIndex(e => e.FederationId).IsUnique();
            entity.HasIndex(e => new { e.AssetFederationId, e.OccurredAt });
            entity.HasIndex(e => e.LocationFederationId);
            entity.HasIndex(e => e.Cirid);
            entity.HasIndex(e => e.OwnerCode);
        });

        modelBuilder.Entity<MonitoredLocationRecord>(entity =>
        {
            entity.ToTable("MonitoredLocationRecord");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.LocationCode).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Designation).HasMaxLength(400);
            entity.Property(e => e.ForeignSourceId).HasMaxLength(200);
            entity.Property(e => e.ForeignIdInSource).HasMaxLength(200);
            entity.Property(e => e.OwnerCode).HasMaxLength(32);
            entity.Property(e => e.ForeignOwnerSourceId).HasMaxLength(200);
            entity.Property(e => e.ForeignOwnerIdInSource).HasMaxLength(200);
            entity.HasIndex(e => e.LocationCode).IsUnique();
            entity.HasIndex(e => new { e.ForeignSourceId, e.ForeignIdInSource });
            entity.HasIndex(e => e.Cirid);
            entity.HasIndex(e => e.OwnerCode);

            // Same exemption as MMS: CMS adopts identity rather than minting it, so a
            // publisher that asserted none leaves this empty, and more than one such
            // record must remain possible.
            entity.HasIndex(e => e.FederationId)
                .IsUnique()
                .HasFilter("[FederationId] <> '00000000-0000-0000-0000-000000000000'");
        });

        modelBuilder.Entity<MonitoredAssetRecord>(entity =>
        {
            entity.ToTable("MonitoredAssetRecord");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AssetCode).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Designation).HasMaxLength(400);
            entity.Property(e => e.SerialNumber).HasMaxLength(64);
            entity.Property(e => e.InstalledAtLocationCode).HasMaxLength(32);
            entity.Property(e => e.ForeignSourceId).HasMaxLength(200);
            entity.Property(e => e.ForeignIdInSource).HasMaxLength(200);
            entity.Property(e => e.OwnerCode).HasMaxLength(32);
            entity.Property(e => e.ForeignOwnerSourceId).HasMaxLength(200);
            entity.Property(e => e.ForeignOwnerIdInSource).HasMaxLength(200);
            entity.HasIndex(e => e.AssetCode).IsUnique();
            entity.HasIndex(e => new { e.ForeignSourceId, e.ForeignIdInSource });
            entity.HasIndex(e => e.InstalledAtLocationCode);
            entity.HasIndex(e => e.Cirid);
            entity.HasIndex(e => e.OwnerCode);

            // Unlike MMS, CMS never originates an asset — it learns of one only when
            // an event names it — so an unidentified asset is possible here and the
            // constraint needs the same empty-Guid exemption.
            entity.HasIndex(e => e.FederationId)
                .IsUnique()
                .HasFilter("[FederationId] <> '00000000-0000-0000-0000-000000000000'");
        });

        ConfigureCmsCustomerTables(modelBuilder);
    }

    /// <summary>
    /// The CMS customer schema, from <c>docs/DDL/CMS.SQL</c>.
    ///
    /// Split into its own method and named in UPPER_CASE because these tables are not
    /// the sandbox's to change. Everything mapped above is participant spine or a
    /// sandbox-native record; everything mapped here belongs to the customer, and the
    /// casing is what makes that boundary visible at the call site rather than
    /// something a reader has to know. The no-join rule between the two categories
    /// applies exactly as it does for MMS.
    ///
    /// Only the site and asset side of the LOCATION AND ASSET block is mapped.
    /// <c>cms.Location</c> is the customer's own plant hierarchy and bears no relation
    /// to the functional locations arriving from REG-LOCATION, so it is deliberately
    /// not modelled — and consequently <c>ASSET.LocationID</c> is not mapped either,
    /// since there is nothing for it to point at. <c>SITE</c> is mapped, because the
    /// segment's RegistrationSite genuinely does correspond to it.
    ///
    /// As with MMS, the SQL schema is left to HasDefaultSchema rather than pinned to
    /// the DDL's <c>cms</c>: in the sandbox each participant is isolated into its own
    /// schema and connects as a contained user granted only there.
    /// </summary>
    private static void ConfigureCmsCustomerTables(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CmsSite>(entity =>
        {
            entity.ToTable("SITE");
            entity.HasKey(e => e.SiteId);

            entity.Property(e => e.SiteId).HasColumnName("SiteID").ValueGeneratedOnAdd();

            // Not generated, despite the DDL's NEWID() default. The value is the
            // publisher's twin id, and letting the database mint one instead would
            // produce a site nobody else can recognise.
            entity.Property(e => e.SiteUuid).HasColumnName("SiteUUID").ValueGeneratedNever();

            entity.Property(e => e.SiteCode).HasColumnName("SiteCode").HasMaxLength(100).IsRequired();
            entity.Property(e => e.SiteName).HasColumnName("SiteName").HasMaxLength(255).IsRequired();
            entity.Property(e => e.Description).HasColumnName("Description").HasMaxLength(1000);
            entity.Property(e => e.CreatedAtUtc).HasColumnName("CreatedAtUTC");

            entity.HasIndex(e => e.SiteUuid).IsUnique().HasDatabaseName("UQ_Site_UUID");
            entity.HasIndex(e => e.SiteCode).IsUnique().HasDatabaseName("UQ_Site_Code");
        });

        modelBuilder.Entity<CmsAsset>(entity =>
        {
            entity.ToTable("ASSET");
            entity.HasKey(e => e.AssetId);

            // IDENTITY in the DDL, so the database allocates it. This is the one place
            // CMS differs usefully from MMS: LIGHT_SYSTEM_ID had to be allocated as
            // MAX+1 in application code because that column is not an identity.
            entity.Property(e => e.AssetId).HasColumnName("AssetID").ValueGeneratedOnAdd();

            entity.Property(e => e.SiteId).HasColumnName("SiteID");
            entity.Property(e => e.ParentAssetId).HasColumnName("ParentAssetID");
            entity.Property(e => e.AssetClassId).HasColumnName("AssetClassID");

            entity.Property(e => e.AssetTag).HasColumnName("AssetTag").HasMaxLength(100).IsRequired();
            entity.Property(e => e.AssetName).HasColumnName("AssetName").HasMaxLength(255).IsRequired();
            entity.Property(e => e.Description).HasColumnName("Description").HasMaxLength(1000);

            entity.Property(e => e.SerialNumber).HasColumnName("SerialNumber").HasMaxLength(100);
            entity.Property(e => e.Manufacturer).HasColumnName("Manufacturer").HasMaxLength(255);
            entity.Property(e => e.Model).HasColumnName("Model").HasMaxLength(255);
            entity.Property(e => e.CommissionDate).HasColumnName("CommissionDate").HasColumnType("date");

            entity.Property(e => e.OperationalStatus).HasColumnName("OperationalStatus").HasMaxLength(50);
            entity.Property(e => e.CriticalityLevel).HasColumnName("CriticalityLevel").HasMaxLength(50);

            entity.Property(e => e.CreatedAtUtc).HasColumnName("CreatedAtUTC");
            entity.Property(e => e.UpdatedAtUtc).HasColumnName("UpdatedAtUTC");

            // UQ_Asset_Tag. This is the alternate key the segment handler matches on,
            // because the schema offers nothing better: no foreign id column exists
            // and none may be added.
            entity.HasIndex(e => e.AssetTag).IsUnique().HasDatabaseName("UQ_Asset_Tag");

            // Required, matching the DDL's NOT NULL. An asset CMS cannot place at a
            // site is not stored at all.
            entity.HasOne<CmsSite>()
                .WithMany()
                .HasForeignKey(e => e.SiteId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_Asset_Site");

            entity.HasOne<CmsAsset>()
                .WithMany()
                .HasForeignKey(e => e.ParentAssetId)
                .HasConstraintName("FK_Asset_Parent");

            entity.HasOne<CmsAssetClass>()
                .WithMany()
                .HasForeignKey(e => e.AssetClassId)
                .HasConstraintName("FK_Asset_AssetClass");
        });

        modelBuilder.Entity<CmsAssetClass>(entity =>
        {
            entity.ToTable("ASSET_CLASS");
            entity.HasKey(e => e.AssetClassId);

            entity.Property(e => e.AssetClassId).HasColumnName("AssetClassID").ValueGeneratedOnAdd();
            entity.Property(e => e.ClassCode).HasColumnName("ClassCode").HasMaxLength(100).IsRequired();
            entity.Property(e => e.ClassName).HasColumnName("ClassName").HasMaxLength(255).IsRequired();
            entity.Property(e => e.Description).HasColumnName("Description").HasMaxLength(1000);

            entity.HasIndex(e => e.ClassCode).IsUnique().HasDatabaseName("UQ_AssetClass_Code");
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
            // The originator's twin, carried through the gate so MMS can resolve an
            // owner. Sized to match the Site fields these are copied from.
            entity.Property(e => e.ContextSourceId).HasMaxLength(64);
            entity.Property(e => e.ContextIdInSource).HasMaxLength(200);
            entity.Property(e => e.ContextName).HasMaxLength(400);
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
            entity.Property(e => e.ContextSourceId).HasMaxLength(64);
            entity.Property(e => e.ContextIdInSource).HasMaxLength(200);
            entity.Property(e => e.ContextName).HasMaxLength(400);
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
