using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using MmsProvider.Application;
using MmsProvider.Infrastructure.Sql;
using Xunit;

namespace SimHost.Tests;

public class MmsLightUnitSqlTests
{
    internal const string ConnectionVariable = "OIIE_MMS_TEST_SQL_CONNECTION_STRING";

    [MmsSqlFact]
    public async Task Footprints_can_be_ungrouped_and_later_updates_preserve_MMS_grouping()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable)!;
        var store = new SqlMmsAssetStore(Options.Create(new MmsOptions
        {
            SqlConnectionString = connectionString
        }));
        var unitId = Guid.NewGuid();
        var systemA = Guid.NewGuid();
        var systemB = Guid.NewGuid();
        var unit = new LightUnitUpsert(unitId, $"Test-{unitId:N}", null, null, null, null, null);

        try
        {
            var inserted = await store.UpsertLightUnitsAsync([unit], CancellationToken.None);
            Assert.Empty(inserted.Rejections);
            var original = Assert.Single(inserted.Units);
            Assert.True(original.Created);
            Assert.True(original.LightUnitId > 0);
            Assert.Null((await store.FindLightUnitAsync(original.LightUnitId, CancellationToken.None))!.LightSystemId);

            var classes = await store.GetLightSystemClassCodesAsync(CancellationToken.None);
            Assert.NotEmpty(classes);
            var systems = await store.UpsertLightSystemsAsync(
                [
                    new(systemA, $"Test-{systemA:N}", classes[0].Id, null, null, null, null),
                    new(systemB, $"Test-{systemB:N}", classes[0].Id, null, null, null, null)
                ], CancellationToken.None);
            Assert.Empty(systems.Rejections);
            Assert.Equal(2, systems.Systems.Count);

            foreach (var system in systems.Systems)
            {
                var assigned = await store.UpsertLightUnitsAsync(
                    [unit with { LightSystemId = system.LightSystemId }], CancellationToken.None);
                Assert.Empty(assigned.Rejections);
                Assert.Equal(original.LightUnitId, Assert.Single(assigned.Units).LightUnitId);

                var renamed = unit with { LightUnitName = $"Renamed-{unitId:N}" };
                var updated = await store.UpsertLightUnitsAsync([renamed], CancellationToken.None);
                Assert.Empty(updated.Rejections);
                Assert.Equal(original.LightUnitId, Assert.Single(updated.Units).LightUnitId);
                Assert.False(updated.Units[0].Created);

                var persisted = await store.FindLightUnitByExtAssetIdAsync(unitId, CancellationToken.None);
                Assert.NotNull(persisted);
                Assert.Equal(system.LightSystemId, persisted.LightSystemId);
                Assert.Equal(renamed.LightUnitName, persisted.LightUnitName);
            }

            var mixed = await store.UpsertLightUnitsAsync(
                [
                    unit with { LightSystemId = 0 },
                    unit with { LightSystemId = -1 },
                    unit
                ], CancellationToken.None);
            Assert.Equal(2, mixed.Rejections.Count);
            Assert.All(mixed.Rejections, r => Assert.False(r.Transient));
            Assert.Equal(original.LightUnitId, Assert.Single(mixed.Units).LightUnitId);
            Assert.Equal(systems.Systems[1].LightSystemId,
                (await store.FindLightUnitAsync(original.LightUnitId, CancellationToken.None))!.LightSystemId);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var cleanup = new SqlCommand(
                """
                DELETE FROM dbo.LIGHT_UNIT_INVENTORY WHERE EXT_ASSET_ID = @unit;
                DELETE FROM dbo.LIGHT_SYSTEM_INVENTORY WHERE EXT_ASSET_ID IN (@systemA, @systemB);
                """, connection);
            cleanup.Parameters.AddWithValue("@unit", unitId.ToString());
            cleanup.Parameters.AddWithValue("@systemA", systemA.ToString());
            cleanup.Parameters.AddWithValue("@systemB", systemB.ToString());
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}

public sealed class MmsSqlFactAttribute : FactAttribute
{
    public MmsSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(MmsLightUnitSqlTests.ConnectionVariable)))
        {
            Skip = $"Set {MmsLightUnitSqlTests.ConnectionVariable} to a dedicated, schema-initialized MMS test database.";
        }
    }
}
