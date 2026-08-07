namespace Oiie.Ccom.Oagis.Verbs;

public class Sync : Verb
{
    public ActionCriteria? ActionCriteria { get; set; }
}

public class Process : Verb
{
    public ActionCriteria? ActionCriteria { get; set; }
}

public class Change : Verb
{
    public ActionCriteria? ActionCriteria { get; set; }
}

public class Cancel : Verb
{
    public ActionCriteria? ActionCriteria { get; set; }
}

public class Get : Verb
{
    public string? UniqueIndicator { get; set; }
    public ResponseCriteria? ResponseCriteria { get; set; }
}

public class Show : Verb
{
    public ResponseCriteria? ResponseCriteria { get; set; }
}

public class Acknowledge : Verb
{
    public ResponseCriteria? ResponseCriteria { get; set; }
}

public class Respond : Verb
{
    public ResponseCriteria? ResponseCriteria { get; set; }
}
