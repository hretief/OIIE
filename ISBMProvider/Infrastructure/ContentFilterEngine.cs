using System.Collections.Concurrent;
using System.Xml;
using System.Xml.XPath;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;                    // JSONPath (SelectTokens) — contained to this file only
using IsbmProvider.Abstractions;
using IsbmProvider.Models;

namespace IsbmProvider.Infrastructure;

/// <summary>
/// Body-level content filtering (ISBM conformance items 12–13):
///   * XML  (CCOM BODs) -> XPath 1.0 via System.Xml.XPath, with session namespace prefixes.
///   * JSON              -> JSONPath via Newtonsoft SelectTokens.
///
/// A message matches only if it satisfies ALL of the session's filter expressions (AND).
/// An empty filter list matches everything. Compiled XPath expressions are cached.
///
/// Fail-open policy: if an individual expression cannot be evaluated (malformed filter, or body
/// not in the language the filter targets) the engine logs and treats that expression as satisfied,
/// so a bad filter never silently black-holes a subscriber. Validate filters at OpenSubscriptionSession
/// for fail-fast behaviour instead.
/// </summary>
public sealed class ContentFilterEngine : IFilterEngine
{
    private readonly ILogger<ContentFilterEngine> _log;
    private readonly ConcurrentDictionary<string, XPathExpression> _xpathCache = new();

    public ContentFilterEngine(ILogger<ContentFilterEngine> log) => _log = log;

    public bool Matches(MessageContent content, IReadOnlyList<string> filterExpressions,
        IReadOnlyDictionary<string, string> namespaces)
    {
        if (filterExpressions.Count == 0) return true;

        var body = content.InlineContent;
        if (string.IsNullOrEmpty(body))
        {
            // Caller must resolve claim-checked payloads before filtering; nothing to match against.
            _log.LogWarning("Filter skipped: message body not resolved (PayloadRef={Ref}).", content.PayloadRef);
            return true;
        }

        var isJson = content.MediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

        foreach (var expr in filterExpressions)
        {
            bool ok = isJson ? EvaluateJsonPath(body, expr) : EvaluateXPath(body, expr, namespaces);
            if (!ok) return false;                 // AND semantics — one failure drops the message
        }
        return true;
    }

    // ---- XPath 1.0 ---------------------------------------------------------

    private bool EvaluateXPath(string xml, string expression, IReadOnlyDictionary<string, string> namespaces)
    {
        try
        {
            var nav = new XPathDocument(new StringReader(xml)).CreateNavigator();
            var compiled = _xpathCache.GetOrAdd(expression, XPathExpression.Compile);

            var nsmgr = new XmlNamespaceManager(nav.NameTable);
            foreach (var (prefix, uri) in namespaces) nsmgr.AddNamespace(prefix, uri);
            compiled.SetContext(nsmgr);

            var result = nav.Evaluate(compiled);
            return result switch
            {
                bool b => b,                                   // boolean XPath, e.g. contains(...) or a=b
                double d => d != 0,
                string s => s.Length > 0,
                XPathNodeIterator it => it.Count > 0,          // node-set: match if non-empty
                _ => false
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "XPath filter '{Expr}' could not be evaluated; treating as matched.", expression);
            return true;                                       // fail-open
        }
    }

    // ---- JSONPath ----------------------------------------------------------

    private bool EvaluateJsonPath(string json, string expression)
    {
        try
        {
            var token = JToken.Parse(json);
            return token.SelectTokens(expression, errorWhenNoMatch: false).Any();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "JSONPath filter '{Expr}' could not be evaluated; treating as matched.", expression);
            return true;                                       // fail-open
        }
    }
}
