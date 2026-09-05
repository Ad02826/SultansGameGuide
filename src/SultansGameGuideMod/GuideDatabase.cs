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
    public string LastError { get; private set; } = "";

    public void Load()
    {
        Nodes.Clear();
        CardNames.Clear();
        CounterHints.Clear();
        RiteNames.Clear();
        EventNames.Clear();
        LastError = "";

        ConfigRoot = Path.Combine(Application.streamingAssetsPath, "config");
        if (!Directory.Exists(ConfigRoot))
        {
            LastError = "找不到游戏配置目录：" + ConfigRoot;
            return;
        }

        LoadCards();
        ScanHumanHints();

        // 先载入 rite，使 event 条件里出现 rite.ID 时能直接显示仪式名字。
        LoadFolder("rite", NodeKind.Rite);
        LoadFolder("event", NodeKind.Event);
        LoadFolder("after_story", NodeKind.AfterStory);
    }

    void LoadCards()
    {
        var p = Path.Combine(ConfigRoot, "cards.json");
        if (!File.Exists(p)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(p), JsonOptions);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(prop.Name, out var id)) continue;
                if (prop.Value.TryGetProperty("name", out var n))
                    CardNames[id] = n.GetString() ?? id.ToString();
            }
        }
        catch (Exception ex)
        {
            LastError = "读取 cards.json 失败：" + ex.Message;
        }
    }

    void ScanHumanHints()
    {
        try
        {
            // 配置作者在 DSL 后面写了很多中文注释，例如：
            // "counter.7000572":1, //是否已经与正教决裂
            // 这里直接把注释作为 ID -> 人话语义字典。
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
                        if (int.TryParse(s, out var id) && !CounterHints.ContainsKey(id))
                            CounterHints[id] = CleanupComment(comment);
                    }
                }
            }
        }
        catch { }
    }

    static string CleanupComment(string s)
    {
        s = s.Trim();
        s = Regex.Replace(s, @"^[：:、\-–—\s]+", "");
        return s.Length > 80 ? s[..80] : s;
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

                if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
                    continue;

                string name = kind switch
                {
                    NodeKind.Event =>
                        root.TryGetProperty("text", out var t)
                            ? (t.GetString() ?? $"事件 {id}")
                            : $"事件 {id}",

                    _ =>
                        root.TryGetProperty("name", out var n)
                            ? (n.GetString() ?? $"{KindName(kind)} {id}")
                            : $"{KindName(kind)} {id}"
                };

                var node = new GuideNode
                {
                    Id = id,
                    Name = StripRichText(name),
                    Kind = kind,
                    SourcePath = p
                };

                if (root.TryGetProperty("condition", out var cond))
                    node.HumanCondition = HumanizeCondition(cond);

                if (kind == NodeKind.AfterStory)
                    LoadAfterStoryDetails(root, node);

                CollectLinks(root, node, null);

                // 去重，避免 settlement 中相同事件重复出现很多次。
                var distinct = node.Links
                    .GroupBy(x => (x.Label, x.TargetId, x.TargetKind))
                    .Select(g => g.First())
                    .ToList();
                node.Links.Clear();
                node.Links.AddRange(distinct);

                Nodes[id] = node;
                if (kind == NodeKind.Event) EventNames[id] = node.Name;
                if (kind == NodeKind.Rite) RiteNames[id] = node.Name;
            }
            catch { }
        }
    }

    static string KindName(NodeKind kind) => kind switch
    {
        NodeKind.Event => "事件",
        NodeKind.Rite => "仪式",
        NodeKind.AfterStory => "结局",
        _ => kind.ToString()
    };

    void LoadAfterStoryDetails(JsonElement root, GuideNode node)
    {
        if (!root.TryGetProperty("extra", out var extra) || extra.ValueKind != JsonValueKind.Array)
            return;

        var chunks = new List<string>();
        int i = 0;

        foreach (var e in extra.EnumerateArray())
        {
            i++;
            string cond = e.TryGetProperty("condition", out var c)
                ? HumanizeCondition(c)
                : "无特殊条件";

            string text = "";
            if (e.TryGetProperty("result_text", out var r))
                text = StripRichText(r.GetString() ?? "");

            chunks.Add($"【结局分支 {i}】\n条件：\n{cond}\n\n{text}");
        }

        node.ResultText = string.Join("\n\n", chunks);
    }

    public string HumanizeCondition(JsonElement e, int depth = 0)
    {
        if (e.ValueKind == JsonValueKind.Undefined || e.ValueKind == JsonValueKind.Null)
            return "无特殊条件";

        if (e.ValueKind != JsonValueKind.Object)
            return HumanizeLooseValue(e);

        var lines = new List<string>();

        foreach (var p in e.EnumerateObject())
        {
            if (p.Name is "any" or "all")
            {
                var inner = HumanizeGroup(p.Value);
                if (inner.Count == 0) continue;

                lines.Add(
                    (p.Name == "any" ? "满足以下任意一项：" : "同时满足以下条件：") +
                    "\n" +
                    string.Join("\n", inner.Select(x => "  · " + x.Replace("\n", "\n    ")))
                );
                continue;
            }

            lines.Add(HumanizeAtom(p.Name, p.Value));
        }

        return lines.Count == 0 ? "无特殊条件" : string.Join("\n", lines);
    }

    List<string> HumanizeGroup(JsonElement e)
    {
        var result = new List<string>();

        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
                result.Add(HumanizeAtom(p.Name, p.Value));
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.Object)
                    result.Add(HumanizeCondition(x));
                else
                    result.Add(HumanizeLooseValue(x));
            }
        }

        return result;
    }

    string HumanizeLooseValue(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.String => StripRichText(e.GetString() ?? ""),
            JsonValueKind.Number => e.ToString(),
            JsonValueKind.True => "是",
            JsonValueKind.False => "否",
            _ => e.ToString()
        };
    }

    string HumanizeAtom(string key, JsonElement value)
    {
        string val = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.ToString();

        bool neg = key.StartsWith("!");
        if (neg) key = key[1..];

        if (key.StartsWith("have."))
        {
            var tail = key[5..];
            var parts = tail.Split('.');
            string who = HumanCard(parts[0]);
            string tag = parts.Length > 1 ? "（标签：" + string.Join("/", parts.Skip(1)) + "）" : "";
            return neg ? $"不能拥有 / 角色不能存活：{who}{tag}" : $"拥有 / 角色仍存活：{who}{tag}";
        }

        if (key.StartsWith("table_have."))
        {
            var tail = key[11..];
            var parts = tail.Split('.');
            string who = HumanCard(parts[0]);
            string tag = parts.Length > 1 ? "（标签：" + string.Join("/", parts.Skip(1)) + "）" : "";
            return neg ? $"不能处于桌面闲置区：{who}{tag}" : $"当前可用 / 闲置：{who}{tag}";
        }

        var cm = Regex.Match(key, @"^counter\.(\d+)(>=|<=|>|<|=)?$");
        if (cm.Success)
        {
            int id = int.Parse(cm.Groups[1].Value);
            string op = cm.Groups[2].Success ? cm.Groups[2].Value : "=";
            string hint = CounterHints.TryGetValue(id, out var h) ? h : $"隐藏状态 #{id}";
            return HumanizeCounter(hint, op, val);
        }

        if (key.StartsWith("rite."))
        {
            if (int.TryParse(key[5..], out var id))
            {
                string n = RiteNames.TryGetValue(id, out var rn) ? rn : $"仪式 #{id}";
                return neg ? $"「{n}」当前没有进行" : $"「{n}」正在进行";
            }
        }

        if (key.StartsWith("f:"))
            return $"数值条件：{key[2..]} {val}";

        if (key.StartsWith("card."))
        {
            string who = HumanCard(key[5..]);
            return neg ? $"不能出现：{who}" : $"需要：{who}";
        }

        // 保留无法识别的 DSL，但把它放在人话条件的最后，不再让整个界面都是代码。
        return neg ? $"未满足内部条件：{key} = {val}" : $"内部条件：{key} = {val}";
    }

    static string HumanizeCounter(string hint, string op, string val)
    {
        if (val == "1")
        {
            if (op is ">=" or "=") return $"已满足：{hint}";
            if (op == "<") return $"尚未满足：{hint}";
        }

        if (val == "0")
        {
            if (op is "=" or "<=") return $"尚未满足：{hint}";
            if (op == ">") return $"已满足：{hint}";
        }

        string humanOp = op switch
        {
            ">=" => "至少",
            "<=" => "至多",
            ">" => "高于",
            "<" => "低于",
            "=" => "等于",
            _ => op
        };
        return $"{hint}：{humanOp} {val}";
    }

    string HumanCard(string token)
    {
        if (int.TryParse(token, out var id) && CardNames.TryGetValue(id, out var n))
            return $"「{StripRichText(n)}」";

        return $"「{token}」";
    }

    void CollectLinks(JsonElement e, GuideNode node, string? context)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            // 读取 option.items：tag -> 玩家真正看到的选项文本
            var optionLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (e.TryGetProperty("option", out var option) &&
                option.ValueKind == JsonValueKind.Object &&
                option.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("tag", out var tagEl)) continue;
                    if (!item.TryGetProperty("text", out var textEl)) continue;
                    string tag = tagEl.GetString() ?? "";
                    string text = StripRichText(textEl.GetString() ?? "");
                    if (tag.Length > 0 && text.Length > 0) optionLabels[tag] = text;
                }
            }

            foreach (var p in e.EnumerateObject())
            {
                string nextContext = context ?? "后续";

                if (p.Name == "success") nextContext = "成功 / 确认";
                else if (p.Name == "failed") nextContext = "失败 / 取消";
                else if (p.Name.StartsWith("case:", StringComparison.OrdinalIgnoreCase))
                {
                    string tag = p.Name[5..];
                    nextContext = optionLabels.TryGetValue(tag, out var choice)
                        ? $"选择「{choice}」"
                        : $"选择分支 {tag}";
                }

                if (p.Name == "event_on" || p.Name == "event")
                {
                    AddTargetValues(p.Value, node, nextContext, NodeKind.Event);
                    continue;
                }

                if (p.Name == "rite")
                {
                    AddTargetValues(p.Value, node, nextContext, NodeKind.Rite);
                    continue;
                }

                CollectLinks(p.Value, node, nextContext);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray())
                CollectLinks(x, node, context);
        }
    }

    static void AddTargetValues(JsonElement value, GuideNode node, string label, NodeKind kind)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id))
        {
            node.Links.Add(new GuideLink(label, id, kind));
            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in value.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var xid))
                    node.Links.Add(new GuideLink(label, xid, kind));
        }
    }

    public IEnumerable<GuideNode> Search(string q)
    {
        q = q.Trim();

        IEnumerable<GuideNode> seq = Nodes.Values;

        if (q.Length > 0)
        {
            seq = seq.Where(n =>
                n.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                n.Id.ToString().Contains(q) ||
                (n.ResultText?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return seq
            .OrderBy(n => n.Kind)
            .ThenBy(n => n.Id)
            .Take(300);
    }

    public GuideNode? Get(int id) => Nodes.TryGetValue(id, out var n) ? n : null;

    public string DisplayTarget(GuideLink link)
    {
        if (Nodes.TryGetValue(link.TargetId, out var n))
            return $"{KindName(n.Kind)} · {n.Name}";

        if (link.TargetKind == NodeKind.Event && EventNames.TryGetValue(link.TargetId, out var ev))
            return $"事件 · {ev}";

        if (link.TargetKind == NodeKind.Rite && RiteNames.TryGetValue(link.TargetId, out var ri))
            return $"仪式 · {ri}";

        return $"{KindName(link.TargetKind ?? NodeKind.Event)} #{link.TargetId}";
    }

    public static string StripRichText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = s.Replace("\\n", "\n");
        return s.Trim();
    }
}
