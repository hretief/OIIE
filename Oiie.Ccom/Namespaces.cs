namespace Oiie.Ccom;

public static class Namespaces
{
    public const string Ccom = "http://www.mimosa.org/ccom4";
    public const string Oagis = "http://www.openapplications.org/oagis/9";
    /// <summary>ws-CIR 1.0. Note the trailing slash — it is part of the namespace.</summary>
    public const string Cir = "http://www.openoandm.org/ws-cir/";

    public const string XmlSchemaInstance = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>Extension namespace for Sandbox-local additions. Kept separate so
    /// extension content is always distinguishable from conformant CCOM.</summary>
    public const string SandboxExtensions = "http://www.openoandm.org/sandbox/extensions/1.0";
}
