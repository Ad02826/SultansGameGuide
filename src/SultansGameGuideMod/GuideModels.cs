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
    // 事件触发时机（例如每回合开始、某仪式结束后）。
    public string HumanTiming { get; set; } = "";

    // 调试 / 实时判定用，正常界面不直接显示机器条件。
    public string RawCondition { get; set; } = "";

    // 从配置中直接扫描到的原始出边；后面会聚合成关系分支。
    public List<GuideOutgoingTrigger> OutgoingTriggers { get; } = new();

    // 统一关系图：
    // IncomingRelations = 哪些事件 / 仪式会产生当前节点。
    // OutgoingRelations = 当前节点会产生哪些事件 / 仪式。
    public List<GuideRelationBranch> IncomingRelations { get; } = new();
    public List<GuideRelationBranch> OutgoingRelations { get; } = new();

    // 旧版平铺链接暂时保留给搜索/兼容逻辑；详情页不再直接使用。
    public List<GuideLink> Links { get; } = new();
    public string? ResultText { get; set; }
}

public sealed class GuideOutgoingTrigger
{
    public string Label { get; init; } = "";
    public int TargetId { get; init; }
    public NodeKind TargetKind { get; init; }

    // rite / event_on / event
    public string RelationType { get; init; } = "";

    public string HumanCondition { get; init; } = "没有额外要求。";
    public string RawCondition { get; init; } = "";
}

public sealed class GuideRelationBranch
{
    // IncomingRelations 中是来源节点；
    // OutgoingRelations 中是目标节点。
    public int NodeId { get; init; }
    public NodeKind NodeKind { get; init; }
    public string NodeName { get; init; } = "";

    // 同一个来源/目标可能存在多条 settlement / case / success 路径。
    // 外层只显示一次节点名，展开后再列出路径。
    public List<GuideRelationPath> Paths { get; } = new();
}

public sealed class GuideRelationPath
{
    public string Context { get; init; } = "";
    public string Timing { get; init; } = "";
    public string HumanCondition { get; init; } = "没有额外要求。";
    public string RawCondition { get; init; } = "";
    public string ActionText { get; init; } = "";
    public string RelationType { get; init; } = "";
}

public sealed record GuideLink(
    string Label,
    int TargetId,
    NodeKind? TargetKind = null
);
