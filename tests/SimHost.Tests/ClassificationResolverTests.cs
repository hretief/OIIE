using SimHost.Application.Classification;
using SimHost.Domain.Common;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// The resolver is pure logic and the piece most worth testing directly — no
/// database, no ISBM, no Azure.
/// </summary>
public class ClassificationResolverTests
{
    private static readonly Guid EquipmentId = Guid.NewGuid();
    private static readonly Guid PumpId = Guid.NewGuid();
    private static readonly Guid CentrifugalPumpId = Guid.NewGuid();
    private static readonly Guid MagDrivePumpId = Guid.NewGuid();
    private static readonly Guid SafetyCriticalId = Guid.NewGuid();

    private static readonly Guid TagNumberDef = Guid.NewGuid();
    private static readonly Guid FlowRateDef = Guid.NewGuid();
    private static readonly Guid ShellMaterialDef = Guid.NewGuid();
    private static readonly Guid SilRatingDef = Guid.NewGuid();

    [Fact]
    public void Resolves_inherited_properties_root_downward()
    {
        var resolver = new ClassificationResolver(BuildSource());
        var set = resolver.Resolve("Asset", "ASSET-000241", DateTimeOffset.UtcNow);

        Assert.Contains(set.Properties, p => p.Definition.Id == TagNumberDef);
        Assert.Contains(set.Properties, p => p.Definition.Id == FlowRateDef);
        Assert.Equal(3, set.TaxonomyChain.Count);
    }

    [Fact]
    public void Attributes_each_property_to_the_class_that_contributed_it()
    {
        var resolver = new ClassificationResolver(BuildSource());
        var set = resolver.Resolve("Asset", "ASSET-000241", DateTimeOffset.UtcNow);

        var flowRate = set.Find(FlowRateDef);
        Assert.NotNull(flowRate);
        Assert.Equal(CentrifugalPumpId, flowRate!.ContributedByClassId);
    }

    [Fact]
    public void Aspect_classes_contribute_alongside_the_taxonomy_chain()
    {
        var resolver = new ClassificationResolver(BuildSource(includeAspect: true));
        var set = resolver.Resolve("Asset", "ASSET-000241", DateTimeOffset.UtcNow);

        Assert.Contains(set.Properties, p => p.Definition.Id == SilRatingDef);
        Assert.Single(set.AspectClasses);
    }

    [Fact]
    public void Subclass_may_promote_a_requirement()
    {
        var source = BuildSource(promoteFlowRateToRequired: true);
        var resolver = new ClassificationResolver(source);
        var set = resolver.Resolve("Asset", "ASSET-000241", DateTimeOffset.UtcNow);

        Assert.Equal(PropertyRequirement.Required, set.Find(FlowRateDef)!.Constraint.Requirement);
    }

    [Fact]
    public void Subclass_may_not_relax_a_requirement()
    {
        var source = BuildSource(relaxTagNumber: true);
        var resolver = new ClassificationResolver(source);

        var ex = Assert.Throws<NarrowingViolationException>(
            () => resolver.Resolve("Asset", "ASSET-000241", DateTimeOffset.UtcNow));

        Assert.Contains("relax", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Subclass_may_not_widen_a_numeric_bound()
    {
        var source = BuildSource(widenFlowRateMax: true);
        var resolver = new ClassificationResolver(source);

        Assert.Throws<NarrowingViolationException>(
            () => resolver.Resolve("Asset", "ASSET-000241", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Unclassified_entity_resolves_to_an_empty_set()
    {
        var resolver = new ClassificationResolver(BuildSource());
        var set = resolver.Resolve("Asset", "ASSET-NOT-CLASSIFIED", DateTimeOffset.UtcNow);

        Assert.Empty(set.Properties);
    }

    [Fact]
    public void Binder_degrades_to_the_nearest_known_ancestor()
    {
        // MMS holds Equipment/Pump/CentrifugalPump but not the mag-drive leaf.
        var binder = new ClassBinder(BuildSource(includeMagDriveLeaf: false));

        var result = binder.Bind([
            "rdl:MagneticDriveCentrifugalPump",
            "rdl:CentrifugalPump",
            "rdl:Pump",
            "rdl:Equipment"
        ]);

        Assert.NotNull(result.BoundClass);
        Assert.Equal(CentrifugalPumpId, result.BoundClass!.Id);
        Assert.True(result.IsDegraded);
        Assert.Single(result.UnknownAncestorKeys);
    }

    [Fact]
    public void Binder_reports_exact_match_when_the_leaf_is_known()
    {
        var binder = new ClassBinder(BuildSource(includeMagDriveLeaf: true));

        var result = binder.Bind([
            "rdl:MagneticDriveCentrifugalPump",
            "rdl:CentrifugalPump"
        ]);

        Assert.True(result.IsExactMatch);
        Assert.False(result.IsDegraded);
    }

    [Fact]
    public void Property_with_no_local_definition_is_inferred_and_retained()
    {
        var source = BuildSource();
        var resolver = new ClassificationResolver(source);
        var ingestor = new PropertyIngestor(source);
        var set = resolver.Resolve("Asset", "ASSET-000241", DateTimeOffset.UtcNow);

        var result = ingestor.Ingest(
            "Asset",
            "ASSET-000241",
            [
                new IncomingProperty("rdl:FlowRate", "Flow rate", PropertyDataType.Numeric, NumericValue: 120m),
                // Not in the fixture's definition list at all — the receiver has
                // never heard of it, so a stub is created and the value kept.
                new IncomingProperty("rdl:EddyCurrentLossRating", "Eddy-current loss rating",
                    PropertyDataType.Character, CharacterValue: "12.4 W")
            ],
            set,
            fromParticipant: "reg-asset",
            sourceMessageId: null,
            at: DateTimeOffset.UtcNow);

        Assert.Equal(2, result.Values.Count);
        Assert.Equal(1, result.MappedCount);
        Assert.Equal(1, result.UnmappedCount);

        var inferred = Assert.Single(result.InferredDefinitions);
        Assert.Equal(DefinitionOrigin.Inferred, inferred.Origin);
        Assert.Equal("reg-asset", inferred.ReceivedFrom);
        Assert.Contains(result.Values, v => !v.Mapped && v.CharacterValue == "12.4 W");
    }

    [Fact]
    public void Known_definition_that_no_class_sanctions_is_unmapped_without_a_stub()
    {
        // A distinct condition from the test above, and one the UI must not conflate:
        // the receiver holds the definition, but the entity's classification does
        // not confer it. Nothing is inferred, and the value is still retained.
        var source = BuildSource();
        var resolver = new ClassificationResolver(source);
        var ingestor = new PropertyIngestor(source);
        var set = resolver.Resolve("Asset", "ASSET-000241", DateTimeOffset.UtcNow);

        var result = ingestor.Ingest(
            "Asset",
            "ASSET-000241",
            [
                new IncomingProperty("rdl:ContainmentShellMaterial", "Containment shell material",
                    PropertyDataType.Character, CharacterValue: "Hastelloy C-276")
            ],
            set,
            fromParticipant: "reg-asset",
            sourceMessageId: null,
            at: DateTimeOffset.UtcNow);

        Assert.Single(result.Values);
        Assert.Equal(0, result.MappedCount);
        Assert.Equal(1, result.UnmappedCount);
        Assert.Empty(result.InferredDefinitions);
        Assert.Contains(result.Values, v => !v.Mapped && v.CharacterValue == "Hastelloy C-276");
    }

    [Fact]
    public void Reclassification_orphans_rather_than_deletes()
    {
        var values = new List<EntityPropertyValue>
        {
            new() { DefinitionId = FlowRateDef, NumericValue = 120m },
            new() { DefinitionId = ShellMaterialDef, CharacterValue = "Hastelloy" }
        };

        var narrowerSet = new EffectivePropertySet(
            [
                new EffectiveProperty(
                    new PropertyDefinition { Id = FlowRateDef, Name = "Flow rate" },
                    new ClassProperty { DefinitionId = FlowRateDef },
                    PumpId, "Pump", ClassKind.Taxonomy, 1)
            ],
            [],
            []);

        var orphaned = PropertyIngestor.MarkOrphaned(values, narrowerSet);

        Assert.Equal(1, orphaned);
        Assert.True(values.Single(v => v.DefinitionId == ShellMaterialDef).Orphaned);
        Assert.Equal("Hastelloy", values.Single(v => v.DefinitionId == ShellMaterialDef).CharacterValue);
    }

    // --- fixture -----------------------------------------------------------

    private static InMemoryClassificationSource BuildSource(
        bool includeAspect = false,
        bool includeMagDriveLeaf = false,
        bool promoteFlowRateToRequired = false,
        bool relaxTagNumber = false,
        bool widenFlowRateMax = false)
    {
        var classes = new List<ClassDefinition>
        {
            New(EquipmentId, "rdl:Equipment", "Equipment", null),
            New(PumpId, "rdl:Pump", "Pump", EquipmentId),
            New(CentrifugalPumpId, "rdl:CentrifugalPump", "Centrifugal Pump", PumpId)
        };

        if (includeMagDriveLeaf)
        {
            classes.Add(New(MagDrivePumpId, "rdl:MagneticDriveCentrifugalPump",
                "Magnetic Drive Centrifugal Pump", CentrifugalPumpId));
        }

        if (includeAspect)
        {
            var aspect = New(SafetyCriticalId, "rdl:SafetyCritical", "Safety Critical", null);
            aspect.Kind = ClassKind.Aspect;
            classes.Add(aspect);
        }

        var definitions = new List<PropertyDefinition>
        {
            new() { Id = TagNumberDef, DefinitionKey = "rdl:TagNumber", Name = "Tag number",
                DataType = PropertyDataType.Character, Origin = DefinitionOrigin.Rdl },
            new() { Id = FlowRateDef, DefinitionKey = "rdl:FlowRate", Name = "Flow rate",
                DataType = PropertyDataType.Numeric, UnitOfMeasure = "m3/h", Origin = DefinitionOrigin.Rdl },
            new() { Id = ShellMaterialDef, DefinitionKey = "rdl:ContainmentShellMaterial",
                Name = "Containment shell material", DataType = PropertyDataType.Character,
                Origin = DefinitionOrigin.Rdl },
            new() { Id = SilRatingDef, DefinitionKey = "rdl:SilRating", Name = "SIL rating",
                DataType = PropertyDataType.Character, Origin = DefinitionOrigin.Rdl }
        };

        var classProperties = new List<ClassProperty>
        {
            new()
            {
                Id = 1, ClassId = EquipmentId, DefinitionId = TagNumberDef,
                Requirement = PropertyRequirement.Required
            },
            new()
            {
                Id = 2, ClassId = CentrifugalPumpId, DefinitionId = FlowRateDef,
                Requirement = promoteFlowRateToRequired
                    ? PropertyRequirement.Required
                    : PropertyRequirement.Recommended,
                MinValue = 0m,
                MaxValue = 5000m
            }
        };

        if (relaxTagNumber)
        {
            classProperties.Add(new ClassProperty
            {
                Id = 3, ClassId = PumpId, DefinitionId = TagNumberDef,
                Requirement = PropertyRequirement.Optional
            });
        }

        if (widenFlowRateMax && includeMagDriveLeaf)
        {
            classProperties.Add(new ClassProperty
            {
                Id = 4, ClassId = MagDrivePumpId, DefinitionId = FlowRateDef,
                Requirement = PropertyRequirement.Recommended,
                MaxValue = 99999m
            });
        }
        else if (widenFlowRateMax)
        {
            classes.Add(New(MagDrivePumpId, "rdl:MagneticDriveCentrifugalPump",
                "Magnetic Drive Centrifugal Pump", CentrifugalPumpId));
            classProperties.Add(new ClassProperty
            {
                Id = 4, ClassId = MagDrivePumpId, DefinitionId = FlowRateDef,
                Requirement = PropertyRequirement.Recommended,
                MaxValue = 99999m
            });
        }

        var primaryClassId = widenFlowRateMax ? MagDrivePumpId : CentrifugalPumpId;

        var classifications = new List<EntityClassification>
        {
            new()
            {
                Id = 1, EntityType = "Asset", EntityKey = "ASSET-000241",
                ClassId = primaryClassId, IsPrimary = true,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1)
            }
        };

        if (includeAspect)
        {
            classProperties.Add(new ClassProperty
            {
                Id = 5, ClassId = SafetyCriticalId, DefinitionId = SilRatingDef,
                Requirement = PropertyRequirement.Required
            });

            classifications.Add(new EntityClassification
            {
                Id = 2, EntityType = "Asset", EntityKey = "ASSET-000241",
                ClassId = SafetyCriticalId, IsPrimary = false,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1)
            });
        }

        return new InMemoryClassificationSource(classes, definitions, classProperties, classifications);
    }

    private static ClassDefinition New(Guid id, string key, string name, Guid? parent) =>
        new()
        {
            Id = id,
            ClassKey = key,
            Name = name,
            ParentClassId = parent,
            AppliesTo = "Asset",
            Kind = ClassKind.Taxonomy,
            Origin = DefinitionOrigin.Rdl,
            Version = "1"
        };
}
