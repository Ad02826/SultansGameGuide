using System.Text.Json;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SultansGameGuide;

public sealed class GuideDatabase
{
    public readonly Dictionary<int, GuideNode> Nodes = new();
    public readonly Dictionary<int, string> CardNames = new();
    public readonly Dictionary<int, string> CounterHints = new();
    public readonly Dictionary<int, string> RiteNames = new();
    public readonly Dictionary<int, string> EventNames = new();

    static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public string ConfigRoot { get; private set; } = "";

    public void Load()
    {
        ConfigRoot = Path.Combine(Application.streamingAssetsPath, "config");
        LoadCards();
        ScanHumanHints();
        LoadFolder("event", NodeKind.Event);
        LoadFolder("rite", NodeKind.Rite);
        LoadFolder("after_story", NodeKind.AfterStory);
    }

    void LoadCards()
    {
        var p = Path.Combine(ConfigRoot, "cards.json");
        if (!File.Exists(p)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(p), JsonOptions);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out var id)) continue;
            if (prop.Value.TryGetProperty("name", out var n))
                CardNames[id] = n.GetString() ?? id.ToString();
        }
    }

    void ScanHumanHints()
    {
        // 游戏配置作者在大量 DSL 条件旁写了中文注释；把这些注释当作最可靠的人类语义字典。
        foreach (var p in Directory.EnumerateFiles(ConfigRoot, "*.json", SearchOption.AllDirectories))
        {
            foreach (var raw in File.ReadLines(p))
            {
                var cidx = raw.IndexOf("//", StringComparison.Ordinal);
                if (cidx < 0) continue;
                var code = raw[..cidx];
                var comment = raw[(cidx + 2)..].Trim();
                if (comment.Length < 2) continue;
                foreach (Match m in Regex.Matches(code, @"counter(?:\+)?\.(\d+)|counter\+(\d+)|counter\.(\d+)"))
                {
                    var s = m.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value;
                    if (int.TryParse(s, out var id) && !CounterHints.ContainsKey(id)) CounterHints[id] = comment;
                }
            }
        }
    }

    void LoadFolder(string folder, NodeKind kind)
    {
        var dir = Path.Combine(ConfigRoot, folder);
        if (!Directory.Exists(dir)) return;
        foreach (var p in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p), JsonOptions);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
                string name = kind switch
                {
                    NodeKind.Event => root.TryGetProperty("text", out var t) ? t.GetString() ?? $"事件 {id}" : $"事件 {id}",
                    _ => root.TryGetProperty("name", out var n) ? n.GetString() ?? $"{kind} {id}" : $"{kind} {id}"
                };
                var node = new GuideNode { Id = id, Name = name, Kind = kind, SourcePath = p };
                if (root.TryGetProperty("condition", out var cond)) node.HumanCondition = HumanizeCondition(cond);
                if (kind == NodeKind.AfterStory) LoadAfterStoryDetails(root, node);
                CollectLinks(root, node);
                Nodes[id] = node;
                if (kind == NodeKind.Event) EventNames[id] = name;
                if (kind == NodeKind.Rite) RiteNames[id] = name;
            }
            catch { }
        }
    }

    void LoadAfterStoryDetails(JsonElement root, GuideNode node)
    {
        if (!root.TryGetProperty("extra", out var extra) || extra.ValueKind != JsonValueKind.Array) return;
        var chunks = new List<string>();
        int i = 0;
        foreach (var e in extra.EnumerateArray())
        {
            i++;
            string cond = e.TryGetProperty("condition", out var c) ? HumanizeCondition(c) : "无特殊条件";
            string text = e.TryGetProperty("result_text", out var r) ? r.GetString() ?? "" : "";
            chunks.Add($"结局分支 {i}\n条件：{cond}\n{text}");
        }
        node.ResultText = string.Join("\n\n", chunks);
    }

    public string HumanizeCondition(JsonElement e, int depth = 0)
    {
        if (e.ValueKind != JsonValueKind.Object) return "无特殊条件";
        var lines = new List<string>();
        foreach (var p in e.EnumerateObject())
        {
            if (p.Name == "any" || p.Name == "all")
            {
                var inner = HumanizeCondition(p.Value, depth + 1);
                lines.Add((p.Name == "any" ? "满足以下任意一项：" : "同时满足：") + "\n" + Indent(inner));
                continue;
            }
            lines.Add(HumanizeAtom(p.Name, p.Value));
        }
        return lines.Count == 0 ? "无特殊条件" : string.Join("；\n", lines);
    }

    static string Indent(string s) => "  · " + s.Replace("\n", "\n  · ");

    string HumanizeAtom(string key, JsonElement value)
    {
        string val = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
        bool neg = key.StartsWith("!");
        if (neg) key = key[1..];

        if (key.StartsWith("have."))
        {
            var tail = key[5..];
            var parts = tail.Split('.');
            string who = HumanCard(parts[0]);
            string tag = parts.Length > 1 ? "（" + string.Join("/", parts.Skip(1)) + "）" : "";
            return neg ? $"不能拥有/存活：{who}{tag}" : $"拥有/仍存活：{who}{tag}";
        }
        if (key.StartsWith("table_have."))
        {
            var tail = key[11..];
            var parts = tail.Split('.');
            string who = HumanCard(parts[0]);
            string tag = parts.Length > 1 ? "（" + string.Join("/", parts.Skip(1)) + "）" : "";
            return neg ? $"不能处于闲置区：{who}{tag}" : $"当前闲置：{who}{tag}";
        }
        var cm = Regex.Match(key, @"^counter\.(\d+)(>=|<=|>|<|=)?$");
        if (cm.Success)
        {
            int id = int.Parse(cm.Groups[1].Value);
            string op = cm.Groups[2].Success ? cm.Groups[2].Value : "=";
            string hint = CounterHints.TryGetValue(id, out var h) ? h : $"计数器 {id}";
            return $"{hint}（{op} {val}）";
        }
        if (key.StartsWith("rite."))
        {
            if (int.TryParse(key[5..], out var id))
            {
                string n = RiteNames.TryGetValue(id, out var rn) ? rn : $"仪式 {id}";
                return neg ? $"「{n}」未开启/未进行" : $"「{n}」正在进行";
            }
        }
        if (key.StartsWith("f:")) return $"数值关系：{key[2..]} {val}";
        return neg ? $"不满足：{key} = {val}" : $"{key} = {val}";
    }

    string HumanCard(string token)
    {
        if (int.TryParse(token, out var id) && CardNames.TryGetValue(id, out var n)) return $"「{n}」";
        return $"「{token}」";
    }

    void CollectLinks(JsonElement e, GuideNode node)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if ((p.Name == "event_on" || p.Name == "event") && p.Value.TryGetInt32(out var ev))
                    node.Links.Add(new GuideLink("进入事件", ev, NodeKind.Event));
                else if (p.Name == "rite" && p.Value.TryGetInt32(out var ri))
                    node.Links.Add(new GuideLink("开启仪式", ri, NodeKind.Rite));
                else CollectLinks(p.Value, node);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray()) CollectLinks(x, node);
        }
    }

    public IEnumerable<GuideNode> Search(string q)
    {
        q = q.Trim();
        if (q.Length == 0) return Nodes.Values.OrderBy(x => x.Id).Take(80);
        return Nodes.Values.Where(n => n.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || n.Id.ToString().Contains(q)).OrderBy(n => n.Kind).ThenBy(n => n.Id).Take(120);
    }
}
