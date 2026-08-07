using System.Data;
using CirProvider.Domain;
using Microsoft.Data.SqlClient;

namespace CirProvider.Infrastructure.Sql;

/// <summary>
/// Translates a ws-CIR §3.2.1 filter set into a parameterised SQL WHERE clause.
///
/// The semantics are precise and easy to get backwards:
///   - The four filter types AND together.
///   - Multiple filters *of the same type* OR together, regardless of which
///     Filter element they arrived in. Two EntryFilters in one Filter and two
///     spread across two Filters produce the same predicate.
///   - An absent filter type is logical TRUE, not an empty result.
///
/// Every string field accepts the §4 wildcard subset and is implicitly anchored
/// at both ends, so patterns are wrapped as ^(...)$ before reaching REGEXP_LIKE.
/// </summary>
internal sealed class CirFilterTranslator
{
    private readonly List<SqlParameter> _parameters = [];
    private int _next;

    public IReadOnlyList<SqlParameter> Parameters => _parameters;

    public string BuildWhere(IReadOnlyList<CirFilter> filters)
    {
        if (filters is null || filters.Count == 0) return "1 = 1";

        var registry = Or(filters.Select(f => f.RegistryFilter)
                                 .Where(x => x is not null)
                                 .Select(x => Translate(x!)));

        var category = Or(filters.Select(f => f.CategoryFilter)
                                 .Where(x => x is not null)
                                 .Select(x => Translate(x!)));

        var entry = Or(filters.Select(f => f.EntryFilter)
                              .Where(x => x is not null)
                              .Select(x => Translate(x!)));

        var property = Or(filters.Select(f => f.PropertyFilter)
                                 .Where(x => x is not null)
                                 .Select(x => Translate(x!)));

        return And([registry, category, entry, property]);
    }

    // -----------------------------------------------------------------------
    // Per-type translation. Fields within one filter AND together.
    // -----------------------------------------------------------------------

    private string Translate(RegistryFilter f)
    {
        var parts = new List<string>();
        if (f.Id is not null) parts.Add(Match("r.RegistryId", f.Id));
        if (f.Description is not null) parts.Add(MatchJsonArray("r.Description", f.Description));
        return And(parts);
    }

    private string Translate(CategoryFilter f)
    {
        var parts = new List<string>();
        if (f.Id is not null) parts.Add(Match("c.CategoryId", f.Id));
        if (f.SourceId is not null) parts.Add(Match("c.CategorySourceId", f.SourceId));
        if (f.Description is not null) parts.Add(MatchJsonArray("c.Description", f.Description));
        return And(parts);
    }

    private string Translate(EntryFilter f)
    {
        var parts = new List<string>();
        if (f.IdInSource is not null) parts.Add(Match("e.IdInSource", f.IdInSource));
        if (f.SourceId is not null) parts.Add(Match("e.SourceId", f.SourceId));
        if (f.SourceOwnerId is not null) parts.Add(Match("e.SourceOwnerId", f.SourceOwnerId));
        if (f.Name is not null) parts.Add(Match("e.Name", f.Name));

        // Entry.Description is a single TextType, not a collection.
        if (f.Description is not null)
            parts.Add(Match("JSON_VALUE(e.Description, '$.value')", f.Description));

        // CIRID and Inactive are typed, not string fields: no wildcards apply.
        if (f.Cirid is not null)
            parts.Add($"e.Cirid = {Param(f.Cirid.Value, SqlDbType.UniqueIdentifier)}");

        if (f.Inactive is not null)
            parts.Add($"ISNULL(e.Inactive, 0) = {Param(f.Inactive.Value, SqlDbType.Bit)}");

        return And(parts);
    }

    private string Translate(PropertyFilter f)
    {
        var conditions = new List<string> { "p.EntryKey = e.EntryKey" };

        if (f.Id is not null) conditions.Add(Match("p.PropertyId", f.Id));

        // Key and Value must match the same PropertyValue element, not merely
        // both appear somewhere in the collection.
        var valueParts = new List<string>();
        if (f.Key is not null) valueParts.Add(Match("pv.[key]", f.Key));
        if (f.Value is not null) valueParts.Add(Match("pv.[value]", f.Value));

        if (valueParts.Count > 0)
        {
            conditions.Add($"""
                (p.PropertyValue IS NOT NULL AND EXISTS (
                    SELECT 1 FROM OPENJSON(p.PropertyValue)
                    WITH ([key] NVARCHAR(400) '$.key', [value] NVARCHAR(MAX) '$.value') pv
                    WHERE {And(valueParts)}))
                """);
        }

        return $"EXISTS (SELECT 1 FROM cir.Property p WHERE {And(conditions)})";
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// §4: the expression is implicitly anchored at both ends. REGEXP_LIKE is a
    /// substring match by default, so the anchors are added here — without them
    /// 'Alpha' would match 'Alpha One' and the implementation would silently
    /// fail conformance while appearing to work.
    /// </summary>
    private string Match(string column, string pattern) =>
        $"REGEXP_LIKE({column}, CONCAT('^(', {Param(pattern, SqlDbType.NVarChar)}, ')$'))";

    /// <summary>Registry and Category descriptions are JSON arrays of TextType.</summary>
    private string MatchJsonArray(string column, string pattern) =>
        $"""
        ({column} IS NOT NULL AND EXISTS (
            SELECT 1 FROM OPENJSON({column}) WITH (v NVARCHAR(MAX) '$.value') d
            WHERE {Match("d.v", pattern)}))
        """;

    private string Param(object value, SqlDbType type)
    {
        var name = $"@f{_next++}";
        _parameters.Add(new SqlParameter(name, type) { Value = value });
        return name;
    }

    private static string And(IEnumerable<string> parts)
    {
        var list = parts.Where(p => !string.IsNullOrWhiteSpace(p) && p != "1 = 1").ToList();
        return list.Count == 0 ? "1 = 1" : $"({string.Join(" AND ", list)})";
    }

    private static string Or(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        if (list.Count == 0) return "1 = 1";                 // absent type = logical TRUE
        if (list.All(p => p == "1 = 1")) return "1 = 1";     // present but empty
        return $"({string.Join(" OR ", list)})";
    }
}
