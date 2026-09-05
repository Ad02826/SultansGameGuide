namespace SultansGameGuide;

public enum NodeKind
{
    Event,
    Rite,
    AfterStory
}

public sealed class GuideNode
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public NodeKind Kind { get; init; }
    public string SourcePath { get; init; } = "";

    // 给玩家看的自然中文。
    public string HumanCondition { get; set; } = "没有额外要求。";
    public string HumanOutcome { get; set; } = "";

    // 调试用，正常界面不显示机器条件。
    public string RawCondition { get; set; } = "";

    public List<GuideLink> Links { get; } = new();
    public string? ResultText { get; set; }
}

public sealed record GuideLink(
    string Label,
    int TargetId,
    NodeKind? TargetKind = null
);
