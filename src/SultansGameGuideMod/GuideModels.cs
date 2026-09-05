namespace SultansGameGuide;

public enum NodeKind { Event, Rite, AfterStory }

public sealed class GuideNode
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public NodeKind Kind { get; init; }
    public string SourcePath { get; init; } = "";
    public string HumanCondition { get; set; } = "无特殊条件";
    public List<GuideLink> Links { get; } = new();
    public string? ResultText { get; set; }
}

public sealed record GuideLink(string Label, int TargetId, NodeKind? TargetKind = null);
