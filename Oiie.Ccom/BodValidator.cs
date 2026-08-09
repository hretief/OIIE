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
    private XmlSchemaSet _schemas = new();
    private readonly HashSet<string> _knownNamespaces = new(StringComparer.Ordinal);
    private readonly List<string> _loadDiagnostics = [];
    private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
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

        var read = new List<(string File, XmlSchema Schema)>();

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

                read.Add((file, schema));
            }
            catch (Exception ex) when (ex is XmlSchemaException or XmlException)
            {
                // A malformed schema in a published package must not stop the rest
                // loading. Surfaced through LoadDiagnostics.
                _loadDiagnostics.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (file, schema) in Ordered(read))
        {
            // Packages vendor their own copies of shared dependencies — the CCOM
            // BOD package carries the same OAGIS schemas as the standalone oagis
            // folder. Both copies must stay on disk so each package's relative
            // includes resolve, but adding the identical file twice looks exactly
            // like a redeclaration. Identity is by content, so a genuine divergence
            // between two copies is still reported rather than silently accepted.
            if (!seen.Add(Fingerprint(file)))
            {
                continue;
            }

            // A schema that redeclares a global its own include already provides
            // cannot compile, and one such file invalidates the whole set — so a
            // defect in an unrelated BOD silently disables validation everywhere.
            // The ws-CIR package has three where the request wrapper takes the same
            // qualified name as the payload element it includes. Skipping the
            // wrapper keeps the payload, which is what messages on the wire carry.
            if (Collides(schema, out var collision))
            {
                _loadDiagnostics.Add(
                    $"{Path.GetFileName(file)}: skipped — redeclares '{collision}', which its " +
                    "own include already declares. Loading it would invalidate the whole set.");
                continue;
            }

            _schemas.Add(schema);
            if (!string.IsNullOrEmpty(schema.TargetNamespace))
            {
                _knownNamespaces.Add(schema.TargetNamespace);
            }

            foreach (var name in GlobalElementNames(schema))
            {
                _declared.Add(name);
            }
        }

        _compiled = false;
    }

    /// <summary>
    /// Schemas that others include are added first, so a redeclaration is detected
    /// against the file that legitimately owns the name rather than by whichever
    /// the directory walk happened to reach first.
    /// </summary>
    private static IEnumerable<(string File, XmlSchema Schema)> Ordered(
        IReadOnlyCollection<(string File, XmlSchema Schema)> read)
    {
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (file, schema) in read)
        {
            foreach (var external in schema.Includes.OfType<XmlSchemaExternal>())
            {
                if (string.IsNullOrEmpty(external.SchemaLocation))
                {
                    continue;
                }

                try
                {
                    var directory = Path.GetDirectoryName(file);
                    if (directory is not null)
                    {
                        included.Add(Path.GetFullPath(
                            Path.Combine(directory, external.SchemaLocation)));
                    }
                }
                catch (ArgumentException)
                {
                    // A location that is not a usable path tells us nothing about
                    // ordering; compilation will report it if it matters.
                }
            }
        }

        return read
            .OrderByDescending(entry => included.Contains(Path.GetFullPath(entry.File)))
            .ToList();
    }

    private bool Collides(XmlSchema schema, out string? collision)
    {
        foreach (var name in GlobalElementNames(schema))
        {
            if (_declared.Contains(name))
            {
                collision = name;
                return true;
            }
        }

        collision = null;
        return false;
    }

    private static IEnumerable<string> GlobalElementNames(XmlSchema schema) =>
        schema.Items.OfType<XmlSchemaElement>()
            .Where(element => !string.IsNullOrEmpty(element.Name))
            .Select(element => $"{schema.TargetNamespace}:{element.Name}");

    /// <summary>
    /// Content hash, so two vendored copies of the same schema are recognised as one
    /// file regardless of which package directory they were reached through.
    /// </summary>
    private static string Fingerprint(string file)
    {
        try
        {
            return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
        }
        catch (IOException)
        {
            // Unreadable here means it was read a moment ago and cannot be now;
            // treat it as distinct rather than silently collapsing two schemas.
            return file;
        }
    }

    /// <summary>
    /// Compiles at most once.
    ///
    /// A published package can carry a defect in one BOD that has nothing to do with
    /// the messages under test — ws-CIR redeclares globals, and the CCOM Show BODs
    /// reference a 'Count' type the package never declares. Because XmlSchemaSet
    /// compiles as a whole, any one of these otherwise disables validation for every
    /// namespace. So a failed compile is retried without the files the errors name,
    /// and what was dropped is recorded rather than passed off as absent schemas.
    /// </summary>
    private void EnsureCompiled()
    {
        if (_compiled)
        {
            return;
        }

        _compiled = true;

        var faulted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (TryCompile(faulted) || faulted.Count == 0)
        {
            return;
        }

        foreach (var source in faulted)
        {
            _loadDiagnostics.Add(
                $"{Path.GetFileName(source)}: dropped — it did not compile, and one " +
                "uncompilable schema invalidates the whole set.");
        }

        var retained = _schemas.Schemas()
            .OfType<XmlSchema>()
            .Where(schema => schema.SourceUri is null || !faulted.Contains(schema.SourceUri))
            .ToList();

        _schemas = new XmlSchemaSet();
        _knownNamespaces.Clear();

        foreach (var schema in retained)
        {
            _schemas.Add(schema);
            if (!string.IsNullOrEmpty(schema.TargetNamespace))
            {
                _knownNamespaces.Add(schema.TargetNamespace);
            }
        }

        if (!TryCompile(null))
        {
            // Two failures means the fault is not isolated to the files named, and
            // guessing further would risk reporting a valid document as unvalidated
            // for the wrong reason.
            _loadDiagnostics.Add(
                "Schema set still did not compile after dropping the faulted files.");
            _knownNamespaces.Clear();
        }
    }

    private bool TryCompile(HashSet<string>? faulted)
    {
        void OnEvent(object? sender, ValidationEventArgs args)
        {
            _loadDiagnostics.Add($"{args.Severity}: {args.Message}");

            var source = args.Exception?.SourceUri;
            if (faulted is not null &&
                args.Severity == XmlSeverityType.Error &&
                !string.IsNullOrEmpty(source))
            {
                faulted.Add(source);
            }
        }

        var before = _loadDiagnostics.Count;

        try
        {
            _schemas.ValidationEventHandler += OnEvent;
            _schemas.Compile();
        }
        catch (Exception ex) when (ex is XmlSchemaException or XmlException)
        {
            _loadDiagnostics.Add($"Schema set failed to compile: {ex.Message}");

            if (faulted is not null && ex is XmlSchemaException { SourceUri: { } uri })
            {
                faulted.Add(uri);
            }

            return false;
        }
        finally
        {
            _schemas.ValidationEventHandler -= OnEvent;
        }

        return _loadDiagnostics.Count == before;
    }

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
