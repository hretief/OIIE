using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EngProvider.E2E.Tests;

/// <summary>
/// Hosts the real ENG Functions app for the duration of the test class.
///
/// The provider is started as a separate process and driven over HTTP, because
/// that is the only way to prove the things this suite exists to prove: that
/// routing, model binding, JSON serialization, status-code mapping and the
/// database triggers all agree once they are wired together. An in-process test
/// against IEngDesignStore cannot fail when a route template is wrong.
/// </summary>
public sealed class EngHostFixture : IAsyncLifetime
{
    // Each run gets its own database. Sharing one would make assertions depend
    // on what an earlier run left behind, and the first failure would cascade.
    private readonly string _database = $"EngE2E_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
    private readonly int _port = FreeTcpPort();

    private Process? _host;
    private readonly List<string> _hostOutput = [];
    private readonly Lock _outputLock = new();

    public HttpClient Client { get; private set; } = null!;

    /// <summary>Seed identifiers read back from the bootstrap, not hard-coded.</summary>
    public Guid IModelId { get; private set; }

    public long ConcreteClassId { get; private set; }

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private string MasterConnectionString =>
        @"Server=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=true;TrustServerCertificate=true;";

    private string DatabaseConnectionString =>
        $@"Server=(localdb)\MSSQLLocalDB;Initial Catalog={_database};Integrated Security=true;TrustServerCertificate=true;";

    public async Task InitializeAsync()
    {
        await CreateDatabaseAsync();

        // The host applies the schema itself via SchemaInitializer. Letting it do
        // so is deliberate: cold-start schema creation is part of the behaviour
        // under test, and applying the DDL here would hide a failure in it.
        StartHost();
        await WaitForHealthyAsync();

        // Bootstrap runs after the schema exists, since it populates it.
        await ApplyBootstrapAsync();
        await ReadSeedIdentifiersAsync();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();

        // Guarded with Id rather than HasExited: if Start() threw, the Process
        // object exists but has no process behind it, and HasExited would throw
        // an exception that replaces the real initialization failure with a
        // useless one on every test in the collection.
        if (_host is not null && TryGetHostAlive())
        {
            _host.Kill(entireProcessTree: true);
            await _host.WaitForExitAsync();
        }

        _host?.Dispose();
        await DropDatabaseAsync();
    }

    private bool TryGetHostAlive()
    {
        try
        {
            return _host is { Id: > 0 } && !_host.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Host output, surfaced when a test fails. Without this a failed assertion
    /// says only that a call returned 500, and the stack trace explaining why
    /// stays invisible in a process the test never sees.
    /// </summary>
    public string HostOutput
    {
        get { lock (_outputLock) return string.Join(Environment.NewLine, _hostOutput); }
    }

    private async Task CreateDatabaseAsync()
    {
        await using var cn = new SqlConnection(MasterConnectionString);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"IF DB_ID('{_database}') IS NULL CREATE DATABASE [{_database}];", cn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DropDatabaseAsync()
    {
        try
        {
            await using var cn = new SqlConnection(MasterConnectionString);
            await cn.OpenAsync();
            await using var cmd = new SqlCommand(
                $"IF DB_ID('{_database}') IS NOT NULL BEGIN " +
                $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{_database}]; END;", cn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // A leftover scratch database is untidy but must not fail a green run.
        }
    }

    private async Task ApplyBootstrapAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ENG_BOOTSTRAP.SQL");
        var sql = await File.ReadAllTextAsync(path);

        await using var cn = new SqlConnection(DatabaseConnectionString);
        await cn.OpenAsync();

        // SqlClient cannot execute GO; it is a batch separator, not T-SQL.
        foreach (var batch in SplitOnGo(sql))
        {
            await using var cmd = new SqlCommand(batch, cn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static IEnumerable<string> SplitOnGo(string sql) =>
        System.Text.RegularExpressions.Regex
            .Split(sql, @"^\s*GO\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Where(b => !string.IsNullOrWhiteSpace(b));

    /// <summary>
    /// Reads the seed iModel and a concrete class from the API itself.
    ///
    /// Hard-coding the bootstrap's GUIDs would couple the tests to a file that is
    /// free to change, and would keep passing after the bootstrap stopped
    /// producing them.
    /// </summary>
    private async Task ReadSeedIdentifiersAsync()
    {
        var models = await Client.GetFromJsonAsync<List<EngIModelDto>>("api/imodels", Json)
                     ?? throw new InvalidOperationException("Bootstrap produced no iModels.");

        IModelId = models.Count > 0
            ? models[0].IModelId
            : throw new InvalidOperationException($"Bootstrap produced no iModels. Host output:{Environment.NewLine}{HostOutput}");

        var classes = await Client.GetFromJsonAsync<List<EngClassDto>>("api/classes", Json)
                      ?? throw new InvalidOperationException("Bootstrap produced no classes.");

        ConcreteClassId = classes.Count > 0
            ? classes[0].ECClassId
            : throw new InvalidOperationException("Bootstrap produced no concrete classes.");
    }

    private void StartHost()
    {
        var projectDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "EngProvider"));

        var psi = new ProcessStartInfo
        {
            FileName = ResolveFuncExecutable(),
            // No --no-build: func's own build produces the bin/output layout it
            // then expects, including the .azurefunctions folder. Pointing it at
            // a separately built folder makes it report "No job functions found".
            Arguments = $"start --port {_port}",
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Environment overrides beat local.settings.json, so the test never
        // touches the Azure database named there. This is the safety property
        // that matters most in this file.
        psi.Environment["Eng__SqlConnectionString"] = DatabaseConnectionString;
        psi.Environment["Eng__AutoCreateSchema"] = "true";
        psi.Environment["AzureWebJobsStorage"] = "";
        psi.Environment["FUNCTIONS_WORKER_RUNTIME"] = "dotnet-isolated";

        _host = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _host.OutputDataReceived += (_, e) => Capture(e.Data);
        _host.ErrorDataReceived += (_, e) => Capture(e.Data);

        _host.Start();
        _host.BeginOutputReadLine();
        _host.BeginErrorReadLine();

        Client = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{_port}/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    private void Capture(string? line)
    {
        if (line is null) return;
        lock (_outputLock) _hostOutput.Add(line);
    }

    /// <summary>
    /// Polls until the host answers. A fixed sleep would be flaky in both
    /// directions: too short and the suite fails on a cold JIT, too long and
    /// every run pays for the worst case.
    /// </summary>
    private async Task WaitForHealthyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);

        while (DateTime.UtcNow < deadline)
        {
            if (TryHostExited())
            {
                throw new InvalidOperationException(
                    $"Functions host exited with code {_host!.ExitCode}.{Environment.NewLine}{HostOutput}");
            }

            try
            {
                using var response = await Client.GetAsync("api/health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // Host is not listening yet.
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Functions host did not become healthy within 120s.{Environment.NewLine}{HostOutput}");
    }

    private static int FreeTcpPort()
    {
        // Binding to port 0 lets the OS pick, so parallel or repeated runs do not
        // collide on a hard-coded port left open by a previous host.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private bool TryHostExited()
    {
        try
        {
            return _host is { Id: > 0 } && _host.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the real func executable.
    ///
    /// "func" on PATH is a .cmd/.ps1 shim, which the shell resolves but
    /// Process.Start does not when UseShellExecute is false. Redirecting output
    /// requires UseShellExecute=false, so the actual .exe has to be located.
    /// </summary>
    private static string ResolveFuncExecutable()
    {
        var candidates = new List<string>();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(appData, "npm", "node_modules",
            "azure-functions-core-tools", "bin", "func.exe"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(programFiles, "Microsoft", "Azure Functions Core Tools", "func.exe"));

        // Anything explicitly on PATH wins over a guessed install location.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Insert(0, Path.Combine(dir.Trim(), "func.exe"));
        }

        var found = candidates.FirstOrDefault(File.Exists);

        return found ?? throw new InvalidOperationException(
            "Could not locate func.exe. Install Azure Functions Core Tools to run the ENG end-to-end suite.");
    }

    private sealed record EngIModelDto(Guid IModelId, Guid ITwinId, string Code);

    private sealed record EngClassDto(long ECClassId, string FullyQualifiedName, string ClassModifier);
}

[CollectionDefinition("eng-host")]
public sealed class EngHostCollection : ICollectionFixture<EngHostFixture>;
