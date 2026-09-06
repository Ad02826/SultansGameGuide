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

    // 事件触发时机（例如每回合开始、某仪式结束后）。
    public string HumanTiming { get; set; } = "";

    // 调试 / 实时判定用，正常界面不直接显示机器条件。
    public string RawCondition { get; set; } = "";

    // “谁会把这个节点打开 / 创建出来”的通用反向索引。
    public List<GuideTriggerBranch> TriggerBranches { get; } = new();

    // 本节点会打开哪些事件 / 仪式。
    // 与 Links 分开保存，是因为这里还保留了分支自身的局部条件。
    public List<GuideOutgoingTrigger> OutgoingTriggers { get; } = new();

    public List<GuideLink> Links { get; } = new();
    public string? ResultText { get; set; }
}

public sealed class GuideTriggerBranch
{
    public string Name { get; init; } = "";
    public int SourceId { get; init; }
    public NodeKind? SourceKind { get; init; }
    public string SourceName { get; init; } = "";
    public string SourceContext { get; init; } = "";
    public string Timing { get; init; } = "";
    public string HumanCondition { get; init; } = "没有额外要求。";
    public string RawCondition { get; init; } = "";
    public string Effect { get; init; } = "";
    public bool IsFallback { get; init; }
}

public sealed class GuideOutgoingTrigger
{
    public string Label { get; init; } = "";
    public int TargetId { get; init; }
    public NodeKind TargetKind { get; init; }
    public string HumanCondition { get; init; } = "没有额外要求。";
    public string RawCondition { get; init; } = "";
}

public sealed record GuideLink(
    string Label,
    int TargetId,
    NodeKind? TargetKind = null
);
