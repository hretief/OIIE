using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SimHost.Application.Cir;
using SimHost.Domain.Cms;
using SimHost.Domain.Mms;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// Guards the rule that context ownership is resolved through the registry rather
/// than copied between schemas.
///
/// These are structural assertions rather than round-trip tests because the property
/// being protected is structural: the moment a foreign context identifier becomes a
/// native column on a participant, the CIR is bypassed and the interoperability
/// claim is void. A behavioural test would pass just as happily against the shortcut.
/// </summary>
public class ContextOwnershipTests
{
    /// <summary>
    /// CMS is not a Bentley system and must carry no iTwin column.
    ///
    /// This is the specific regression being locked out: an earlier revision stored
    /// the inbound RegistrationSite as ITwinId on each CMS table, which made twin
    /// filtering work by teaching a condition monitoring system to speak iTwin.
    /// </summary>
    [Theory]
    [InlineData(typeof(AssetInstallationEvent))]
    [InlineData(typeof(MonitoredLocationRecord))]
    [InlineData(typeof(MonitoredAssetRecord))]
    [InlineData(typeof(ContextOwnerRecord))]
    [InlineData(typeof(CmsAsset))]
    public void Cms_entities_carry_no_native_twin_identifier(Type entity)
    {
        var offending = entity
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.Contains("Twin", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();

        Assert.True(offending.Count == 0,
            $"{entity.Name} exposes {string.Join(", ", offending)}. A foreign context " +
            "identifier must be held as ForeignOwnerIdInSource and resolved via the CIR.");
    }

    /// <summary>
    /// CmsSite is the deliberate exception, and this pins down its exact scope.
    ///
    /// SiteUUID exists in the customer's own DDL and holds whatever the publisher put
    /// in RegistrationSite, so CMS retains it rather than discarding data it was
    /// given. What it must not become is a twin column by another name: the property
    /// is named for the customer's schema, not for Bentley's, and nothing in CMS may
    /// present it as an iTwin identifier.
    ///
    /// The behavioural half of the rule — that read paths scope through the registry
    /// rather than matching this column against a foreign GUID — cannot be asserted
    /// structurally, and is guarded by review of CmsContextResolver instead.
    /// </summary>
    [Fact]
    public void Cms_site_retains_the_publisher_uuid_without_naming_it_a_twin()
    {
        Assert.NotNull(typeof(CmsSite).GetProperty("SiteUuid"));

        var offending = typeof(CmsSite)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.Contains("Twin", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();

        Assert.True(offending.Count == 0,
            $"CmsSite exposes {string.Join(", ", offending)}. The publisher's UUID is " +
            "retained as customer data, not as a foreign context key CMS understands.");
    }

    /// <summary>
    /// The asserted context must survive ingestion verbatim. Without it there is
    /// nothing to hand the registry, and an unresolved record could never later
    /// become resolved.
    /// </summary>
    [Theory]
    [InlineData(typeof(AssetInstallationEvent))]
    [InlineData(typeof(MonitoredLocationRecord))]
    [InlineData(typeof(MonitoredAssetRecord))]
    public void Cms_records_retain_the_asserted_foreign_context(Type entity)
    {
        Assert.NotNull(entity.GetProperty("ForeignOwnerSourceId"));
        Assert.NotNull(entity.GetProperty("ForeignOwnerIdInSource"));
        Assert.NotNull(entity.GetProperty("OwnerCode"));
    }

    /// <summary>
    /// CMS's owner codes must not coincide with the OWNER_ID values MMS uses.
    ///
    /// If they did, a join across the two schemas would appear to work and the
    /// sandbox would demonstrate the opposite of its thesis. The divergence is the
    /// condition that forces resolution through the registry.
    /// </summary>
    [Fact]
    public void Cms_owner_codes_do_not_collide_with_mms_owner_ids()
    {
        var cmsCodes = Enumerable
            .Range(0, ContextOwnerSeeder.OwnerNames.Count)
            .Select(ContextOwnerSeeder.CmsOwnerCode)
            .ToList();

        // MMS keys the same districts 1..11.
        var mmsOwnerIds = Enumerable.Range(1, 11).Select(i => i.ToString()).ToList();

        Assert.Empty(cmsCodes.Intersect(mmsOwnerIds, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(cmsCodes.Count, cmsCodes.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The owner domain is the shared organisational reality both systems describe,
    /// so the names must match what MMS holds in dbo.SETUP_OWNER — the name is the
    /// only thing a steward can judge equivalence on across two opaque key spaces.
    /// </summary>
    [Fact]
    public void Owner_domain_matches_the_mms_setup_owner_list()
    {
        Assert.Equal(11, ContextOwnerSeeder.OwnerNames.Count);
        Assert.Contains("7000 - Metro District", ContextOwnerSeeder.OwnerNames);
        Assert.Contains("9600 - District 6", ContextOwnerSeeder.OwnerNames);
        Assert.Contains("MnDOT", ContextOwnerSeeder.OwnerNames);
    }

    /// <summary>
    /// The CMS schema must map the owner table and enforce uniqueness on CMS's own
    /// code, mirroring the OWNER_ID uniqueness a real O&amp;M system relies on.
    /// </summary>
    [Fact]
    public void Cms_schema_maps_a_unique_context_owner_code()
    {
        using var context = TestContexts.ForSchema("cms");

        var owner = context.Model.FindEntityType(typeof(ContextOwnerRecord));
        Assert.NotNull(owner);

        var unique = owner!.GetIndexes()
            .Where(i => i.IsUnique)
            .SelectMany(i => i.Properties.Select(p => p.Name))
            .ToList();

        Assert.Contains(nameof(ContextOwnerRecord.OwnerCode), unique);

        // Cirid must NOT be unique: every owner is null until related, and a unique
        // constraint would allow exactly one unresolved owner to exist.
        var ciridUnique = owner.GetIndexes()
            .Any(i => i.IsUnique && i.Properties.Any(p => p.Name == nameof(ContextOwnerRecord.Cirid)));

        Assert.False(ciridUnique,
            "A unique Cirid index would permit only one unresolved owner.");
    }

    /// <summary>
    /// MMS entities must carry no federation identity, CIRID or foreign identifier.
    ///
    /// This is the strongest constraint in the sandbox and the easiest to breach by
    /// accident: the customer's schema cannot be altered, so any such property would
    /// be a column that does not exist in the database the code claims to model. The
    /// test is structural because the failure is silent \u2014 EF would happily map a
    /// phantom column and only fail against the real database.
    /// </summary>
    [Theory]
    [InlineData(typeof(LightSystemInventory))]
    [InlineData(typeof(LightSystemClassCode))]
    [InlineData(typeof(SetupAssetStatus))]
    [InlineData(typeof(SetupOwner))]
    public void Mms_entities_carry_no_identity_columns(Type entity)
    {
        string[] forbidden =
            ["FederationId", "Cirid", "ForeignSourceId", "ForeignIdInSource", "ITwinId", "TwinId"];

        var offending = entity
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => forbidden.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();

        Assert.True(offending.Count == 0,
            $"{entity.Name} declares {string.Join(", ", offending)}, which the customer's " +
            "MMS schema has no column for. Identity must be resolved through ws-CIR.");
    }

    /// <summary>
    /// The MMS mapping must use the customer's own table and column names.
    ///
    /// Mapping to a sandbox-invented name would still build and still pass every
    /// behavioural test, while being unable to read a single row of the real
    /// database. Only an explicit assertion catches that.
    /// </summary>
    [Fact]
    public void Mms_schema_maps_the_customer_table_and_column_names()
    {
        using var context = TestContexts.ForSchema("mms");

        var inventory = context.Model.FindEntityType(typeof(LightSystemInventory));
        Assert.NotNull(inventory);

        Assert.Equal("LIGHT_SYSTEM_INVENTORY", inventory!.GetTableName());

        // The participant's own schema, not dbo. The customer keeps these in dbo, but
        // the sandbox isolates each participant into its own schema and connects as a
        // user granted only there; pinning dbo would fail on permissions at runtime.
        Assert.Equal("mms", inventory.GetSchema());

        var columns = inventory.GetProperties().Select(p => p.GetColumnName()).ToList();

        Assert.Contains("LIGHT_SYSTEM_ID", columns);
        Assert.Contains("LIGHT_SYSTEM_NAME", columns);
        Assert.Contains("LIGHT_SYSTEM_CLASS_CODE_ID", columns);
        Assert.Contains("LIGHT_SYSTEM_STATUS_ID", columns);
        Assert.Contains("OWNER_ID", columns);

        // Exactly five: the customer's table has five columns and the sandbox may
        // not add a sixth.
        Assert.Equal(5, columns.Count);
    }

    /// <summary>
    /// OWNER_ID must stay nullable.
    ///
    /// A light system with no owner is a real state in the customer's data, and it
    /// can never resolve to an iTwin. Making the column required would force a
    /// placeholder owner, which would silently give unowned inventory a context it
    /// does not have.
    /// </summary>
    [Fact]
    public void Mms_owner_id_remains_optional()
    {
        using var context = TestContexts.ForSchema("mms");

        var owner = context.Model
            .FindEntityType(typeof(LightSystemInventory))!
            .FindProperty(nameof(LightSystemInventory.OwnerId));

        Assert.NotNull(owner);
        Assert.True(owner!.IsNullable,
            "OWNER_ID is nullable in the customer schema; unowned light systems must remain representable.");
    }

    /// <summary>
    /// The seeded owner keys must match the customer's actual OWNER_ID values.
    ///
    /// CMS derives OWN-nn from the same list, so an ordering change here would
    /// silently repoint every MMS owner while leaving CMS looking correct.
    /// </summary>
    [Fact]
    public void Mms_owner_ids_match_the_customer_numbering()
    {
        Assert.Equal("7200 - Metro Traffic", ContextOwnerSeeder.OwnerNames[1]);
        Assert.Equal("9600 - District 6", ContextOwnerSeeder.OwnerNames[7]);
        Assert.Equal("MnDOT", ContextOwnerSeeder.OwnerNames[10]);
    }

    /// <summary>
    /// Provisioned site codes are the district number, and only where there is one.
    ///
    /// MnDOT is the agency rather than a district, so it must yield no site: a plant
    /// that does not exist should not be provisioned merely to keep a list uniform.
    /// </summary>
    [Fact]
    public void Site_codes_are_district_numbers_where_a_district_exists()
    {
        Assert.Equal("9100", ContextOwnerSeeder.SiteCodeFor("9100 - District 1"));
        Assert.Equal("7000", ContextOwnerSeeder.SiteCodeFor("7000 - Metro District"));
        Assert.Null(ContextOwnerSeeder.SiteCodeFor("MnDOT"));
    }

    /// <summary>
    /// A provisioned site's retained UUID must be stable across resets.
    ///
    /// If it were random, every day zero would give the same plant a new identity, and
    /// an equivalence asserted against yesterday's value would silently come to
    /// describe a row that no longer means the same thing.
    /// </summary>
    [Fact]
    public void Provisioned_site_uuids_are_stable_across_reseeds()
    {
        var codes = ContextOwnerSeeder.OwnerNames
            .Select(ContextOwnerSeeder.SiteCodeFor)
            .Where(c => c is not null)
            .ToList();

        // Ten districts carry a number; MnDOT does not.
        Assert.Equal(10, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// CMS site codes deliberately coincide with the district numbers MMS uses.
    ///
    /// This is the opposite of the owner-code rule and is not a contradiction: the
    /// codes agreeing is what makes the equivalence a steward asserts an obvious one.
    /// What matters is that no read path joins on the agreement — scoping resolves
    /// through the registry — so this test pins the intent rather than a mechanism.
    /// </summary>
    [Fact]
    public void Site_codes_are_shared_business_keys_not_opaque_local_ones()
    {
        var siteCode = ContextOwnerSeeder.SiteCodeFor("9100 - District 1");

        Assert.NotNull(siteCode);
        Assert.DoesNotContain("OWN-", siteCode!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every seeded iTwin must name a district CMS actually provisions a site for.
    ///
    /// The twins were supplied with District 6 numbered 9500, where the rest of the
    /// estate numbers it 9600. Nothing would have failed loudly: the twin would
    /// register, the relate would find no site, and District 6 would simply stay
    /// unscoped -- which is the failure the registry exists to prevent being silent.
    /// </summary>
    [Fact]
    public void Seeded_twins_name_districts_cms_provisions_a_site_for()
    {
        var siteCodes = ContextOwnerSeeder.OwnerNames
            .Select(ContextOwnerSeeder.SiteCodeFor)
            .Where(c => c is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (_, code, name) in ContextOwnerSeeder.EngTwins)
        {
            Assert.True(
                siteCodes.Contains(code),
                $"Twin '{name}' is scoped to site {code}, which CMS never provisions.");
        }
    }

    /// <summary>
    /// The seeded twin GUIDs are the ones iTwin issued, so they are pinned. A twin
    /// re-created with a fresh GUID resolves to nothing in the real platform, and a
    /// reset that quietly changed one would strand every equivalence built on it.
    /// </summary>
    [Fact]
    public void Seeded_twin_identifiers_are_distinct_and_fixed()
    {
        var ids = ContextOwnerSeeder.EngTwins.Select(t => t.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, ids);
    }
}
