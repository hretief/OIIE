using System.Xml.Linq;
using Oiie.Participant.Engine.Contracts;

namespace Oiie.Participant.Engine.Plan;

/// Parses the XML a stylesheet emits into the model above. A compiled transform
/// builds the model directly and never touches this class — which is exactly why
/// both implementations satisfy the same interface.
public static class WritePlanParser
{
    public static WritePlan Parse(XDocument doc)
    {
        var root = doc.Root ?? throw new PlanFormatException("empty document");
        if (root.Name.LocalName != "WritePlan")
            throw new PlanFormatException($"expected <WritePlan>, found <{root.Name.LocalName}>");

        return new WritePlan(
            Req(root, "participant"),
            Req(root, "noun"),
            Req(root, "bodId"),
            [.. root.Elements("Item").Select(ParseItem)]);
    }

    private static PlanItem ParseItem(XElement e) => new(
        Req(e, "sourceRef"),
        [.. e.Elements("Op").Select(ParseOp)],
        [.. e.Elements("Register").Select(ParseRegister)]);

    private static PlanOp ParseOp(XElement e) => new(
        Req(e, "id"),
        Req(e, "resource"),
        Enum.Parse<OpMode>(Req(e, "mode"), ignoreCase: true),
        (string?)e.Attribute("match"),
        [.. e.Elements("Field").Select(ParseField)],
        [.. e.Elements("Returns").Select(r => new ReturnSpec(Req(r, "name"), Req(r, "as")))]);

    private static PlanField ParseField(XElement e)
    {
        var name = Req(e, "name");
        var onUpdate = (string?)e.Attribute("onUpdate")
            // §4.2 — the engine MUST reject a plan whose field lacks onUpdate.
            // Defaulting it here would silently reintroduce the overwrite bug
            // that DR-032 and §6.1 exist to prevent.
            ?? throw new PlanFormatException($"field '{name}' has no onUpdate");

        var raw = (string?)e.Attribute("ref");
        var text = e.IsEmpty ? null : e.Value;

        var sources = new[] { raw, text }.Count(v => !string.IsNullOrEmpty(v));
        if (sources != 1)
            throw new PlanFormatException($"field '{name}' must have exactly one of @ref or text");

        var policy = Enum.Parse<UpdatePolicy>(onUpdate.Replace("-", ""), ignoreCase: true);

        if (raw is null)
            return new PlanField(name, policy, Literal: text);

        return raw.StartsWith('$')
            ? new PlanField(name, policy, ContextRef: raw[1..])
            : new PlanField(name, policy, OpRef: raw);
    }

    private static RegisterOp ParseRegister(XElement e) => new(
        Req(e, "cirId"),
        Req(e, "category"),
        Req(e, "idInSource"),
        Req(e, "merge") switch
        {
            "adopt-existing" => MergeMode.AdoptExisting,
            "replace"        => MergeMode.Replace,
            var other        => throw new PlanFormatException($"unknown merge '{other}'")
        });

    private static string Req(XElement e, string attr) =>
        (string?)e.Attribute(attr)
        ?? throw new PlanFormatException($"<{e.Name.LocalName}> missing @{attr}");
}

public sealed class PlanFormatException(string message) : Exception(message);
