using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;

namespace Oiie.Ccom.Bods;

public class SyncSegments : SyncBodBase<Segment>
{
    public SyncSegments()
    {
    }

    public SyncSegments(string actionCode) : base(actionCode)
    {
    }
}

/// <summary>
/// Synchronises sites -- the plants, facilities and projects a segment can belong to.
///
/// This is the one Sync BOD that has to travel on an enterprise-level channel. Every
/// other flow is scoped to an iTwin, but this is what creates the iTwin context, so
/// there is no per-iTwin channel to carry it yet.
///
/// No members are declared here because CCOM models Site as a specialisation of
/// Segment: ShortName, FullName, Description and Type are inherited. Restating them
/// would create a second set that could disagree with the first.
/// </summary>
public class SyncSites : SyncBodBase<Site>
{
    public SyncSites()
    {
    }

    public SyncSites(string actionCode) : base(actionCode)
    {
    }
}

public class SyncAssets : SyncBodBase<Asset>
{
    public SyncAssets()
    {
    }

    public SyncAssets(string actionCode) : base(actionCode)
    {
    }
}

public class SyncAssetSegmentEvents : SyncBodBase<AssetSegmentEvent>
{
    public SyncAssetSegmentEvents()
    {
    }

    public SyncAssetSegmentEvents(string actionCode) : base(actionCode)
    {
    }
}

/// <summary>
/// Synchronises segment meshes and the directed connections they contain.
///
/// The wrapper is SegmentMeshConnections rather than the conventional noun + "s",
/// so DataAreaNodeName is stated explicitly. This is the only Sync BOD in the
/// Sandbox that carries relationships: CCOM has no envelope for a free-standing
/// SegmentConnection, so an edge can only be published inside a mesh.
/// </summary>
public class SyncSegmentMeshConnections : SyncBodBase<SegmentMesh>
{
    public SyncSegmentMeshConnections()
    {
    }

    public SyncSegmentMeshConnections(string actionCode) : base(actionCode)
    {
    }

    public override string DataAreaNodeName => "SegmentMeshConnections";
}
