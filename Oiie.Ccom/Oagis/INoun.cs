namespace Oiie.Ccom.Oagis;

/// <summary>
/// Marker constraining the TNoun type parameter of <see cref="CcomBod{TVerb, TNoun}"/>,
/// so a verb type cannot accidentally be supplied where a noun belongs.
/// </summary>
public interface INoun
{
}
