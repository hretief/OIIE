namespace CirProvider.Domain;

/// <summary>
/// The ws-CIR fault set. Names are preserved verbatim because conformance
/// (§5) is declared against them; the HTTP status is the REST projection.
/// </summary>
public enum CirFaultCode
{
    RegistryNotFoundFault,
    CategoryNotFoundFault,
    EntryNotFoundFault,
    PropertyNotFoundFault,
    DuplicateEntryFault,
    DuplicatePropertyFault,
    CreateRegistryFault,
    CreateCategoryFault
}

public sealed class CirFaultException : Exception
{
    public CirFaultException(CirFaultCode code, string message)
        : base(message)
    {
        Faults = [new CirFault(code, message)];
    }

    public CirFaultException(IReadOnlyList<CirFault> faults)
        : base(faults.Count == 1 ? faults[0].Detail : $"{faults.Count} faults occurred.")
    {
        Faults = faults;
    }

    public IReadOnlyList<CirFault> Faults { get; }

    /// <summary>
    /// The WSDL binding returns one fault; the OAGIS message model permits many.
    /// REST follows the message model, so status reflects the first fault.
    /// </summary>
    public int StatusCode => Faults[0].Code switch
    {
        CirFaultCode.RegistryNotFoundFault => 404,
        CirFaultCode.CategoryNotFoundFault => 404,
        CirFaultCode.EntryNotFoundFault => 404,
        CirFaultCode.PropertyNotFoundFault => 404,
        CirFaultCode.DuplicateEntryFault => 409,
        CirFaultCode.DuplicatePropertyFault => 409,
        CirFaultCode.CreateRegistryFault => 403,
        CirFaultCode.CreateCategoryFault => 403,
        _ => 500
    };
}

public sealed record CirFault(CirFaultCode Code, string Detail);
