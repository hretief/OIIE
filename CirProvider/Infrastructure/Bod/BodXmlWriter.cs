using System.Xml.Linq;
using CirProvider.Domain;
using CirProvider.Domain.Bod;

namespace CirProvider.Infrastructure.Bod;

/// <summary>
/// Emits ws-CIR BODs. Envelope shape follows the OAGIS platform specification
/// referenced by Annex A: a BusinessObjectDocumentType extension carrying an
/// ApplicationArea and a DataArea, where the DataArea holds the verb element
/// followed by the noun.
/// </summary>
public static class BodXmlWriter
{
    private static XNamespace N => CirNs.Cir;
    private static XNamespace Oa => CirNs.Oa;

    /// <summary>Annex A: releaseID MUST be the referenced OAGIS release.</summary>
    public const string ReleaseId = "1.2.1";

    /// <summary>Annex A: versionID is the version of the BOD.</summary>
    public const string VersionId = "1.0";

    public static XDocument Write(BodResult result, ApplicationArea sender)
    {
        if (result.ResponseBodName is null)
            throw new InvalidOperationException("This result defines no response BOD.");

        var definition = BodCatalogue.Find(result.ResponseBodName)
            ?? throw new InvalidOperationException($"'{result.ResponseBodName}' is not in the BOD catalogue.");

        // Meta.xsd: ResponseVerbType carries OriginalApplicationArea, so it sits
        // inside the verb element rather than beside the ApplicationArea.
        var verb = new XElement(Oa + definition.Verb.ToString());
        if (result.OriginalApplicationArea is not null)
            verb.Add(WriteApplicationArea(Oa + "OriginalApplicationArea", result.OriginalApplicationArea));

        var dataArea = new XElement(N + "DataArea", verb);
        foreach (var content in WriteDataAreaContent(definition, result))
            dataArea.Add(content);

        var bod = new XElement(N + definition.BodName,
            new XAttribute(XNamespace.Xmlns + "cir", N.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "oa", Oa.NamespaceName),
            new XAttribute("releaseID", ReleaseId),
            new XAttribute("versionID", VersionId),
            WriteApplicationArea(Oa + "ApplicationArea", sender),
            dataArea);

        return new XDocument(new XDeclaration("1.0", "utf-8", null), bod);
    }

    /// <summary>
    /// ApplicationAreaType is an OAGIS element, so it is emitted in the oa
    /// namespace even though DataArea belongs to ws-CIR. Meta.xsd fixes the
    /// child order: Sender, Receiver, CreationDateTime, Signature, BODID, UserArea.
    ///
    /// Annex A allows OriginalApplicationArea to be omitted when other message
    /// correlation is used — over ws-ISBM that is the OriginalMessageID.
    /// </summary>
    private static XElement WriteApplicationArea(XName name, ApplicationArea area) =>
        new(name,
            new XElement(Oa + "Sender", new XElement(Oa + "LogicalID", area.SenderLogicalId ?? "ws-CIR")),
            new XElement(Oa + "CreationDateTime", area.CreationDateTime.ToString("o")),
            new XElement(Oa + "BODID", area.BodId));

    /// <summary>
    /// Acknowledge and Respond DataAreas have no noun. Per AcknowledgeRegistry.xsd
    /// they are the verb followed by fault elements — each named for the fault,
    /// each repeatable, and in the declaration order of the xsd:sequence, which
    /// means all faults of one type must be grouped together.
    ///
    /// Show DataAreas do carry a noun, named for the operation's response type.
    /// An Acknowledge with no fault elements is a success.
    /// </summary>
    private static IEnumerable<XElement> WriteDataAreaContent(BodDefinition definition, BodResult result)
    {
        if (definition.CarriesFaults)
        {
            var byCode = result.Faults.GroupBy(f => f.Code).ToDictionary(g => g.Key, g => g.ToList());

            // Emit in schema order, not the order the faults were raised.
            foreach (var code in definition.FaultOrder)
            {
                if (!byCode.TryGetValue(code, out var faults)) continue;
                foreach (var fault in faults)
                    yield return WriteFault(code, fault);
                byCode.Remove(code);
            }

            // A fault the schema does not declare for this BOD would be invalid,
            // so surface it rather than dropping it silently.
            foreach (var (code, faults) in byCode)
                foreach (var fault in faults)
                    yield return WriteFault(code, fault);

            yield break;
        }

        var noun = new XElement(N + definition.Noun);
        foreach (var registry in result.Registries)
            noun.Add(WriteRegistry(registry));
        yield return noun;
    }

    /// <summary>
    /// Every fault element declares an optional Description child of TextType.
    /// The detail is therefore a child element, not the fault's text content —
    /// a client reading .InnerText would otherwise see nothing.
    ///
    /// The NotFound and Duplicate faults also declare a mandatory identifier
    /// child. Not emitted: the store reports faults as a code and a message, so
    /// the identifier would have to be reconstructed. See the conformance
    /// statement's non-conformance list.
    /// </summary>
    private static XElement WriteFault(CirFaultCode code, CirFault fault) =>
        new(N + code.ToString(), new XElement(N + "Description", fault.Detail));

    // --- Data model ---------------------------------------------------------

    public static XElement WriteRegistry(Registry registry)
    {
        var element = new XElement(N + "Registry", new XElement(N + "ID", registry.Id));
        foreach (var text in registry.Description) element.Add(WriteText("Description", text));
        foreach (var category in registry.Categories) element.Add(WriteCategory(category));
        return element;
    }

    private static XElement WriteCategory(Category category)
    {
        // Schema: Category has ID then CategorySourceID. Entry has IDInSource
        // then SourceID. The asymmetry is real — do not "tidy" it.
        var element = new XElement(N + "Category",
            new XElement(N + "ID", category.Id),
            new XElement(N + "CategorySourceID", category.SourceId));
        foreach (var text in category.Description) element.Add(WriteText("Description", text));
        foreach (var entry in category.Entries) element.Add(WriteEntry(entry));
        return element;
    }

    private static XElement WriteEntry(Entry entry)
    {
        var element = new XElement(N + "Entry",
            new XElement(N + "IDInSource", entry.IdInSource),
            new XElement(N + "SourceID", entry.SourceId));

        if (entry.Cirid is not null) element.Add(new XElement(N + "CIRID", entry.Cirid.Value.ToString()));
        if (entry.SourceOwnerId is not null) element.Add(new XElement(N + "SourceOwnerID", entry.SourceOwnerId));
        if (entry.Name is not null) element.Add(new XElement(N + "Name", entry.Name));
        if (entry.Description is not null) element.Add(WriteText("Description", entry.Description));
        if (entry.Inactive is not null) element.Add(new XElement(N + "Inactive", entry.Inactive.Value));

        foreach (var property in entry.Properties) element.Add(WriteProperty(property));
        return element;
    }

    private static XElement WriteProperty(Property property)
    {
        var element = new XElement(N + "Property", new XElement(N + "ID", property.Id));
        if (property.DataType is not null) element.Add(new XElement(N + "DataType", property.DataType));

        foreach (var value in property.PropertyValue)
        {
            var pv = new XElement(N + "PropertyValue");
            if (value.Key is not null) pv.Add(new XElement(N + "Key", value.Key));
            pv.Add(new XElement(N + "Value", value.Value));
            if (value.UnitOfMeasure is not null) pv.Add(new XElement(N + "UnitOfMeasure", value.UnitOfMeasure));
            element.Add(pv);
        }

        return element;
    }

    /// <summary>UN/CEFACT TextType carries language on an attribute, not an element.</summary>
    private static XElement WriteText(string name, LocalizedText text)
    {
        var element = new XElement(N + name, text.Value);
        if (text.LanguageId is not null) element.Add(new XAttribute("languageID", text.LanguageId));
        return element;
    }
}
