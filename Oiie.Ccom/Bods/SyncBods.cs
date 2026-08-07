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
