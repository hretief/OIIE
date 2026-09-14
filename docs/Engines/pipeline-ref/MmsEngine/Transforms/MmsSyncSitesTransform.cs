using System.Xml.Linq;
using Oiie.Participant.Engine.Contracts;

namespace MmsEngine.Transforms;

/// §3.3 — the ESCAPE HATCH, and build-order step 3.
///
/// Identical semantics to SyncSites/transform.xslt, written in C#. Step 5 of the
/// build order swaps this for the stylesheet and requires the same golden files
/// to pass. That swap is the moment "pluggable" stops being an assertion.
///
/// Keep one of these in the tree permanently. Without it, the expression grammar
/// in the stylesheets accretes conditionals until it becomes a programming
/// language, badly.
public sealed class MmsSyncSitesTransform : IInboundTransform
{
    private static readonly XNamespace Ccom = "http://www.mimosa.org/ccom4";

    public ResolutionPlan Resolutions => ResolutionPlan.None;

    public WritePlan Apply(XDocument bod, TransformContext context)
    {
        var bodId = bod.Descendants(Ccom + "BODID").Single().Value;

        var items = bod.Descendants(Ccom + "Site").Select(site =>
        {
            var uuid = site.Element(Ccom + "UUID")!.Value;

            var op = new PlanOp(
                Id: "system",
                Resource: "LIGHT_SYSTEM_INVENTORY",
                Mode: OpMode.Upsert,
                Match: "EXT_ASSET_ID",
                Fields:
                [
                    new PlanField("EXT_ASSET_ID",      UpdatePolicy.Never,       Literal: uuid),
                    new PlanField("LIGHT_SYSTEM_NAME", UpdatePolicy.Set,         Literal: site.Element(Ccom + "ShortName")!.Value),
                    new PlanField("CLASSIFICATION",    UpdatePolicy.SetIfAbsent, Literal: "Undetermined"),
                    new PlanField("DATE_UPDATE",       UpdatePolicy.Set,         Literal: context.AsOfUtc.ToString("O")),
                ],
                Returns: []);

            return new PlanItem(uuid, [op], []);
        }).ToList();

        return new WritePlan("mms", "SyncSites", bodId, items);
    }
}
