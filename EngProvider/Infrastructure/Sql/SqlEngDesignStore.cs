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

    // Error numbers THROWn by the schema's gate triggers. Matching on these
    // rather than on message text: the text is a UI string and may be reworded,
    // the number is the contract.
    private const int ErrFindingsOpen = 50001;
    private const int ErrVersionEmpty = 50002;
    private const int ErrReleasedImmutable = 50003;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_options.SqlConnectionString);
        await cn.OpenAsync(ct);
        return cn;
    }

    // ---- iTwins and iModels ----------------------------------------------

    public async Task<IReadOnlyList<EngITwin>> GetITwinsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT iTwinId, Code, Description, CreatedUtc
            FROM dbo.iTwin
            ORDER BY Code;
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);

        var twins = new List<EngITwin>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            twins.Add(new EngITwin(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetDateTime(3)));
        }

        return twins;
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
            NamedVersionId,
            NamedVersionName,
            NamedVersionState,
            IsReleased,
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
        r.GetInt64(9),
        r.GetString(10),
        ParseState(r.GetString(11)),
        r.GetBoolean(12),
        r.GetDateTime(13),
        r.GetDateTime(14));

    private static NamedVersionState ParseState(string state) =>
        state == "Released" ? NamedVersionState.Released : NamedVersionState.Draft;

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
        Guid? iModelId, long? namedVersionId, bool? released, CancellationToken ct)
    {
        var sql = $"""
            {ElementSelect}
            WHERE (@iModelId IS NULL OR iModelId = @iModelId)
              AND (@namedVersionId IS NULL OR NamedVersionId = @namedVersionId)
              AND (@released IS NULL OR IsReleased = @released)
            ORDER BY CodeValue, ECInstanceId;
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@iModelId", (object?)iModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@namedVersionId", (object?)namedVersionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@released", (object?)released ?? DBNull.Value);

        var elements = new List<EngElement>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) elements.Add(ReadElement(reader));
        return elements;
    }

    public async Task<ElementUpsertResult> UpsertElementsAsync(
        long namedVersionId, IReadOnlyList<ElementUpsert> elements, CancellationToken ct)
    {
        var upserted = new List<UpsertedElement>();
        var rejections = new List<UpsertRejection>();

        if (elements.Count == 0) return new ElementUpsertResult(upserted, rejections);

        await using var cn = await OpenAsync(ct);

        // The version determines the target iModel and whether writing is allowed
        // at all. Resolved once, before the transaction: if the version is
        // released or absent, no item in the batch can succeed.
        var version = await ReadVersionTargetAsync(cn, null, namedVersionId, ct);

        if (version is null)
        {
            return new ElementUpsertResult([], [.. elements.Select(e => new UpsertRejection(
                KeyOf(e), $"No named version '{namedVersionId}'.", false))]);
        }

        if (version.Value.State == "Released")
        {
            return new ElementUpsertResult([], [.. elements.Select(e => new UpsertRejection(
                KeyOf(e),
                "That named version is released. A release is a handover record; "
                    + "new work belongs in a new draft version.",
                false))]);
        }

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
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
                        cn, tx, namedVersionId, version.Value.IModelId, element, ct));
                }
                catch (SqlException ex) when (!IsTransient(ex))
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
            await tx.RollbackAsync(ct);

            return new ElementUpsertResult(
                [],
                [.. elements.Select(e => new UpsertRejection(
                    KeyOf(e), Explain(ex), IsTransient(ex)))]);
        }

        return new ElementUpsertResult(upserted, rejections);
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
        long namedVersionId,
        Guid iModelId,
        ElementUpsert element,
        CancellationToken ct)
    {
        if (element.ECInstanceId is { } id)
        {
            const string update = """
                UPDATE dbo.Element
                   SET ECClassId          = @classId,
                       ECClassModifier    = (SELECT ClassModifier FROM dbo.ECClass WHERE ECClassId = @classId),
                       CodeValue          = @code,
                       UserLabel          = @label,
                       DisplayName        = @display,
                       ParentECInstanceId = @parent
                 WHERE ECInstanceId = @id;
                """;

            await using var cmd = new SqlCommand(update, cn, tx);
            cmd.Parameters.AddWithValue("@id", id);
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
                 ParentECInstanceId, iModelId, NamedVersionId)
            OUTPUT inserted.ECInstanceId INTO @new (ECInstanceId)
            VALUES
                (@classId,
                 (SELECT ClassModifier FROM dbo.ECClass WHERE ECClassId = @classId),
                 @code, @label, @display, @parent, @iModelId, @namedVersionId);

            SELECT ECInstanceId FROM @new;
            """;

        await using var insertCmd = new SqlCommand(insert, cn, tx);
        AddElementParameters(insertCmd, element);
        insertCmd.Parameters.AddWithValue("@iModelId", iModelId);
        insertCmd.Parameters.AddWithValue("@namedVersionId", namedVersionId);

        var newId = (long)(await insertCmd.ExecuteScalarAsync(ct))!;
        return new UpsertedElement(newId, element.CodeValue, true);
    }

    private static void AddElementParameters(SqlCommand cmd, ElementUpsert e)
    {
        cmd.Parameters.AddWithValue("@classId", e.ECClassId);
        cmd.Parameters.AddWithValue("@code", (object?)e.CodeValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@label", (object?)e.UserLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@display", (object?)e.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@parent", (object?)e.ParentECInstanceId ?? DBNull.Value);
    }

    // ---- Named versions ---------------------------------------------------

    private const string VersionSelect = """
        SELECT
            nv.NamedVersionId,
            nv.iModelId,
            nv.Name,
            nv.Description,
            nv.State,
            nv.CreatedBy,
            nv.CreatedUtc,
            nv.ReleasedUtc,
            (SELECT COUNT(*) FROM dbo.Element AS e
              WHERE e.NamedVersionId = nv.NamedVersionId),
            (SELECT COUNT(*) FROM dbo.ValidationFinding AS f
              WHERE f.NamedVersionId = nv.NamedVersionId AND f.State = N'Open')
        FROM dbo.NamedVersion AS nv
        """;

    private static EngNamedVersion ReadVersion(SqlDataReader r) => new(
        r.GetInt64(0),
        r.GetGuid(1),
        r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        ParseState(r.GetString(4)),
        r.GetString(5),
        r.GetDateTime(6),
        r.IsDBNull(7) ? null : r.GetDateTime(7),
        r.GetInt32(8),
        r.GetInt32(9));

    public async Task<IReadOnlyList<EngNamedVersion>> GetNamedVersionsAsync(
        Guid? iModelId, CancellationToken ct)
    {
        var sql = $"""
            {VersionSelect}
            WHERE (@iModelId IS NULL OR nv.iModelId = @iModelId)
            ORDER BY nv.CreatedUtc DESC;
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@iModelId", (object?)iModelId ?? DBNull.Value);

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

    private static async Task<(Guid IModelId, string State)?> ReadVersionTargetAsync(
        SqlConnection cn, SqlTransaction? tx, long namedVersionId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT iModelId, State FROM dbo.NamedVersion WHERE NamedVersionId = @id;", cn, tx);
        cmd.Parameters.AddWithValue("@id", namedVersionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetGuid(0), reader.GetString(1))
            : null;
    }

    public async Task<EngNamedVersion> CreateNamedVersionAsync(
        NamedVersionDraft draft, CancellationToken ct)
    {
        // State is not a parameter. A version is created as a Draft and reaches
        // Released only through the gate; accepting it here would be a way past.
        const string sql = """
            INSERT INTO dbo.NamedVersion (iModelId, Name, Description, CreatedBy)
            OUTPUT inserted.NamedVersionId
            VALUES (@iModelId, @name, @description, @createdBy);
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@iModelId", draft.IModelId);
        cmd.Parameters.AddWithValue("@name", draft.Name);
        cmd.Parameters.AddWithValue("@description", (object?)draft.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdBy", draft.CreatedBy ?? "system");

        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;

        return await FindVersionAsync(cn, null, id, ct)
            ?? throw new InvalidOperationException($"Named version '{id}' vanished after insert.");
    }

    /// <summary>
    /// Attempts the release and lets the database decide.
    ///
    /// The gate conditions are not re-checked here first. Checking in the
    /// application and then relying on the trigger would be two rules that can
    /// disagree, and the check-then-act window is exactly when another caller
    /// raises a finding. The UPDATE is issued, and a refusal is translated.
    /// </summary>
    public async Task<ReleaseResult> ReleaseNamedVersionAsync(
        long namedVersionId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);

        var before = await FindVersionAsync(cn, null, namedVersionId, ct);

        if (before is null)
        {
            return new ReleaseResult(
                false, namedVersionId, string.Empty, NamedVersionState.Draft, 0,
                $"No named version '{namedVersionId}'.", []);
        }

        if (before.State == NamedVersionState.Released)
        {
            // Already where the caller wants it. Reporting success on a no-op
            // would imply this call released it.
            return new ReleaseResult(
                false, namedVersionId, before.Name, before.State, before.ElementCount,
                "That named version is already released.",
                await ReadFindingsAsync(cn, null, namedVersionId, openOnly: true, ct));
        }

        const string release = """
            UPDATE dbo.NamedVersion
               SET State = N'Released', ReleasedUtc = SYSUTCDATETIME()
             WHERE NamedVersionId = @id;
            """;

        try
        {
            await using var cmd = new SqlCommand(release, cn);
            cmd.Parameters.AddWithValue("@id", namedVersionId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Number is ErrFindingsOpen or ErrVersionEmpty)
        {
            var reason = ex.Number == ErrFindingsOpen
                ? "Validation findings are still open against this version."
                : "This version contains no elements.";

            return new ReleaseResult(
                false, namedVersionId, before.Name, before.State, before.ElementCount,
                reason,
                await ReadFindingsAsync(cn, null, namedVersionId, openOnly: true, ct));
        }

        var after = await FindVersionAsync(cn, null, namedVersionId, ct)
            ?? throw new InvalidOperationException($"Named version '{namedVersionId}' vanished.");

        return new ReleaseResult(
            true, after.NamedVersionId, after.Name, after.State, after.ElementCount, null, []);
    }

    // ---- Validation findings ----------------------------------------------

    private const string FindingSelect = """
        SELECT
            FindingId, NamedVersionId, CodeValue, Severity, Message,
            State, CreatedUtc, ResolvedUtc, ResolvedBy
        FROM dbo.ValidationFinding
        """;

    private static EngValidationFinding ReadFinding(SqlDataReader r) => new(
        r.GetInt64(0),
        r.GetInt64(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        r.GetString(5),
        r.GetDateTime(6),
        r.IsDBNull(7) ? null : r.GetDateTime(7),
        r.IsDBNull(8) ? null : r.GetString(8));

    public async Task<IReadOnlyList<EngValidationFinding>> GetFindingsAsync(
        long namedVersionId, bool openOnly, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        return await ReadFindingsAsync(cn, null, namedVersionId, openOnly, ct);
    }

    private static async Task<IReadOnlyList<EngValidationFinding>> ReadFindingsAsync(
        SqlConnection cn, SqlTransaction? tx, long namedVersionId, bool openOnly,
        CancellationToken ct)
    {
        var sql = $"""
            {FindingSelect}
            WHERE NamedVersionId = @id
              AND (@openOnly = 0 OR State = N'Open')
            ORDER BY CreatedUtc, FindingId;
            """;

        await using var cmd = new SqlCommand(sql, cn, tx);
        cmd.Parameters.AddWithValue("@id", namedVersionId);
        cmd.Parameters.AddWithValue("@openOnly", openOnly);

        var findings = new List<EngValidationFinding>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) findings.Add(ReadFinding(reader));
        return findings;
    }

    public async Task<EngValidationFinding> RaiseFindingAsync(
        FindingDraft draft, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO dbo.ValidationFinding (NamedVersionId, CodeValue, Severity, Message)
            OUTPUT inserted.FindingId
            VALUES (@versionId, @code, @severity, @message);
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@versionId", draft.NamedVersionId);
        cmd.Parameters.AddWithValue("@code", (object?)draft.CodeValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@severity", draft.Severity);
        cmd.Parameters.AddWithValue("@message", draft.Message);

        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;

        await using var read = new SqlCommand($"{FindingSelect} WHERE FindingId = @id;", cn);
        read.Parameters.AddWithValue("@id", id);

        await using var reader = await read.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ReadFinding(reader)
            : throw new InvalidOperationException($"Finding '{id}' vanished after insert.");
    }

    public async Task<bool> ResolveFindingAsync(
        long findingId, string? resolvedBy, CancellationToken ct)
    {
        // Guarded on State = 'Open' so re-resolving reports false rather than
        // silently restamping who closed it and when.
        const string sql = """
            UPDATE dbo.ValidationFinding
               SET State = N'Resolved',
                   ResolvedUtc = SYSUTCDATETIME(),
                   ResolvedBy = @by
             WHERE FindingId = @id AND State = N'Open';
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@id", findingId);
        cmd.Parameters.AddWithValue("@by", resolvedBy ?? "system");

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ---- Error translation ------------------------------------------------

    /// <summary>
    /// Turns a SQL fault into something a caller can act on.
    ///
    /// The raw message names constraints, which is an internal detail and tells
    /// an API caller nothing they can use.
    /// </summary>
    private static string Explain(SqlException ex) => ex.Number switch
    {
        ErrReleasedImmutable =>
            "That element belongs to a released named version and cannot be changed. "
                + "New work belongs in a new draft version.",

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

        547 when ex.Message.Contains("FK_Element_NamedVersion") =>
            "That named version does not belong to this iModel.",

        547 => "The request references something that does not exist.",

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
