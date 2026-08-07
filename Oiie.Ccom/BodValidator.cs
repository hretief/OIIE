using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Oiie.Ccom;

public enum BodValidationStatus
{
    Valid,
    Invalid,
    NotValidated
}

public sealed record BodValidationResult(
    BodValidationStatus Status,
    IReadOnlyList<string> Messages)
{
    public static BodValidationResult Valid() => new(BodValidationStatus.Valid, []);

    public static BodValidationResult NotValidated(string reason) =>
        new(BodValidationStatus.NotValidated, [reason]);

    public static BodValidationResult Invalid(IReadOnlyList<string> messages) =>
        new(BodValidationStatus.Invalid, messages);

    public string? Detail => Messages.Count == 0 ? null : string.Join(Environment.NewLine, Messages);
}

/// <summary>
/// Secondary check on documents the typed model has already produced or parsed.
///
/// Where no schema is held for a namespace the result is NotValidated, never Valid.
/// Silently passing unvalidated documents would hide exactly the gap that a missing
/// schema package represents.
/// </summary>
public sealed class BodValidator
{
    private readonly XmlSchemaSet _schemas = new();
    private readonly HashSet<string> _knownNamespaces = new(StringComparer.Ordinal);
    private readonly List<string> _loadDiagnostics = [];
    private bool _compiled;

    public IReadOnlyCollection<string> KnownNamespaces => _knownNamespaces;

    /// <summary>
    /// Problems found while reading or compiling the schema set. Published packages
    /// carry known defects, so this is expected to be non-empty; it exists so the
    /// gaps are visible rather than silent.
    /// </summary>
    public IReadOnlyList<string> LoadDiagnostics => _loadDiagnostics;

    public void LoadDirectory(string schemaDirectory)
    {
        if (!Directory.Exists(schemaDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(schemaDirectory, "*.xsd", SearchOption.AllDirectories))
        {
            try
            {
                // The reader is created over the path rather than a stream so the
                // schema keeps its source URI and its relative xs:include and
                // xs:import locations still resolve.
                using var reader = XmlReader.Create(file);
                var schema = XmlSchema.Read(reader, null);
                if (schema is null)
                {
                    continue;
                }

                _schemas.Add(schema);
                if (!string.IsNullOrEmpty(schema.TargetNamespace))
                {
                    _knownNamespaces.Add(schema.TargetNamespace);
                }
            }
            catch (Exception ex) when (ex is XmlSchemaException or XmlException)
            {
                // A malformed schema in a published package must not stop the rest
                // loading. Surfaced through LoadDiagnostics.
                _loadDiagnostics.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        _compiled = false;
    }

    /// <summary>
    /// Compiles at most once. A compilation fault is recorded and the namespaces are
    /// dropped, so documents fall back to NotValidated. Rethrowing per message would
    /// turn one defective schema into a failure of every read on the channel.
    /// </summary>
    private void EnsureCompiled()
    {
        if (_compiled)
        {
            return;
        }

        _compiled = true;

        try
        {
            _schemas.ValidationEventHandler += OnSchemaValidationEvent;
            _schemas.Compile();
        }
        catch (Exception ex) when (ex is XmlSchemaException or XmlException)
        {
            _loadDiagnostics.Add($"Schema set failed to compile: {ex.Message}");
            _knownNamespaces.Clear();
        }
        finally
        {
            _schemas.ValidationEventHandler -= OnSchemaValidationEvent;
        }
    }

    private void OnSchemaValidationEvent(object? sender, ValidationEventArgs args) =>
        _loadDiagnostics.Add($"{args.Severity}: {args.Message}");

    public BodValidationResult Validate(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var targetNamespace = document.Root?.Name.NamespaceName;

        if (string.IsNullOrEmpty(targetNamespace) || !_knownNamespaces.Contains(targetNamespace))
        {
            return BodValidationResult.NotValidated(
                $"No schema held for namespace '{targetNamespace}'.");
        }

        EnsureCompiled();

        if (!_knownNamespaces.Contains(targetNamespace))
        {
            return BodValidationResult.NotValidated(
                $"Schema for namespace '{targetNamespace}' did not compile.");
        }

        var messages = new List<string>();

        try
        {
            document.Validate(_schemas, (_, args) => messages.Add($"{args.Severity}: {args.Message}"), false);
        }
        catch (Exception ex) when (ex is XmlSchemaException or XmlException)
        {
            // The document could not be matched against the schema set at all. That
            // is a gap in coverage, not evidence the document is wrong.
            return BodValidationResult.NotValidated(ex.Message);
        }

        return messages.Count == 0
            ? BodValidationResult.Valid()
            : BodValidationResult.Invalid(messages);
    }
}
