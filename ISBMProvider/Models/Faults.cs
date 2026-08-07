namespace IsbmProvider.Models;

/// <summary>
/// ISBM fault kinds and their REST status mappings (from the spec's per-operation tables).
/// ChannelFault=404, OperationFault=422, SessionFault=404/422, NamespaceFault=400,
/// SecurityTokenFault=401.
/// </summary>
public enum FaultKind { Channel, Operation, Session, Namespace, SecurityToken }

public sealed class IsbmFaultException : Exception
{
    public FaultKind Kind { get; }
    public int StatusCode { get; }
    public IsbmFaultException(FaultKind kind, string message, int statusCode)
        : base(message) { Kind = kind; StatusCode = statusCode; }

    public static IsbmFaultException Channel(string m = "Channel does not exist or token mismatch.")
        => new(FaultKind.Channel, m, 404);
    public static IsbmFaultException Operation(string m = "Operation not valid for this channel.")
        => new(FaultKind.Operation, m, 422);
    public static IsbmFaultException Session(string m = "Session does not exist or wrong type.", int status = 404)
        => new(FaultKind.Session, m, status);
    public static IsbmFaultException Namespace(string m = "Invalid namespace prefixes in filter.")
        => new(FaultKind.Namespace, m, 400);
    public static IsbmFaultException SecurityToken(string m = "Invalid security token.")
        => new(FaultKind.SecurityToken, m, 401);
}
