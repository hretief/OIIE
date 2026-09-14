using System.Net;

namespace Oiie.Participant.Engine.Failures;

/// §6 — THE DEFAULT IS TRANSIENT.
///
/// This is the inverse of the natural implementation and it is deliberate. A
/// Rejected verdict is terminal and never retried, so a transient condition
/// misreported as terminal destroys data. A terminal condition misreported as
/// transient merely retries until a human looks.
public sealed class FailureClassifier(FailureRules rules) : IFailureClassifier
{
    public FailureVerdict Classify(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } s } when rules.TerminalHttp.Contains((int)s)
            => new FailureVerdict(Transient: false, $"HTTP {(int)s}"),

        Microsoft.Data.SqlClient.SqlException sql when rules.TerminalSql.Contains(sql.Number)
            => new FailureVerdict(Transient: false, $"SQL {sql.Number}"),

        // Everything else — including every condition nobody thought to declare.
        _ => new FailureVerdict(Transient: true, $"{ex.GetType().Name}")
    };
}

public interface IFailureClassifier
{
    FailureVerdict Classify(Exception ex);
}

public readonly record struct FailureVerdict(bool Transient, string Reason);

public sealed record FailureRules(IReadOnlySet<int> TerminalHttp, IReadOnlySet<int> TerminalSql)
{
    /// Loaded from failures.yaml at startup. A rule keyed on message text is
    /// rejected here rather than accepted and quietly relied upon: message text
    /// is locale-dependent and changes between provider versions.
    public static FailureRules Load(IDictionary<string, object> yaml)
    {
        if (yaml.ContainsKey("messageContains") || yaml.ContainsKey("messageMatches"))
            throw new InvalidOperationException(
                "failures.yaml may not classify on message text. Use http status, "
              + "exception type, or SQL error number.");

        // ... parse terminal http / sqlError lists ...
        return new FailureRules(new HashSet<int>(), new HashSet<int>());
    }
}
