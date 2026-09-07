using EngProvider.Application;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EngProvider.Infrastructure.Sql;

/// <summary>
/// IEngDesignStore over Azure SQL.
///
/// Hand-written ADO.NET rather than EF: this project emulates a customer system,
/// and a customer system's data access is not the integrator's to model.
///
/// Two constraints from the schema shape everything below.
///
/// First, dbo.Element carries an AFTER trigger, and a table with an enabled
/// trigger cannot be the target of a bare OUTPUT clause (error 334). Inserts here
/// use OUTPUT ... INTO a table variable. Never @@IDENTITY — it would return
/// whatever the trigger last touched.
///
/// Second, the release gate and the released-immutability rule live in database
/// triggers. This class translates their errors into results; it does not restate
/// the rules, because a second copy could disagree with the one actually enforced.
/// </summary>
public sealed class SqlEngDesignStore(
    IOptions<EngOptions> options) : IEngDesignStore
{
    private readonly EngOptions _options = options.Value;

    // Error number THROWn by the schema's immutability trigger. Matching on this
    // rather than on message text: the text is a UI string and may be reworded,
    // the number is the contract.
    private const int ErrBaselineImmutable = 50003;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_options.SqlConnectionString);
        await cn.OpenAsync(ct);
        return cn;
    }

    // ---- Lifecycle --------------------------------------------------------

    public async Task ResetAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.SqlConnectionString))
        {
            throw new InvalidOperationException(
                "Eng__SqlConnectionString is not configured; there is nothing to reset.");
        }

        await SqlScriptRunner.ExecuteAsync(
            _options.SqlConnectionString, "EngProvider.Infrastructure.Sql.drop.sql", ct);

        await SqlScriptRunner.ExecuteAsync(
            _options.SqlConnectionString, "EngProvider.Infrastructure.Sql.schema.sql", ct);

        // Reference data is part of a reset, not something left for the next
        // cold start. The drop takes the EC schemas and classes with it, and a
        // provider that answered its class catalog with an empty list would
        // look broken rather than empty -- nothing could be created on it, and
        // the cause would not be visible from the response.
        await SqlScriptRunner.ExecuteAsync(
            _options.SqlConnectionString, "EngProvider.Infrastructure.Sql.bootstrap.sql", ct);
    }

    // ---- iTwins and iModels ----------------------------------------------

    // Class, Type and Status are reserved words, so they are bracketed here and
    // in every statement below.
    //
    // The boundary's name is read from iTwinType rather than from iTwin.[Type].
    // Both hold it, but only the iTwinType copy is guarded by a uniqueness
    // constraint and only it moves when a boundary is renamed; [Type] is the
    // raw as-received string. Publishing the joined value keeps
    // Site.Type.ShortName sourced from the same row as Site.Type.UUID.
    private const string ITwinColumns = """
        t.iTwinId, t.CreatedUtc, t.[Class], t.SubClass, t.[Type],
        t.DisplayName, t.Number, t.[Status], t.ParentITwinId, t.Description,
        t.iTwinTypeId, ty.Number AS iTwinTypeNumber
        """;

    private const string ITwinFrom = """
        FROM dbo.iTwin AS t
        LEFT JOIN dbo.iTwinType AS ty ON ty.iTwinTypeId = t.iTwinTypeId
        """;

    private static EngITwin ReadITwin(SqlDataReader r) => new(
        r.GetGuid(0),
        r.GetDateTime(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetGuid(8),
        r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetGuid(10),
        r.IsDBNull(11) ? null : r.GetString(11));

    public async Task<IReadOnlyList<EngITwin>> GetITwinsAsync(CancellationToken ct)
    {
        // Ordered by the engineering identifier, falling back to the display
        // name, so the listing stays stable now that there is no single code
        // column to sort on.
        var sql = $"""
            SELECT {ITwinColumns}
            {ITwinFrom}
            ORDER BY COALESCE(t.Number, t.DisplayName, CONVERT(NVARCHAR(36), t.iTwinId));
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);

        var twins = new List<EngITwin>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            twins.Add(ReadITwin(reader));
        }

        return twins;
    }

    public async Task<EngITwin?> FindITwinAsync(Guid iTwinId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            $"SELECT {ITwinColumns} {ITwinFrom} WHERE t.iTwinId = @id;", cn);
        cmd.Parameters.AddWithValue("@id", iTwinId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadITwin(reader) : null;
    }

    public async Task<EngITwin> UpsertITwinAsync(UpsertITwinRequest request, CancellationToken ct)
    {
        // UPDATE-then-INSERT rather than MERGE. MERGE would express this in one
        // statement, but it needs HOLDLOCK to be race-free and has a long list
        // of known correctness caveats; this shape is the one used elsewhere in
        // the sandbox and is easier to reason about.
        //
        // CreatedUtc is deliberately absent from the UPDATE: re-registering a
        // twin the sandbox already knows does not make it new.
        //
        // iTwinTypeId is resolved from [Type] when the caller did not supply
        // one. The platform gives the boundary as a bare string with no id
        // attached, so the caller usually cannot name the key; matching it
        // against iTwinType.Number here is what turns the free-text value into
        // the stored identity that Site.Type.UUID carries. Without this the
        // column stays null and the engine silently declines to publish the
        // twin, which is indistinguishable from nothing having happened.
        //
        // An unrecognised boundary leaves the reference null rather than
        // minting a row, so it surfaces as an unpublished twin instead of a
        // new identity nobody chose. Deciding what should happen instead is
        // tracked in docs/open-items.md.
        const string sql = """
            DECLARE @resolvedTypeId UNIQUEIDENTIFIER = @iTwinTypeId;

            IF @resolvedTypeId IS NULL AND @type IS NOT NULL
                SELECT @resolvedTypeId = iTwinTypeId
                FROM dbo.iTwinType
                WHERE Number = @type;

            UPDATE dbo.iTwin WITH (UPDLOCK, HOLDLOCK)
            SET [Class] = @class,
                SubClass = @subClass,
                [Type] = @type,
                DisplayName = @displayName,
                Number = @number,
                [Status] = @status,
                ParentITwinId = @parentITwinId,
                Description = @description,
                iTwinTypeId = @resolvedTypeId,
                ModifiedUtc = SYSUTCDATETIME()
            WHERE iTwinId = @id;

            IF @@ROWCOUNT = 0
                INSERT INTO dbo.iTwin
                    (iTwinId, [Class], SubClass, [Type], DisplayName, Number,
                     [Status], ParentITwinId, Description, iTwinTypeId)
                VALUES
                    (@id, @class, @subClass, @type, @displayName, @number,
                     @status, @parentITwinId, @description, @resolvedTypeId);
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@id", request.ITwinId);
        cmd.Parameters.AddWithValue("@class", (object?)request.Class ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@subClass", (object?)request.SubClass ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@type", (object?)request.Type ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@displayName", (object?)request.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@number", (object?)request.Number ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", (object?)request.Status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@parentITwinId", (object?)request.ParentITwinId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@description", (object?)request.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@iTwinTypeId", (object?)request.ITwinTypeId ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);

        return (await FindITwinAsync(request.ITwinId, ct))!;
    }

    public async Task<bool> DeleteITwinAsync(Guid iTwinId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);

        // The iModel count is taken in the same statement as the delete so the
        // two cannot disagree. Checking first and deleting after would leave a
        // window where an iModel is added against a twin already judged empty,
        // and the delete would then fail on the foreign key with a message that
        // says nothing useful.
        const string sql = """
            DECLARE @iModels INT;

            SELECT @iModels = COUNT(*)
            FROM dbo.iModel WITH (UPDLOCK, HOLDLOCK)
            WHERE iTwinId = @id;

            IF @iModels > 0
                THROW 50000, 'iModels still reference this iTwin.', 1;

            DELETE FROM dbo.iTwin WHERE iTwinId = @id;

            SELECT @@ROWCOUNT;
            """;

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@id", iTwinId);

        try
        {
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
        }
        catch (SqlException ex) when (ex.Number == 50000)
        {
            // Re-thrown as the store's own vocabulary. The route turns this into
            // a 409, which is the honest answer: the request was understood and
            // refused, not malformed.
            throw new InvalidOperationException(
                $"iTwin {iTwinId:D} still holds iModels. Delete them first.", ex);
        }
    }

    public async Task<IReadOnlyList<EngIModel>> GetIModelsAsync(Guid? iTwinId, CancellationToken ct)
    {
        const string sql = """
            SELECT iModelId, iTwinId, Code, Description, CreatedUtc
            FROM dbo.iModel
            WHERE (@iTwinId IS NULL OR iTwinId = @iTwinId)
            ORDER BY Code;
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@iTwinId", (object?)iTwinId ?? DBNull.Value);

        var models = new List<EngIModel>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) models.Add(ReadIModel(reader));
        return models;
    }

    public async Task<EngIModel?> FindIModelAsync(Guid iModelId, CancellationToken ct)
    {
        const string sql = """
            SELECT iModelId, iTwinId, Code, Description, CreatedUtc
            FROM dbo.iModel
            WHERE iModelId = @id;
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@id", iModelId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadIModel(reader) : null;
    }

    private static EngIModel ReadIModel(SqlDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetDateTime(4));

    public async Task<EngIModel> UpsertIModelAsync(UpsertIModelRequest request, CancellationToken ct)
    {
        // UPDATE-then-INSERT under UPDLOCK/HOLDLOCK, as UpsertITwinAsync does and
        // for the same reasons -- see the note there on MERGE.
        //
        // CreatedUtc is absent from the UPDATE: re-reading a model the sandbox
        // already knows does not make it new. ModifiedUtc is set, because unlike
        // the iTwin table this one has a column for it.
        const string sql = """
            UPDATE dbo.iModel WITH (UPDLOCK, HOLDLOCK)
            SET Code = @code,
                Description = @description,
                ModifiedUtc = SYSUTCDATETIME()
            WHERE iModelId = @id;

            IF @@ROWCOUNT = 0
                INSERT INTO dbo.iModel (iModelId, iTwinId, Code, Description)
                VALUES (@id, @iTwinId, @code, @description);
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@id", request.IModelId);
        cmd.Parameters.AddWithValue("@iTwinId", request.ITwinId);
        cmd.Parameters.AddWithValue("@code", request.Code);
        cmd.Parameters.AddWithValue("@description", (object?)request.Description ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);

        return (await FindIModelAsync(request.IModelId, ct))!;
    }

    // ---- Schema catalogue -------------------------------------------------

    public async Task<IReadOnlyList<EngClass>> GetConcreteClassesAsync(CancellationToken ct)
    {
        // Abstract classes are filtered out here, matching CK_Element_NotAbstract.
        const string sql = """
            SELECT
                c.ECClassId,
                s.SchemaName,
                s.SchemaVersion,
                c.ClassName,
                CONCAT(s.SchemaName, N'.', c.ClassName),
                c.DisplayLabel,
                c.ClassModifier
            FROM dbo.ECClass AS c
            INNER JOIN dbo.ECSchema AS s ON s.ECSchemaId = c.ECSchemaId
            WHERE c.ClassModifier <> N'Abstract'
            ORDER BY s.SchemaName, c.ClassName;
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);

        var classes = new List<EngClass>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            classes.Add(new EngClass(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6)));
        }

        return classes;
    }

    // ---- Elements ---------------------------------------------------------

    private const string ElementSelect = """
        SELECT
            ECInstanceId,
            iModelId,
            ECClassId,
            FullyQualifiedECClassName,
            FederationGuid,
            CodeValue,
            UserLabel,
            DisplayName,
            ParentECInstanceId,
            ChangesetIndex,
            CreatedUtc,
            ModifiedUtc
        FROM dbo.vElement
        """;

    private static EngElement ReadElement(SqlDataReader r) => new(
        r.GetInt64(0),
        r.GetGuid(1),
        r.GetInt64(2),
        r.GetString(3),
        r.IsDBNull(4) ? null : r.GetGuid(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetInt64(8),
        r.GetInt32(9),
        r.GetDateTime(10),
        r.GetDateTime(11));

    public async Task<EngElement?> FindElementAsync(long ecInstanceId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{ElementSelect} WHERE ECInstanceId = @id;", cn);
        cmd.Parameters.AddWithValue("@id", ecInstanceId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadElement(reader) : null;
    }

    public async Task<EngElement?> FindElementByCodeAsync(
        Guid iModelId, string codeValue, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            $"{ElementSelect} WHERE iModelId = @iModelId AND CodeValue = @code;", cn);
        cmd.Parameters.AddWithValue("@iModelId", iModelId);
        cmd.Parameters.AddWithValue("@code", codeValue);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadElement(reader) : null;
    }

    public async Task<IReadOnlyList<EngElement>> GetElementsAsync(
        Guid? iModelId, long? namedVersionId,
        DateTime? modifiedSince, CancellationToken ct)
    {
        // Ordered by ModifiedUtc for an incremental read so a caller that stops
        // partway through resumes without a gap; by code otherwise, which is the
        // order a person reading a list expects.
        var order = modifiedSince is null
            ? "ORDER BY CodeValue, ECInstanceId"
            : "ORDER BY ModifiedUtc, ECInstanceId";

        // Membership is derived, so filtering by named version asks the view
        // rather than a column: the element itself does not know which markers
        // enclose it.
        var sql = $"""
            {ElementSelect}
            WHERE (@iModelId IS NULL OR iModelId = @iModelId)
              AND (@modifiedSince IS NULL OR ModifiedUtc >= @modifiedSince)
              AND (@namedVersionId IS NULL OR ECInstanceId IN (
                    SELECT ECInstanceId FROM dbo.vNamedVersionElement
                     WHERE NamedVersionId = @namedVersionId))
            {order};
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@iModelId", (object?)iModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@namedVersionId", (object?)namedVersionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@modifiedSince", (object?)modifiedSince ?? DBNull.Value);

        var elements = new List<EngElement>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) elements.Add(ReadElement(reader));
        return elements;
    }

    /// <summary>
    /// The elements a marker encloses, straight from the derivation view so this
    /// and every other caller get the same answer.
    /// </summary>
    public async Task<IReadOnlyList<EngElement>> GetNamedVersionElementsAsync(
        long namedVersionId, CancellationToken ct)
    {
        var sql = $"""
            {ElementSelect}
            WHERE ECInstanceId IN (
                SELECT ECInstanceId FROM dbo.vNamedVersionElement
                 WHERE NamedVersionId = @id)
            ORDER BY ChangesetIndex, ECInstanceId;
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@id", namedVersionId);

        var elements = new List<EngElement>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) elements.Add(ReadElement(reader));
        return elements;
    }

    public async Task<ElementUpsertResult> UpsertElementsAsync(
        Guid iModelId, IReadOnlyList<ElementUpsert> elements, CancellationToken ct)
    {
        var upserted = new List<UpsertedElement>();
        var rejections = new List<UpsertRejection>();

        if (elements.Count == 0) return new ElementUpsertResult(upserted, rejections);

        await using var cn = await OpenAsync(ct);

        // The iModel must exist before anything can be positioned within it.
        // Resolved once, before the transaction: if it is absent, no item in the
        // batch can succeed.
        if (!await IModelExistsAsync(cn, iModelId, ct))
        {
            return new ElementUpsertResult([], [.. elements.Select(e => new UpsertRejection(
                KeyOf(e), $"No iModel '{iModelId}'.", false))]);
        }

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            // One position for the whole batch: these edits were made together,
            // so a marker cut afterwards should enclose all of them or none.
            // Allocated inside the transaction under UPDLOCK/HOLDLOCK, so two
            // concurrent batches cannot claim the same position.
            var changesetIndex = await AllocateChangesetIndexAsync(cn, tx, iModelId, ct);

            foreach (var element in elements)
            {
                if (element.ECClassId <= 0)
                {
                    rejections.Add(new UpsertRejection(
                        KeyOf(element), "ECClassId is required.", false));
                    continue;
                }

                try
                {
                    upserted.Add(await UpsertOneElementAsync(
                        cn, tx, iModelId, changesetIndex, element, ct));
                }
                catch (SqlException ex) when (!IsTransient(ex) && !IsFatalToBatch(ex))
                {
                    // A per-item fault: a duplicate code, an abstract class, a
                    // parent in another iModel. Reject the item and keep going,
                    // so one bad row does not discard a good batch.
                    rejections.Add(new UpsertRejection(KeyOf(element), Explain(ex), false));
                }
            }

            await tx.CommitAsync(ct);
        }
        catch (SqlException ex)
        {
            await RollbackQuietlyAsync(tx, ct);

            return new ElementUpsertResult(
                [],
                [.. elements.Select(e => new UpsertRejection(
                    KeyOf(e), Explain(ex), IsTransient(ex)))]);
        }

        return new ElementUpsertResult(upserted, rejections);
    }

    /// <summary>
    /// True for faults that take the whole transaction down with them.
    ///
    /// A THROW inside a trigger dooms the transaction: the statement after it
    /// cannot run, so there is nothing left to continue with. A constraint
    /// violation is different -- only that one statement failed, and the batch
    /// carries on. Treating the two alike is what turns a clean rejection into
    /// "this SqlTransaction has completed".
    /// </summary>
    private static bool IsFatalToBatch(SqlException ex) =>
        ex.Number == ErrBaselineImmutable;

    /// <summary>
    /// Rolls back if there is still anything to roll back. A doomed transaction
    /// has already been undone by the server, and asking again throws an
    /// exception that would replace the real cause with a misleading one.
    /// </summary>
    private static async Task RollbackQuietlyAsync(SqlTransaction tx, CancellationToken ct)
    {
        try
        {
            await tx.RollbackAsync(ct);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string KeyOf(ElementUpsert e) =>
        e.CodeValue ?? e.ECInstanceId?.ToString() ?? "(no code)";

    /// <summary>
    /// Insert or update one element.
    ///
    /// ECClassModifier is copied from ECClass rather than accepted from the
    /// caller: FK_Element_ECClass is composite over (ECClassId, ClassModifier),
    /// so a caller-supplied modifier that disagreed would be rejected anyway.
    /// Reading it here means the caller never has to know it exists.
    /// </summary>
    private static async Task<UpsertedElement> UpsertOneElementAsync(
        SqlConnection cn,
        SqlTransaction tx,
        Guid iModelId,
        int changesetIndex,
        ElementUpsert element,
        CancellationToken ct)
    {
        if (element.ECInstanceId is { } id)
        {
            // The position moves forward on edit: a change is itself new work,
            // and leaving the element at its original position would place the
            // edit inside a baseline that was cut before it happened.
            const string update = """
                UPDATE dbo.Element
                   SET ECClassId          = @classId,
                       ECClassModifier    = (SELECT ClassModifier FROM dbo.ECClass WHERE ECClassId = @classId),
                       CodeValue          = @code,
                       UserLabel          = @label,
                       DisplayName        = @display,
                       ParentECInstanceId = @parent,
                       -- Set when supplied, left alone when not. An update that
                       -- omitted the guid would otherwise revoke an identity
                       -- other systems are already holding, over a field the
                       -- caller never mentioned.
                       FederationGuid     = COALESCE(@federationGuid, FederationGuid),
                       ChangesetIndex     = @changesetIndex
                 WHERE ECInstanceId = @id;
                """;

            await using var cmd = new SqlCommand(update, cn, tx);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@changesetIndex", changesetIndex);
            AddElementParameters(cmd, element);

            var affected = await cmd.ExecuteNonQueryAsync(ct);

            if (affected == 0)
                throw new InvalidOperationException($"No element '{id}'.");

            return new UpsertedElement(id, element.CodeValue, false);
        }

        // OUTPUT ... INTO a table variable, not a bare OUTPUT: dbo.Element has an
        // AFTER trigger, and error 334 forbids OUTPUT *without* INTO on a table
        // that has one. SCOPE_IDENTITY() would also work, but OUTPUT INTO stays
        // correct if this ever becomes a multi-row insert.
        const string insert = """
            DECLARE @new TABLE (ECInstanceId BIGINT);

            INSERT INTO dbo.Element
                (ECClassId, ECClassModifier, CodeValue, UserLabel, DisplayName,
                 ParentECInstanceId, iModelId, ChangesetIndex, FederationGuid)
            OUTPUT inserted.ECInstanceId INTO @new (ECInstanceId)
            VALUES
                (@classId,
                 (SELECT ClassModifier FROM dbo.ECClass WHERE ECClassId = @classId),
                 @code, @label, @display, @parent, @iModelId, @changesetIndex,
                 @federationGuid);

            SELECT ECInstanceId FROM @new;
            """;

        await using var insertCmd = new SqlCommand(insert, cn, tx);
        AddElementParameters(insertCmd, element);
        insertCmd.Parameters.AddWithValue("@iModelId", iModelId);
        insertCmd.Parameters.AddWithValue("@changesetIndex", changesetIndex);

        var newId = (long)(await insertCmd.ExecuteScalarAsync(ct))!;
        return new UpsertedElement(newId, element.CodeValue, true);
    }

    private static async Task<bool> IModelExistsAsync(
        SqlConnection cn, Guid iModelId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT 1 FROM dbo.iModel WHERE iModelId = @id;", cn);
        cmd.Parameters.AddWithValue("@id", iModelId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// Takes the next changeset position in an iModel.
    ///
    /// The next position is one past the highest already in use by either an
    /// element or a marker, so a position is never reused and work can never
    /// land at or below an existing baseline.
    ///
    /// UPDLOCK, HOLDLOCK because this is a read-then-write: without it two
    /// concurrent writers read the same maximum and both claim it, which for a
    /// marker collides on the unique index and for elements silently interleaves
    /// two batches at one position.
    /// </summary>
    private static async Task<int> AllocateChangesetIndexAsync(
        SqlConnection cn, SqlTransaction tx, Guid iModelId, CancellationToken ct)
    {
        const string sql = """
            SELECT 1 + ISNULL(
                (
                    SELECT MAX(used.ChangesetIndex)
                    FROM
                    (
                        SELECT ChangesetIndex FROM dbo.Element WITH (UPDLOCK, HOLDLOCK)
                         WHERE iModelId = @iModelId
                        UNION ALL
                        SELECT ChangesetIndex FROM dbo.NamedVersion WITH (UPDLOCK, HOLDLOCK)
                         WHERE iModelId = @iModelId
                    ) AS used
                ), 0);
            """;

        await using var cmd = new SqlCommand(sql, cn, tx);
        cmd.Parameters.AddWithValue("@iModelId", iModelId);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static void AddElementParameters(SqlCommand cmd, ElementUpsert e)
    {
        cmd.Parameters.AddWithValue("@classId", e.ECClassId);
        cmd.Parameters.AddWithValue("@code", (object?)e.CodeValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@label", (object?)e.UserLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@display", (object?)e.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@parent", (object?)e.ParentECInstanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@federationGuid", (object?)e.FederationGuid ?? DBNull.Value);
    }

    // ---- Named versions ---------------------------------------------------

    private const string VersionSelect = """
        SELECT
            nv.NamedVersionId,
            nv.VersionGuid,
            nv.iModelId,
            nv.Name,
            nv.Description,
            nv.ChangesetId,
            nv.ChangesetIndex,
            nv.CreatedBy,
            nv.CreatedUtc,
            nv.ModifiedUtc,
            (SELECT COUNT(*) FROM dbo.vNamedVersionElement AS m
              WHERE m.NamedVersionId = nv.NamedVersionId)
        FROM dbo.NamedVersion AS nv
        """;

    private static EngNamedVersion ReadVersion(SqlDataReader r) => new(
        r.GetInt64(0),
        r.GetGuid(1),
        r.GetGuid(2),
        r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetString(5),
        r.GetInt32(6),
        r.GetString(7),
        r.GetDateTime(8),
        r.GetDateTime(9),
        r.GetInt32(10));

    public async Task<IReadOnlyList<EngNamedVersion>> GetNamedVersionsAsync(
        Guid? iModelId, DateTime? modifiedSince, CancellationToken ct)
    {
        // Newest first for a browsing caller; oldest change first for an
        // incremental one, which has to process in the order things happened.
        var order = modifiedSince is null
            ? "ORDER BY nv.CreatedUtc DESC"
            : "ORDER BY nv.ModifiedUtc, nv.NamedVersionId";

        var sql = $"""
            {VersionSelect}
            WHERE (@iModelId IS NULL OR nv.iModelId = @iModelId)
              AND (@modifiedSince IS NULL OR nv.ModifiedUtc >= @modifiedSince)
            {order};
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@iModelId", (object?)iModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@modifiedSince", (object?)modifiedSince ?? DBNull.Value);

        var versions = new List<EngNamedVersion>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) versions.Add(ReadVersion(reader));
        return versions;
    }

    public async Task<EngNamedVersion?> FindNamedVersionAsync(
        long namedVersionId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        return await FindVersionAsync(cn, null, namedVersionId, ct);
    }

    private static async Task<EngNamedVersion?> FindVersionAsync(
        SqlConnection cn, SqlTransaction? tx, long namedVersionId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            $"{VersionSelect} WHERE nv.NamedVersionId = @id;", cn, tx);
        cmd.Parameters.AddWithValue("@id", namedVersionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    /// <summary>
    /// Creates the marker, pinned at the iModel's current position.
    ///
    /// One transaction, because allocating the position and writing the row that
    /// claims it is a read-then-write: a marker created between the two would
    /// leave this one pinned where work has since landed.
    ///
    /// A real iModels pins a named version to a changeset it already has; this
    /// emulation has no changeset history, so an identifier is synthesised from
    /// the version GUID. It is meaningful only inside ENG: it describes a
    /// position in this model's own history and means nothing to any other
    /// participant, so nothing downstream may key or reconcile against it.
    /// </summary>
    public async Task<EngNamedVersion> CreateNamedVersionAsync(
        NamedVersionDraft draft, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO dbo.NamedVersion
                (iModelId, Name, Description, CreatedBy, ChangesetIndex, ChangesetId)
            OUTPUT inserted.NamedVersionId
            VALUES
                (@iModelId, @name, @description, @createdBy, @changesetIndex,
                 CONVERT(VARCHAR(64),
                     HASHBYTES('SHA2_256',
                         CONVERT(NVARCHAR(100), NEWID())
                         + CONVERT(NVARCHAR(50), SYSUTCDATETIME())), 2));
            """;

        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        long id;

        try
        {
            var changesetIndex = await AllocateChangesetIndexAsync(cn, tx, draft.IModelId, ct);

            await using var cmd = new SqlCommand(sql, cn, tx);
            cmd.Parameters.AddWithValue("@iModelId", draft.IModelId);
            cmd.Parameters.AddWithValue("@name", draft.Name);
            cmd.Parameters.AddWithValue("@description", (object?)draft.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@createdBy", draft.CreatedBy ?? "system");
            cmd.Parameters.AddWithValue("@changesetIndex", changesetIndex);

            id = (long)(await cmd.ExecuteScalarAsync(ct))!;

            await tx.CommitAsync(ct);
        }
        catch
        {
            await RollbackQuietlyAsync(tx, ct);
            throw;
        }

        return await FindVersionAsync(cn, null, id, ct)
            ?? throw new InvalidOperationException($"Named version '{id}' vanished after insert.");
    }

    /// <summary>
    /// Turns a SQL fault into something a caller can act on.
    /// </summary>
    private static string Explain(SqlException ex) => ex.Number switch
    {
        ErrBaselineImmutable =>
            "That element cannot be removed: it is at or below the most recent named version, "
                + "and removing it would rewrite what that marker already described. Edit it "
                + "instead and cut a new named version to promote the change.",

        // 2601/2627 are the two duplicate-key numbers: unique index and unique
        // constraint respectively.
        2601 or 2627 when ex.Message.Contains("UX_Element_iModel_CodeValue") =>
            "Another element in this iModel already carries that code.",

        2601 or 2627 when ex.Message.Contains("UX_Element_FederationGuid") =>
            "Another element already carries that federation identifier.",

        2601 or 2627 => "That value duplicates one already stored.",

        // A composite-FK failure against ECClass means the class is abstract, or
        // the modifier moved underneath the caller.
        547 when ex.Message.Contains("FK_Element_ECClass") =>
            "That EC class cannot be instantiated; it is abstract.",

        547 when ex.Message.Contains("CK_Element_NotAbstract") =>
            "That EC class cannot be instantiated; it is abstract.",

        547 when ex.Message.Contains("FK_Element_Parent") =>
            "The parent element does not exist in this iModel.",

        547 =>
            "The request references something that does not exist.",

        _ => ex.Message
    };

    /// <summary>
    /// True only for faults a later identical attempt could clear. A constraint
    /// violation is a fact about the request and will never clear on retry;
    /// saying otherwise would invite a caller to retry forever.
    /// </summary>
    private static bool IsTransient(SqlException ex) => ex.Number switch
    {
        -2 or 1205 or 49918 or 49919 or 49920 or 4060 or 40197 or 40501 or 40613 or 10928
            or 10929 or 10053 or 10054 or 10060 or 233 or 64 => true,
        _ => false
    };
}
