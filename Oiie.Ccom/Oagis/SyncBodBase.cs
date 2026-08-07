using Oiie.Ccom.Oagis.Verbs;
using Oiie.Ccom.Types;

namespace Oiie.Ccom.Oagis;

public static class ActionCodes
{
    public const string Add = "Add";
    public const string Change = "Change";
    public const string Replace = "Replace";
    public const string Delete = "Delete";
}

/// <summary>
/// Sync BODs differ only in their noun, so the ActionExpression XPath — which tells
/// the receiver which nodes the verb applies to — can be generated from the naming
/// conventions rather than restated per BOD.
/// </summary>
public abstract class SyncBodBase<TNoun> : CcomBod<Sync, TNoun>
    where TNoun : Entity
{
    private readonly string _actionCode;

    protected SyncBodBase() : this(ActionCodes.Add)
    {
    }

    protected SyncBodBase(string actionCode)
    {
        _actionCode = actionCode;
        DataArea = CreateDataArea();
    }

    protected sealed override DataArea<Sync, TNoun> CreateDataArea() => new()
    {
        Verb = new Sync
        {
            ActionCriteria = new ActionCriteria
            {
                ActionExpression = new ActionExpression
                {
                    ActionCode = _actionCode,
                    ExpressionLanguage = "Xpath",
                    Value = $"/{RootNodeName}/{nameof(DataArea)}/{DataAreaNodeName}"
                }
            }
        }
    };

    public SyncBodBase<TNoun> With(params TNoun[] nouns)
    {
        DataArea ??= CreateDataArea();
        DataArea.Entities.AddRange(nouns);
        return this;
    }
}
