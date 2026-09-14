using System.Xml.Linq;
using Saxon.Api;
using Oiie.Participant.Engine.Contracts;
using Oiie.Participant.Engine.Plan;

namespace Oiie.Participant.Engine.Transforms;

/// §3.3 — the human-authored implementation. The stylesheet is compiled once at
/// startup; Xslt30Transformer instances are not thread-safe, so one is loaded
/// per invocation from the shared XsltExecutable.
///
/// Runtime is SaxonCS-HE 13 (free from 13.0, May 2026, .NET 8+). .NET's built-in
/// XslCompiledTransform is XSLT 1.0 only and cannot run these maps.
///
/// NOTE the constructor signature: there is no ICirClient, no HttpClient, no
/// DbContext. A stylesheet cannot open a connection even if its author wants to,
/// which is how §3's purity constraint is enforced structurally rather than by
/// review — the caveat DR-013 admitted it could not close.
public sealed class XsltInboundTransform : IInboundTransform
{
    private readonly XsltExecutable _executable;
    private readonly Processor _processor;

    public ResolutionPlan Resolutions { get; }

    public XsltInboundTransform(Processor processor, Stream stylesheet, ResolutionPlan resolutions)
    {
        _processor  = processor;
        Resolutions = resolutions;
        _executable = processor.NewXsltCompiler().Compile(stylesheet);
    }

    public WritePlan Apply(XDocument bod, TransformContext context)
    {
        var transformer = _executable.Load30();

        // The ONLY channel by which external data reaches the stylesheet.
        transformer.SetStylesheetParameters(
            context.Resolved.ToDictionary(
                kv => new QName(kv.Key),
                kv => (XdmValue)new XdmAtomicValue(kv.Value)));

        using var input  = new StringReader(bod.ToString());
        using var output = new StringWriter();

        var destination = _processor.NewSerializer(output);
        transformer.ApplyTemplates(
            _processor.NewDocumentBuilder().Build(input), destination);

        return WritePlanParser.Parse(XDocument.Parse(output.ToString()));
    }
}
