using System.Text.Json;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SultansGameGuide;

public sealed class GuideDatabase
{
    public readonly Dictionary<int, GuideNode> Nodes = new();

    public readonly Dictionary<int, string> CardNames = new();
    public readonly Dictionary<int, string> CardTypes = new();
    public readonly Dictionary<int, string> CardTitles = new();

    public readonly Dictionary<int, string> CounterHints = new();

    // 同一个 counter ID 在不同配置旁出现不同注释时，不把任何一条当成全局语义。
    // 这避免了早期“第一条注释污染其他剧情”的问题。
    private readonly HashSet<int> AmbiguousCounterHints = new();

    public readonly Dictionary<int, string> RiteNames = new();
    public readonly Dictionary<int, string> EventNames = new();

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public string ConfigRoot { get; private set; } = "";
    public string LastError { get; private set; } = "";

    private enum CondKind
    {
        Atom,
        All,
        Any
    }

    private sealed class CondNode
    {
        public CondKind Kind { get; init; }
        public string Key { get; init; } = "";
        public string Value { get; init; } = "";
        public List<CondNode> Children { get; } = new();
    }

    public void Load()
    {
        Nodes.Clear();

        CardNames.Clear();
        CardTypes.Clear();
        CardTitles.Clear();

        CounterHints.Clear();
        AmbiguousCounterHints.Clear();

        RiteNames.Clear();
        EventNames.Clear();

        LastError = "";

        ConfigRoot =
            Path.Combine(
                Application.streamingAssetsPath,
                "config"
            );

        if (!Directory.Exists(ConfigRoot))
        {
            LastError =
                "找不到游戏配置目录：" + ConfigRoot;

            return;
        }

        LoadCards();

        // 先读取策划写在 JSON 旁边的人类注释。
        // 这些注释非常重要：它们能告诉我们 counter 到底代表什么。
        ScanHumanHints();

        // 先载入仪式，后面事件引用 rite.ID 时可以直接显示名称。
        LoadFolder(
            "rite",
            NodeKind.Rite
        );

        LoadFolder(
            "event",
            NodeKind.Event
        );

        LoadFolder(
            "after_story",
            NodeKind.AfterStory
        );

        // 所有节点加载完成后，建立统一关系图：
        // 1. 谁会创建 / 开启我；
        // 2. 我会创建 / 开启谁。
        BuildRelationBranches();
    }

    private void LoadCards()
    {
        var path =
            Path.Combine(
                ConfigRoot,
                "cards.json"
            );

        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var doc =
                JsonDocument.Parse(
                    File.ReadAllText(path),
                    JsonOptions
                );

            foreach (
                var prop
                in
                doc.RootElement.EnumerateObject()
            )
            {
                if (
                    !int.TryParse(
                        prop.Name,
                        out var id
                    )
                )
                {
                    continue;
                }

                if (
                    prop.Value.TryGetProperty(
                        "name",
                        out var name
                    )
                )
                {
                    CardNames[id] =
                        StripRichText(
                            name.GetString()
                            ??
                            id.ToString()
                        );
                }

                if (
                    prop.Value.TryGetProperty(
                        "type",
                        out var type
                    )
                )
                {
                    CardTypes[id] =
                        type.GetString()
                        ??
                        "";
                }

                if (
                    prop.Value.TryGetProperty(
                        "title",
                        out var title
                    )
                )
                {
                    CardTitles[id] =
                        StripRichText(
                            title.GetString()
                            ??
                            ""
                        );
                }
            }
        }
        catch (Exception ex)
        {
            LastError =
                "读取 cards.json 失败："
                +
                ex.Message;
        }
    }

    private void ScanHumanHints()
    {
        try
        {
            foreach (
                var path
                in
                Directory.EnumerateFiles(
                    ConfigRoot,
                    "*.json",
                    SearchOption.AllDirectories
                )
            )
            {
                foreach (
                    var raw
                    in
                    File.ReadLines(path)
                )
                {
                    int commentIndex =
                        raw.IndexOf(
                            "//",
                            StringComparison.Ordinal
                        );

                    if (commentIndex < 0)
                    {
                        continue;
                    }

                    string code =
                        raw[..commentIndex];

                    string comment =
                        CleanupComment(
                            raw[
                                (commentIndex + 2)..
                            ]
                        );

                    if (comment.Length < 2)
                    {
                        continue;
                    }

                    // 普通机器键的行尾注释只属于其所在配置上下文。
                    // 不再跨文件建立 machineKey -> comment 全局映射。

                    foreach (
                        Match match
                        in
                        Regex.Matches(
                            code,
                            @"counter(?:\+)?\.(\d+)|counter\+(\d+)"
                        )
                    )
                    {
                        string? idText = null;

                        for (
                            int i = 1;
                            i < match.Groups.Count;
                            i++
                        )
                        {
                            if (
                                match.Groups[i]
                                    .Success
                            )
                            {
                                idText =
                                    match.Groups[i]
                                        .Value;

                                break;
                            }
                        }

                        if (
                            idText != null
                            &&
                            int.TryParse(
                                idText,
                                out var id
                            )
                        )
                        {
                            if (
                                CounterHints.TryGetValue(
                                    id,
                                    out var existingCounterHint
                                )
                            )
                            {
                                if (
                                    !string.Equals(
                                        CleanupComment(existingCounterHint),
                                        CleanupComment(comment),
                                        StringComparison.Ordinal
                                    )
                                )
                                {
                                    AmbiguousCounterHints.Add(id);
                                }
                            }
                            else
                            {
                                CounterHints[id] = comment;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // 注释属于辅助信息。
            // 即使读取失败，也不影响 MOD 主体加载。
        }
    }

    private static string CleanupComment(
        string text
    )
    {
        text =
            text.Trim();

        text =
            Regex.Replace(
                text,
                @"^[：:、\-–—\s]+",
                ""
            );

        if (text.Length > 100)
        {
            text =
                text[..100];
        }

        return text;
    }

    private void LoadFolder(
        string folder,
        NodeKind kind
    )
    {
        var dir =
            Path.Combine(
                ConfigRoot,
                folder
            );

        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (
            var path
            in
            Directory.EnumerateFiles(
                dir,
                "*.json"
            )
        )
        {
            try
            {
                using var doc =
                    JsonDocument.Parse(
                        File.ReadAllText(path),
                        JsonOptions
                    );

                var root =
                    doc.RootElement;

                if (
                    !root.TryGetProperty(
                        "id",
                        out var idElement
                    )
                    ||
                    !idElement.TryGetInt32(
                        out var id
                    )
                )
                {
                    continue;
                }

                string name =
                    kind switch
                    {
                        NodeKind.Event =>
                            root.TryGetProperty(
                                "text",
                                out var text
                            )
                                ?
                                (
                                    text.GetString()
                                    ??
                                    $"事件 {id}"
                                )
                                :
                                $"事件 {id}",

                        _ =>
                            root.TryGetProperty(
                                "name",
                                out var n
                            )
                                ?
                                (
                                    n.GetString()
                                    ??
                                    $"{KindName(kind)} {id}"
                                )
                                :
                                $"{KindName(kind)} {id}"
                    };

                var node =
                    new GuideNode
                    {
                        Id = id,
                        Name =
                            StripRichText(name),
                        Kind = kind,
                        SourcePath = path
                    };

                if (
                    root.TryGetProperty(
                        "condition",
                        out var condition
                    )
                )
                {
                    node.RawCondition =
                        condition.GetRawText();

                    node.HumanCondition =
                        HumanizeCondition(
                            condition
                        );
                }

                if (
                    root.TryGetProperty(
                        "on",
                        out var on
                    )
                )
                {
                    node.HumanTiming =
                        HumanizeTiming(
                            on
                        );
                }

                // 为“触发机制”建立精确的 action 级出边。
                // 这里会保留 settlement 局部分支自己的 condition，
                // 而不是只使用整个节点的顶层 condition。
                CollectOutgoingTriggers(
                    root,
                    node,
                    "",
                    node.HumanCondition,
                    node.RawCondition,
                    false
                );

                if (
                    kind
                    ==
                    NodeKind.AfterStory
                )
                {
                    LoadAfterStoryDetails(
                        root,
                        node
                    );
                }

                CollectLinks(
                    root,
                    node,
                    null
                );

                // 去重。
                var distinct =
                    node.Links
                        .GroupBy(
                            x =>
                                (
                                    x.Label,
                                    x.TargetId,
                                    x.TargetKind
                                )
                        )
                        .Select(
                            g =>
                                g.First()
                        )
                        .ToList();

                node.Links.Clear();
                node.Links.AddRange(
                    distinct
                );

                Nodes[id] =
                    node;

                if (
                    kind
                    ==
                    NodeKind.Event
                )
                {
                    EventNames[id] =
                        node.Name;
                }

                if (
                    kind
                    ==
                    NodeKind.Rite
                )
                {
                    RiteNames[id] =
                        node.Name;
                }
            }
            catch
            {
                // 单个配置损坏不应该让整个攻略数据库失效。
            }
        }
    }

    private static string KindName(
        NodeKind kind
    )
    {
        return kind switch
        {
            NodeKind.Event =>
                "事件",

            NodeKind.Rite =>
                "仪式",

            NodeKind.AfterStory =>
                "结局",

            _ =>
                kind.ToString()
        };
    }

    private void LoadAfterStoryDetails(
        JsonElement root,
        GuideNode node
    )
    {
        if (
            !root.TryGetProperty(
                "extra",
                out var extra
            )
            ||
            extra.ValueKind
            !=
            JsonValueKind.Array
        )
        {
            return;
        }

        var chunks =
            new List<string>();

        int index = 0;

        foreach (
            var element
            in
            extra.EnumerateArray()
        )
        {
            index++;

            string condition =
                element.TryGetProperty(
                    "condition",
                    out var c
                )
                    ?
                    HumanizeCondition(c)
                    :
                    "没有额外要求。";

            string resultText = "";

            if (
                element.TryGetProperty(
                    "result_text",
                    out var result
                )
            )
            {
                resultText =
                    StripRichText(
                        result.GetString()
                        ??
                        ""
                    );
            }

            var part =
                new List<string>();

            if (condition.Length > 0)
            {
                part.Add(
                    $"这条结局线要求：{condition}"
                );
            }

            if (resultText.Length > 0)
            {
                part.Add(
                    resultText
                );
            }

            chunks.Add(
                string.Join(
                    "\n",
                    part
                )
            );
        }

        node.ResultText =
            string.Join(
                "\n\n",
                chunks
            );
    }

    // ============================================================
    // 条件：先解析成逻辑树，再从逻辑树重新写成人话
    // ============================================================

    public string HumanizeConditionAtom(
        string key,
        JsonElement value
    )
    {
        return
            CleanupHumanText(
                RenderAtom(
                    new CondNode
                    {
                        Kind =
                            CondKind.Atom,
                        Key =
                            key,
                        Value =
                            ValueToString(
                                value
                            )
                    }
                )
            );
    }

    public string HumanizeCondition(
        JsonElement element
    )
    {
        var tree =
            ParseCondition(
                element
            );

        string result =
            RenderCondition(
                tree,
                true
            );

        result =
            CleanupHumanText(
                result
            );

        return string.IsNullOrWhiteSpace(
            result
        )
            ?
            "没有额外要求。"
            :
            result;
    }

    private CondNode ParseCondition(
        JsonElement element
    )
    {
        if (
            element.ValueKind
            !=
            JsonValueKind.Object
        )
        {
            return new CondNode
            {
                Kind = CondKind.Atom,
                Key = "__value__",
                Value =
                    ValueToString(
                        element
                    )
            };
        }

        var root =
            new CondNode
            {
                Kind = CondKind.All
            };

        foreach (
            var property
            in
            element.EnumerateObject()
        )
        {
            root.Children.Add(
                ParseProperty(
                    property
                )
            );
        }

        return root;
    }

    private CondNode ParseProperty(
        JsonProperty property
    )
    {
        if (
            property.Name.Equals(
                "any",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return ParseLogicGroup(
                property.Value,
                CondKind.Any
            );
        }

        if (
            property.Name.Equals(
                "all",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return ParseLogicGroup(
                property.Value,
                CondKind.All
            );
        }

        return new CondNode
        {
            Kind = CondKind.Atom,
            Key = property.Name,
            Value =
                ValueToString(
                    property.Value
                )
        };
    }

    private CondNode ParseLogicGroup(
        JsonElement element,
        CondKind kind
    )
    {
        var node =
            new CondNode
            {
                Kind = kind
            };

        if (
            element.ValueKind
            ==
            JsonValueKind.Object
        )
        {
            foreach (
                var property
                in
                element.EnumerateObject()
            )
            {
                node.Children.Add(
                    ParseProperty(
                        property
                    )
                );
            }
        }
        else if (
            element.ValueKind
            ==
            JsonValueKind.Array
        )
        {
            foreach (
                var item
                in
                element.EnumerateArray()
            )
            {
                if (
                    item.ValueKind
                    ==
                    JsonValueKind.Object
                )
                {
                    node.Children.Add(
                        ParseCondition(
                            item
                        )
                    );
                }
                else
                {
                    node.Children.Add(
                        new CondNode
                        {
                            Kind =
                                CondKind.Atom,
                            Key =
                                "__value__",
                            Value =
                                ValueToString(
                                    item
                                )
                        }
                    );
                }
            }
        }
        else
        {
            node.Children.Add(
                new CondNode
                {
                    Kind =
                        CondKind.Atom,
                    Key =
                        "__value__",
                    Value =
                        ValueToString(
                            element
                        )
                }
            );
        }

        return node;
    }

    private static string ValueToString(
        JsonElement element
    )
    {
        return element.ValueKind switch
        {
            JsonValueKind.String =>
                element.GetString()
                ??
                "",

            JsonValueKind.Number =>
                element.ToString(),

            JsonValueKind.True =>
                "1",

            JsonValueKind.False =>
                "0",

            _ =>
                element.GetRawText()
        };
    }

    private string RenderCondition(
        CondNode node,
        bool root
    )
    {
        if (
            node.Kind
            ==
            CondKind.Atom
        )
        {
            return RenderAtom(
                node
            );
        }

        // 特殊语义模式优先处理。
        // 例如：
        // any(
        //   all(!have.祭司, table_have.伊曼),
        //   table_have.祭司
        // )
        // 直接理解成“没有代班祭司时由伊曼处理；有代班祭司时祭司空闲即可”。
        if (
            node.Kind
            ==
            CondKind.Any
            &&
            TryRenderFallbackAvailability(
                node,
                out var fallbackText
            )
        )
        {
            return fallbackText;
        }

        // any(table_have.A, table_have.B, ...)
        // 不是把 four 条机器条件列出来，而是理解成“其中任意一个可用即可”。
        if (
            node.Kind
            ==
            CondKind.Any
            &&
            TryRenderAnyAvailableCards(
                node,
                out var availableText
            )
        )
        {
            return availableText;
        }

        var rendered =
            node.Children
                .Select(
                    child =>
                        RenderCondition(
                            child,
                            false
                        )
                )
                .Where(
                    text =>
                        !string.IsNullOrWhiteSpace(
                            text
                        )
                )
                .Distinct()
                .ToList();

        if (rendered.Count == 0)
        {
            return "";
        }

        if (rendered.Count == 1)
        {
            return rendered[0];
        }

        if (
            node.Kind
            ==
            CondKind.Any
        )
        {
            var lines =
                new List<string>
                {
                    $"有 {rendered.Count} 种满足方式，任选一种即可："
                };

            for (
                int i = 0;
                i < rendered.Count;
                i++
            )
            {
                lines.Add(
                    $"{ChineseNumber(i + 1)}、{MakeAlternativeSentence(rendered[i])}"
                );
            }

            return string.Join(
                "\n",
                lines
            );
        }

        // AND
        if (!root)
        {
            return JoinAsSentence(
                rendered
            );
        }

        var result =
            new List<string>
            {
                "需要同时满足以下条件："
            };

        foreach (
            string text
            in
            rendered
        )
        {
            result.Add(
                "• "
                +
                MakeBulletSentence(
                    text
                )
            );
        }

        return string.Join(
            "\n",
            result
        );
    }

    private bool TryRenderFallbackAvailability(
        CondNode node,
        out string text
    )
    {
        text = "";

        if (
            node.Children.Count
            !=
            2
        )
        {
            return false;
        }

        CondNode? all = null;
        CondNode? direct = null;

        foreach (
            var child
            in
            node.Children
        )
        {
            if (
                child.Kind
                ==
                CondKind.All
            )
            {
                all = child;
            }
            else if (
                child.Kind
                ==
                CondKind.Atom
                &&
                IsTableHave(
                    child,
                    out _
                )
            )
            {
                direct = child;
            }
        }

        if (
            all == null
            ||
            direct == null
        )
        {
            return false;
        }

        CondNode? missingCard =
            all.Children
                .FirstOrDefault(
                    child =>
                        IsNegativeHave(
                            child,
                            out _
                        )
                );

        CondNode? fallbackAvailable =
            all.Children
                .FirstOrDefault(
                    child =>
                        IsTableHave(
                            child,
                            out _
                        )
                );

        if (
            missingCard == null
            ||
            fallbackAvailable == null
        )
        {
            return false;
        }

        IsNegativeHave(
            missingCard,
            out int missingId
        );

        IsTableHave(
            fallbackAvailable,
            out int fallbackId
        );

        IsTableHave(
            direct,
            out int directId
        );

        if (
            missingId
            !=
            directId
        )
        {
            return false;
        }

        string directName =
            CardName(
                directId
            );

        string fallbackName =
            CardName(
                fallbackId
            );

        text =
            $"需要有人可以出面处理这件事："
            +
            $"如果当前没有{directName}，则{fallbackName}必须处于空闲状态；"
            +
            $"如果已经有{directName}，只要这名祭司空闲即可。";

        return true;
    }

    private bool TryRenderAnyAvailableCards(
        CondNode node,
        out string text
    )
    {
        text = "";

        if (
            node.Children.Count
            <
            2
        )
        {
            return false;
        }

        var ids =
            new List<int>();

        foreach (
            var child
            in
            node.Children
        )
        {
            if (
                !IsTableHave(
                    child,
                    out int id
                )
            )
            {
                return false;
            }

            ids.Add(id);
        }

        var names =
            ids
                .Select(
                    CardName
                )
                .ToList();

        string category =
            CommonCardCategory(
                ids
            );

        if (
            !string.IsNullOrWhiteSpace(
                category
            )
        )
        {
            text =
                $"以下{category}中任意一种当前可用即可：{JoinNames(names)}。";

            return true;
        }

        text =
            $"以下内容任意一个当前可用即可：{JoinNames(names)}。";

        return true;
    }

    private string RenderAtom(
        CondNode atom
    )
    {
        string key =
            atom.Key;

        string value =
            atom.Value;

        if (
            key
            ==
            "__value__"
        )
        {
            // 单独出现裸值时无法确定语义。明确标记为待适配，不伪造游戏机制。
            return
                "待适配条件：值="
                +
                value;
        }

        bool negated =
            key.StartsWith(
                "!"
            );

        if (negated)
        {
            key =
                key[1..];
        }

        // 游戏中还有 "rite":5002002 / "!rite":5002002 这种写法。
        // 仪式 ID 在 value 里，不能拿通用的 rite 字段去匹配别处注释。
        if (
            key.Equals(
                "rite",
                StringComparison.OrdinalIgnoreCase
            )
            &&
            int.TryParse(
                value,
                out int directRiteId
            )
        )
        {
            string directRiteName =
                RiteNames.TryGetValue(
                    directRiteId,
                    out var knownRiteName
                )
                    ?
                    $"《{knownRiteName}》"
                    :
                    "相关仪式";

            return negated
                ?
                $"当前没有正在进行的{directRiteName}"
                :
                $"{directRiteName}当前正在进行";
        }

        // hand_have 原版完全没有解析，先做玩家可理解的基础翻译。
        if (
            key.StartsWith(
                "hand_have."
            )
        )
        {
            string expression = key[10..];
            string subject = expression.Split('.')[0];
            string subjectName =
                int.TryParse(subject, out int handId)
                    ?
                    CardName(handId)
                    :
                    $"「{subject}」";

            return negated
                ?
                $"手牌中没有{subjectName}"
                :
                $"{subjectName}在手牌中";
        }

        if (
            key.StartsWith(
                "have."
            )
        )
        {
            string expression = key[5..];
            string[] parts = expression.Split('.');
            string subject = parts[0];

            string subjectName =
                int.TryParse(subject, out int id)
                    ?
                    CardName(id)
                    :
                    $"「{subject}」";

            if (parts.Length > 1)
            {
                string tag = string.Join(".", parts.Skip(1));

                return negated
                    ?
                    $"{subjectName}不能满足「{tag}」这一状态要求"
                    :
                    $"当前有{subjectName}，并具有「{tag}」状态";
            }

            if (
                int.TryParse(
                    subject,
                    out int numericId
                )
            )
            {
                bool character =
                    IsCharacter(numericId);

                if (negated)
                {
                    return character
                        ?
                        $"当前没有{subjectName}"
                        :
                        $"还没有{subjectName}";
                }

                return character
                    ?
                    $"{subjectName}仍在"
                    :
                    $"已经拥有{subjectName}";
            }

            return negated
                ?
                $"当前没有{subjectName}"
                :
                $"当前有{subjectName}";
        }

        if (
            key.StartsWith(
                "table_have."
            )
        )
        {
            string expression = key[11..];
            string[] parts = expression.Split('.');
            string subject = parts[0];

            string subjectName =
                int.TryParse(subject, out int id)
                    ?
                    CardName(id)
                    :
                    $"「{subject}」";

            if (parts.Length > 1)
            {
                string tag = string.Join(".", parts.Skip(1));

                return negated
                    ?
                    $"{subjectName}当前不能以「{tag}」状态使用"
                    :
                    $"{subjectName}当前空闲，并具有「{tag}」状态";
            }

            if (negated)
            {
                return $"{subjectName}当前不能使用";
            }

            return
                int.TryParse(subject, out int numericId)
                &&
                IsCharacter(numericId)
                    ?
                    $"{subjectName}当前空闲，可以出面"
                    :
                    $"{subjectName}当前没有被占用，可以使用";
        }

        var counterMatch =
            Regex.Match(
                key,
                @"^counter\.(\d+)(>=|<=|>|<|=)?$"
            );

        if (counterMatch.Success)
        {
            int id =
                int.Parse(
                    counterMatch
                        .Groups[1]
                        .Value
                );

            string op =
                counterMatch
                    .Groups[2]
                    .Success
                        ?
                        counterMatch
                            .Groups[2]
                            .Value
                        :
                        "=";

            string hint =
                !AmbiguousCounterHints.Contains(id)
                &&
                CounterHints.TryGetValue(
                    id,
                    out var humanHint
                )
                    ?
                    humanHint
                    :
                    "";

            return HumanizeCounter(
                id,
                hint,
                op,
                value
            );
        }

        if (
            key.StartsWith(
                "rite."
            )
        )
        {
            if (
                int.TryParse(
                    key[5..],
                    out int id
                )
            )
            {
                string name =
                    RiteNames.TryGetValue(
                        id,
                        out var riteName
                    )
                        ?
                        $"《{riteName}》"
                        :
                        "相关仪式";

                return negated
                    ?
                    $"{name}当前没有进行"
                    :
                    $"{name}正在进行";
            }
        }

        if (
            key.StartsWith(
                "card."
            )
        )
        {
            string token =
                key[5..]
                    .Split('.')[0];

            if (
                int.TryParse(
                    token,
                    out int id
                )
            )
            {
                string name =
                    CardName(id);

                return negated
                    ?
                    $"不能有{name}"
                    :
                    $"需要{name}";
            }
        }

        // 未识别字段必须显式暴露为待适配条件；不再借用其他文件的注释猜语义。
        return
            "待适配条件："
            +
            atom.Key
            +
            "="
            +
            value;
    }

    private string HumanizeCounter(
        int id,
        string hint,
        string op,
        string value
    )
    {
        hint =
            NormalizeCounterHint(
                hint
            );

        bool trueLike =
            value
            ==
            "1";

        bool falseLike =
            value
            ==
            "0";

        if (
            hint.Length == 0
        )
        {
            return
                $"待适配计数条件：counter.{id}{op}{value}";
        }

        bool hintAlreadyNegative =
            ContainsNegativeMeaning(
                hint
            );

        if (
            trueLike
            &&
            (
                op == ">="
                ||
                op == "="
            )
        )
        {
            if (hintAlreadyNegative)
            {
                return hint;
            }

            if (
                hint.StartsWith("已经")
                ||
                hint.StartsWith("已")
            )
            {
                return hint;
            }

            if (
                hint.StartsWith("和")
            )
            {
                return
                    "已经与"
                    +
                    hint[1..];
            }

            if (
                hint.StartsWith("与")
            )
            {
                return
                    "已经"
                    +
                    hint;
            }

            return
                "已经"
                +
                AddVerbFriendlyPrefix(
                    hint
                );
        }

        if (
            trueLike
            &&
            op == "<"
        )
        {
            if (hintAlreadyNegative)
            {
                return
                    NormalizeNegativePhrase(
                        hint
                    );
            }

            string positive =
                RemoveCompletedPrefixes(
                    hint
                );

            return
                "还没有"
                +
                AddVerbFriendlyPrefix(
                    positive
                );
        }

        if (
            falseLike
            &&
            (
                op == "="
                ||
                op == "<="
            )
        )
        {
            if (hintAlreadyNegative)
            {
                return
                    NormalizeNegativePhrase(
                        hint
                    );
            }

            return
                "还没有"
                +
                AddVerbFriendlyPrefix(
                    RemoveCompletedPrefixes(
                        hint
                    )
                );
        }

        string opText =
            op switch
            {
                ">=" =>
                    "至少达到",

                "<=" =>
                    "不能超过",

                ">" =>
                    "需要高于",

                "<" =>
                    "需要低于",

                "=" =>
                    "需要达到",

                _ =>
                    "需要满足"
            };

        return
            $"{hint}（{opText} {value}）";
    }

    private static string NormalizeCounterHint(
        string hint
    )
    {
        hint =
            hint.Trim();

        hint =
            Regex.Replace(
                hint,
                @"^是否(已经|已)?",
                ""
            );

        hint =
            Regex.Replace(
                hint,
                @"^判断",
                ""
            );

        hint =
            hint.Trim(
                '：',
                ':',
                ' ',
                '。'
            );

        return hint;
    }

    private static bool ContainsNegativeMeaning(
        string text
    )
    {
        return
            text.Contains("还没")
            ||
            text.Contains("尚未")
            ||
            text.Contains("没有")
            ||
            text.StartsWith("未");
    }

    private static string NormalizeNegativePhrase(
        string text
    )
    {
        text =
            text.Replace(
                "还没",
                "还没有"
            );

        text =
            text.Replace(
                "尚未",
                "还没有"
            );

        return text;
    }

    private static string RemoveCompletedPrefixes(
        string text
    )
    {
        if (
            text.StartsWith("已经")
        )
        {
            return
                text[2..];
        }

        if (
            text.StartsWith("已")
        )
        {
            return
                text[1..];
        }

        return text;
    }

    private static string AddVerbFriendlyPrefix(
        string text
    )
    {
        // 中文这里不强行加“完成/触发”，避免：
        // “已经完成和正教决裂”这种生硬表达。
        return text;
    }

    private bool IsNegativeHave(
        CondNode node,
        out int id
    )
    {
        id = 0;

        if (
            node.Kind
            !=
            CondKind.Atom
        )
        {
            return false;
        }

        if (
            !node.Key.StartsWith(
                "!have."
            )
        )
        {
            return false;
        }

        string token =
            node.Key[6..]
                .Split('.')[0];

        return int.TryParse(
            token,
            out id
        );
    }

    private bool IsTableHave(
        CondNode node,
        out int id
    )
    {
        id = 0;

        if (
            node.Kind
            !=
            CondKind.Atom
        )
        {
            return false;
        }

        string key =
            node.Key.TrimStart('!');

        if (
            !key.StartsWith(
                "table_have."
            )
        )
        {
            return false;
        }

        string token =
            key[11..]
                .Split('.')[0];

        return int.TryParse(
            token,
            out id
        );
    }

    private string CardName(
        int id
    )
    {
        if (
            CardNames.TryGetValue(
                id,
                out var name
            )
        )
        {
            return
                $"「{name}」";
        }

        return "相关人物或卡牌";
    }

    private bool IsCharacter(
        int id
    )
    {
        return
            CardTypes.TryGetValue(
                id,
                out var type
            )
            &&
            type.Equals(
                "char",
                StringComparison.OrdinalIgnoreCase
            );
    }

    private string CommonCardCategory(
        List<int> ids
    )
    {
        var titles =
            ids
                .Select(
                    id =>
                        CardTitles.TryGetValue(
                            id,
                            out var title
                        )
                            ?
                            title
                            :
                            ""
                )
                .Where(
                    title =>
                        !string.IsNullOrWhiteSpace(
                            title
                        )
                )
                .Distinct()
                .ToList();

        if (
            titles.Count == 1
        )
        {
            string title =
                titles[0];

            if (
                title.Length
                <=
                10
            )
            {
                return
                    $"「{title}」";
            }
        }

        return "";
    }

    private static string JoinNames(
        List<string> names
    )
    {
        if (
            names.Count == 0
        )
        {
            return "";
        }

        if (
            names.Count == 1
        )
        {
            return names[0];
        }

        return
            string.Join(
                "、",
                names
            );
    }

    private static string JoinAsSentence(
        List<string> parts
    )
    {
        if (
            parts.Count == 0
        )
        {
            return "";
        }

        if (
            parts.Count == 1
        )
        {
            return
                MakeBulletSentence(
                    parts[0]
                );
        }

        if (
            parts.Count == 2
        )
        {
            return
                $"{TrimPunctuation(parts[0])}，同时{TrimPunctuation(parts[1])}。";
        }

        return
            string.Join(
                "；",
                parts.Select(
                    TrimPunctuation
                )
            )
            +
            "。";
    }

    private static string MakeAlternativeSentence(
        string text
    )
    {
        text =
            text.Trim();

        if (
            text.StartsWith(
                "需要同时满足以下条件："
            )
        )
        {
            text =
                text.Replace(
                    "需要同时满足以下条件：",
                    ""
                );

            text =
                text.Replace(
                    "\n• ",
                    "；"
                );

            text =
                text.Trim(
                    '；',
                    ' ',
                    '\n'
                );
        }

        return
            MakeBulletSentence(
                text
            );
    }

    private static string MakeBulletSentence(
        string text
    )
    {
        text =
            text.Trim();

        if (
            text.EndsWith("。")
            ||
            text.EndsWith("；")
            ||
            text.EndsWith("！")
            ||
            text.EndsWith("？")
        )
        {
            return text;
        }

        return
            text
            +
            "。";
    }

    private static string TrimPunctuation(
        string text
    )
    {
        return
            text.Trim()
                .TrimEnd(
                    '。',
                    '；',
                    ';'
                );
    }

    private static string CleanupHumanText(
        string text
    )
    {
        text =
            text.Replace(
                "。。",
                "。"
            );

        text =
            Regex.Replace(
                text,
                @"\n{3,}",
                "\n\n"
            );

        return
            text.Trim();
    }

    private static string ChineseNumber(
        int number
    )
    {
        return number switch
        {
            1 => "一",
            2 => "二",
            3 => "三",
            4 => "四",
            5 => "五",
            6 => "六",
            7 => "七",
            8 => "八",
            9 => "九",
            _ => number.ToString()
        };
    }


    // ============================================================
    // 触发机制：检查阶段 + action 反向索引
    // ============================================================

    private string HumanizeTiming(
        JsonElement on
    )
    {
        if (
            on.ValueKind
            !=
            JsonValueKind.Object
        )
        {
            return "";
        }

        var lines =
            new List<string>();

        foreach (
            var property
            in
            on.EnumerateObject()
        )
        {
            string key =
                property.Name;

            string value =
                ValueToString(
                    property.Value
                );

            switch (key)
            {
                case "round_begin_ba":
                    lines.Add(
                        DescribeRoundBeginTiming(
                            property.Value
                        )
                    );
                    break;

                case "rite_end":
                    lines.Add(
                        $"指定仪式结束后：当{DescribeRiteTimingTarget(property.Value)}完成结算并关闭时，立即检查本事件。"
                    );
                    break;

                case "rite_start":
                    lines.Add(
                        $"指定仪式开始时：当{DescribeRiteTimingTarget(property.Value)}正式开始执行时，立即检查本事件。"
                    );
                    break;

                case "card_clean":
                    lines.Add(
                        $"卡牌被移除时：当{DescribeCardTimingTarget(property.Value)}从当前局面中被清除时，立即检查本事件。"
                    );
                    break;

                case "card_born":
                    lines.Add(
                        $"卡牌出现时：当{DescribeCardTimingTarget(property.Value)}被生成 / 获得时，立即检查本事件。"
                    );
                    break;

                case "counter":
                    lines.Add(
                        $"计数器变化时：当计数器 {value} 发生更新时，事件系统会重新检查本事件的条件。"
                    );
                    break;

                case "game_end":
                    lines.Add(
                        "一局游戏结束阶段：在本局进入结束 / 结算流程时检查。"
                    );
                    break;

                case "close_wizard":
                    lines.Add(
                        "关闭引导 / 向导界面后：对应的引导流程关闭时立即检查。"
                    );
                    break;

                case "close_prompt":
                    lines.Add(
                        "关闭提示窗口后：对应剧情提示框关闭时立即检查。"
                    );
                    break;

                case "open_card_info":
                    lines.Add(
                        "打开卡牌详情时：玩家打开对应卡牌的信息界面时检查。"
                    );
                    break;

                case "open_rite":
                    lines.Add(
                        $"打开仪式界面时：玩家打开{DescribeRiteTimingTarget(property.Value)}时立即检查。"
                    );
                    break;

                default:
                    lines.Add(
                        $"特殊检查阶段「{key}」：游戏在该阶段发生时检查（配置值：{value}）。"
                    );
                    break;
            }
        }

        return
            string.Join(
                "\n",
                lines.Where(
                    x =>
                        !string.IsNullOrWhiteSpace(
                            x
                        )
                )
            );
    }

    private static string DescribeRoundBeginTiming(
        JsonElement value
    )
    {
        if (
            value.ValueKind
            ==
            JsonValueKind.Number
            &&
            value.TryGetInt32(
                out var n
            )
        )
        {
            if (n <= 1)
            {
                return
                    "每回合开始时：进入新回合的刷新 / 结算起始阶段后检查一次。";
            }

            return
                $"回合开始阶段：事件被启用后，按约每 {n} 回合一次的节奏在回合开始时检查。";
        }

        if (
            value.ValueKind
            ==
            JsonValueKind.Array
        )
        {
            var numbers =
                value.EnumerateArray()
                    .Where(
                        x =>
                            x.ValueKind
                            ==
                            JsonValueKind.Number
                    )
                    .Select(
                        x =>
                            x.TryGetInt32(
                                out var n
                            )
                                ?
                                n
                                :
                                0
                    )
                    .Where(
                        x =>
                            x > 0
                    )
                    .ToList();

            if (
                numbers.Count
                >=
                2
            )
            {
                return
                    $"回合开始阶段：每隔 {numbers[0]}～{numbers[1]} 回合（该区间由游戏决定具体间隔）在回合开始时检查。";
            }
        }

        return
            "回合开始阶段：进入新回合的刷新 / 结算起始阶段时检查。";
    }

    private string DescribeRiteTimingTarget(
        JsonElement value
    )
    {
        var ids =
            ExtractIntValues(
                value
            );

        if (
            ids.Count
            ==
            0
        )
        {
            return
                "对应仪式";
        }

        return
            string.Join(
                "、",
                ids.Select(
                    id =>
                        RiteNames.TryGetValue(
                            id,
                            out var name
                        )
                            ?
                            $"《{name}》"
                            :
                            $"仪式 {id}"
                )
            );
    }

    private string DescribeCardTimingTarget(
        JsonElement value
    )
    {
        var ids =
            ExtractIntValues(
                value
            );

        if (
            ids.Count
            ==
            0
        )
        {
            return
                "对应卡牌";
        }

        return
            string.Join(
                "、",
                ids.Select(
                    id =>
                        CardNames.TryGetValue(
                            id,
                            out var name
                        )
                            ?
                            $"「{name}」"
                            :
                            $"卡牌 {id}"
                )
            );
    }

    private static List<int> ExtractIntValues(
        JsonElement value
    )
    {
        var result =
            new List<int>();

        if (
            value.ValueKind
            ==
            JsonValueKind.Number
            &&
            value.TryGetInt32(
                out var id
            )
        )
        {
            result.Add(id);

            return result;
        }

        if (
            value.ValueKind
            ==
            JsonValueKind.Array
        )
        {
            foreach (
                var item
                in
                value.EnumerateArray()
            )
            {
                if (
                    item.ValueKind
                    ==
                    JsonValueKind.Number
                    &&
                    item.TryGetInt32(
                        out var itemId
                    )
                )
                {
                    result.Add(
                        itemId
                    );
                }
            }
        }

        return result;
    }

    private void CollectOutgoingTriggers(
        JsonElement element,
        GuideNode node,
        string context,
        string inheritedHumanCondition,
        string inheritedRawCondition,
        bool insideAction
    )
    {
        if (
            element.ValueKind
            ==
            JsonValueKind.Object
        )
        {
            string humanCondition =
                inheritedHumanCondition;

            string rawCondition =
                inheritedRawCondition;

            if (
                element.TryGetProperty(
                    "condition",
                    out var localCondition
                )
                &&
                localCondition.ValueKind
                ==
                JsonValueKind.Object
            )
            {
                rawCondition =
                    localCondition.GetRawText();

                humanCondition =
                    HumanizeCondition(
                        localCondition
                    );
            }

            string localContext =
                context;

            if (
                string.IsNullOrWhiteSpace(
                    localContext
                )
                &&
                element.TryGetProperty(
                    "result_title",
                    out var resultTitle
                )
                &&
                resultTitle.ValueKind
                ==
                JsonValueKind.String
            )
            {
                string title =
                    StripRichText(
                        resultTitle.GetString()
                        ??
                        ""
                    );

                if (
                    !string.IsNullOrWhiteSpace(
                        title
                    )
                )
                {
                    localContext =
                        title;
                }
            }

            var optionLabels =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase
                );

            if (
                element.TryGetProperty(
                    "option",
                    out var option
                )
                &&
                option.ValueKind
                ==
                JsonValueKind.Object
                &&
                option.TryGetProperty(
                    "items",
                    out var items
                )
                &&
                items.ValueKind
                ==
                JsonValueKind.Array
            )
            {
                foreach (
                    var item
                    in
                    items.EnumerateArray()
                )
                {
                    if (
                        item.ValueKind
                        !=
                        JsonValueKind.Object
                        ||
                        !item.TryGetProperty(
                            "tag",
                            out var tagElement
                        )
                        ||
                        !item.TryGetProperty(
                            "text",
                            out var textElement
                        )
                    )
                    {
                        continue;
                    }

                    string tag =
                        tagElement.GetString()
                        ??
                        "";

                    string label =
                        StripRichText(
                            textElement.GetString()
                            ??
                            ""
                        );

                    if (
                        tag.Length > 0
                        &&
                        label.Length > 0
                    )
                    {
                        optionLabels[tag] =
                            label;
                    }
                }
            }

            foreach (
                var property
                in
                element.EnumerateObject()
            )
            {
                if (
                    property.Name
                    ==
                    "condition"
                )
                {
                    continue;
                }

                string nextContext =
                    localContext;

                if (
                    property.Name
                    ==
                    "success"
                )
                {
                    nextContext =
                        "成功后";
                }
                else if (
                    property.Name
                    ==
                    "failed"
                )
                {
                    nextContext =
                        "失败后";
                }
                else if (
                    property.Name.StartsWith(
                        "case:",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    string tag =
                        property.Name[5..];

                    nextContext =
                        optionLabels.TryGetValue(
                            tag,
                            out var choice
                        )
                            ?
                            $"选择「{choice}」"
                            :
                            (
                                string.IsNullOrWhiteSpace(
                                    localContext
                                )
                                    ?
                                    "做出对应选择"
                                    :
                                    localContext
                            );
                }

                bool nextInsideAction =
                    insideAction
                    ||
                    property.Name
                    ==
                    "action";

                // rite 只有出现在 action 内才表示“创建 / 开启仪式”。
                // condition 里的 rite / !rite 只是状态判断，绝不能当作剧情出边。
                if (
                    property.Name
                    ==
                    "rite"
                    &&
                    insideAction
                )
                {
                    AddOutgoingTriggerValues(
                        property.Value,
                        node,
                        nextContext,
                        NodeKind.Rite,
                        "rite",
                        humanCondition,
                        rawCondition
                    );

                    continue;
                }

                // event_on 在 action 中最常见，也有少量配置放在 result 中，
                // 语义都是“开启后续事件”，所以只要不在 condition 中就保留。
                if (
                    property.Name
                    ==
                    "event_on"
                )
                {
                    AddOutgoingTriggerValues(
                        property.Value,
                        node,
                        nextContext,
                        NodeKind.Event,
                        "event_on",
                        humanCondition,
                        rawCondition
                    );

                    continue;
                }

                // event 表示直接进入 / 跳转到另一个事件。
                if (
                    property.Name
                    ==
                    "event"
                )
                {
                    AddOutgoingTriggerValues(
                        property.Value,
                        node,
                        nextContext,
                        NodeKind.Event,
                        "event",
                        humanCondition,
                        rawCondition
                    );

                    continue;
                }

                CollectOutgoingTriggers(
                    property.Value,
                    node,
                    nextContext,
                    humanCondition,
                    rawCondition,
                    nextInsideAction
                );
            }
        }
        else if (
            element.ValueKind
            ==
            JsonValueKind.Array
        )
        {
            foreach (
                var item
                in
                element.EnumerateArray()
            )
            {
                CollectOutgoingTriggers(
                    item,
                    node,
                    context,
                    inheritedHumanCondition,
                    inheritedRawCondition,
                    insideAction
                );
            }
        }
    }

    private static void AddOutgoingTriggerValues(
        JsonElement value,
        GuideNode node,
        string label,
        NodeKind kind,
        string relationType,
        string humanCondition,
        string rawCondition
    )
    {
        if (
            value.ValueKind
            ==
            JsonValueKind.Number
            &&
            value.TryGetInt32(
                out var id
            )
        )
        {
            node.OutgoingTriggers.Add(
                new GuideOutgoingTrigger
                {
                    Label =
                        label,
                    TargetId =
                        id,
                    TargetKind =
                        kind,
                    RelationType =
                        relationType,
                    HumanCondition =
                        string.IsNullOrWhiteSpace(
                            humanCondition
                        )
                            ?
                            "没有额外要求。"
                            :
                            humanCondition,
                    RawCondition =
                        rawCondition
                }
            );

            return;
        }

        if (
            value.ValueKind
            ==
            JsonValueKind.Array
        )
        {
            foreach (
                var item
                in
                value.EnumerateArray()
            )
            {
                if (
                    item.ValueKind
                    ==
                    JsonValueKind.Number
                    &&
                    item.TryGetInt32(
                        out var itemId
                    )
                )
                {
                    node.OutgoingTriggers.Add(
                        new GuideOutgoingTrigger
                        {
                            Label =
                                label,
                            TargetId =
                                itemId,
                            TargetKind =
                                kind,
                            RelationType =
                                relationType,
                            HumanCondition =
                                string.IsNullOrWhiteSpace(
                                    humanCondition
                                )
                                    ?
                                    "没有额外要求。"
                                    :
                                    humanCondition,
                            RawCondition =
                                rawCondition
                        }
                    );
                }
            }
        }
    }

    private void BuildRelationBranches()
    {
        foreach (
            var node
            in
            Nodes.Values
        )
        {
            node.IncomingRelations.Clear();
            node.OutgoingRelations.Clear();

        }

        foreach (
            var source
            in
            Nodes.Values
        )
        {
            foreach (
                var edge
                in
                source.OutgoingTriggers
            )
            {
                if (
                    !Nodes.TryGetValue(
                        edge.TargetId,
                        out var target
                    )
                    ||
                    target.Kind
                    !=
                    edge.TargetKind
                )
                {
                    continue;
                }

                // 当前节点 -> 目标节点
                var outgoingBranch =
                    source.OutgoingRelations.FirstOrDefault(
                        x =>
                            x.NodeId
                            ==
                            target.Id
                            &&
                            x.NodeKind
                            ==
                            target.Kind
                    );

                if (
                    outgoingBranch
                    ==
                    null
                )
                {
                    outgoingBranch =
                        new GuideRelationBranch
                        {
                            NodeId =
                                target.Id,
                            NodeKind =
                                target.Kind,
                            NodeName =
                                target.Name
                        };

                    source.OutgoingRelations.Add(
                        outgoingBranch
                    );
                }

                AddRelationPath(
                    outgoingBranch,
                    source,
                    target,
                    edge
                );

                // 来源节点 -> 当前节点
                // A -> A 属于自身续存 / 自循环，只保留在“走向”中，
                // 不能反向显示成 A 的外部触发来源。
                if (
                    source.Id
                    ==
                    target.Id
                    &&
                    source.Kind
                    ==
                    target.Kind
                )
                {
                    continue;
                }

                var incomingBranch =
                    target.IncomingRelations.FirstOrDefault(
                        x =>
                            x.NodeId
                            ==
                            source.Id
                            &&
                            x.NodeKind
                            ==
                            source.Kind
                    );

                if (
                    incomingBranch
                    ==
                    null
                )
                {
                    incomingBranch =
                        new GuideRelationBranch
                        {
                            NodeId =
                                source.Id,
                            NodeKind =
                                source.Kind,
                            NodeName =
                                source.Name
                        };

                    target.IncomingRelations.Add(
                        incomingBranch
                    );
                }

                AddRelationPath(
                    incomingBranch,
                    source,
                    target,
                    edge
                );
            }
        }

        foreach (
            var node
            in
            Nodes.Values
        )
        {
            SortRelationBranches(
                node.IncomingRelations
            );

            SortRelationBranches(
                node.OutgoingRelations
            );
        }
    }

    private void AddRelationPath(
        GuideRelationBranch branch,
        GuideNode source,
        GuideNode target,
        GuideOutgoingTrigger edge
    )
    {
        string actionText =
            edge.RelationType switch
            {
                "rite" =>
                    $"生成《{target.Name}》。",

                "event" =>
                    $"进入事件「{target.Name}」。",

                _ =>
                    $"开启事件「{target.Name}」。"
            };

        if (
            !string.IsNullOrWhiteSpace(
                edge.Label
            )
        )
        {
            actionText =
                $"{edge.Label}：{actionText}";
        }

        var path =
            new GuideRelationPath
            {
                Context =
                    edge.Label,
                Timing =
                    BuildSourceExecutionTiming(
                        source
                    ),
                HumanCondition =
                    edge.HumanCondition,
                RawCondition =
                    edge.RawCondition,
                ActionText =
                    actionText,
                RelationType =
                    edge.RelationType
            };

        bool exists =
            branch.Paths.Any(
                x =>
                    x.Context
                    ==
                    path.Context
                    &&
                    x.RawCondition
                    ==
                    path.RawCondition
                    &&
                    x.RelationType
                    ==
                    path.RelationType
                    &&
                    x.ActionText
                    ==
                    path.ActionText
            );

        if (
            !exists
        )
        {
            branch.Paths.Add(
                path
            );
        }
    }

    private static void SortRelationBranches(
        List<GuideRelationBranch> branches
    )
    {
        var ordered =
            branches
                .OrderBy(
                    x =>
                        x.NodeKind
                )
                .ThenBy(
                    x =>
                        x.NodeName,
                    StringComparer.Ordinal
                )
                .ThenBy(
                    x =>
                        x.NodeId
                )
                .ToList();

        branches.Clear();
        branches.AddRange(
            ordered
        );
    }

    private string BuildEventTimingDescription(
        GuideNode node
    )
    {
        if (
            !string.IsNullOrWhiteSpace(
                node.HumanTiming
            )
        )
        {
            return
                node.HumanTiming;
        }

        return
            "事件被启用后，等待游戏对应的剧情检查点；该配置没有单独写出可展示的 on 检查阶段。";
    }

    private string BuildSourceExecutionTiming(
        GuideNode source
    )
    {
        if (
            source.Kind
            ==
            NodeKind.Event
        )
        {
            return
                BuildEventTimingDescription(
                    source
                );
        }

        if (
            source.Kind
            ==
            NodeKind.Rite
        )
        {
            return
                $"在《{source.Name}》的仪式结算阶段：当玩家完成对应选择 / 判定并进入该分支时执行。";
        }

        return
            "在对应剧情分支结算时执行。";
    }

    private static string DisplayNode(
        GuideNode node
    )
    {
        return
            node.Kind
            ==
            NodeKind.Rite
                ?
                $"《{node.Name}》"
                :
                $"「{node.Name}」";
    }

    // ============================================================
    // 后续事件 / 仪式：翻译成“接下来会怎样”
    // ============================================================

    private void CollectLinks(
        JsonElement element,
        GuideNode node,
        string? context,
        bool insideAction = false
    )
    {
        if (
            element.ValueKind
            ==
            JsonValueKind.Object
        )
        {
            var optionLabels =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase
                );

            if (
                element.TryGetProperty(
                    "option",
                    out var option
                )
                &&
                option.ValueKind
                ==
                JsonValueKind.Object
                &&
                option.TryGetProperty(
                    "items",
                    out var items
                )
                &&
                items.ValueKind
                ==
                JsonValueKind.Array
            )
            {
                foreach (
                    var item
                    in
                    items.EnumerateArray()
                )
                {
                    if (
                        item.ValueKind
                        !=
                        JsonValueKind.Object
                    )
                    {
                        continue;
                    }

                    if (
                        !item.TryGetProperty(
                            "tag",
                            out var tagElement
                        )
                        ||
                        !item.TryGetProperty(
                            "text",
                            out var textElement
                        )
                    )
                    {
                        continue;
                    }

                    string tag =
                        tagElement.GetString()
                        ??
                        "";

                    string text =
                        StripRichText(
                            textElement.GetString()
                            ??
                            ""
                        );

                    if (
                        tag.Length > 0
                        &&
                        text.Length > 0
                    )
                    {
                        optionLabels[tag] =
                            text;
                    }
                }
            }

            foreach (
                var property
                in
                element.EnumerateObject()
            )
            {
                string nextContext =
                    context
                    ??
                    "";

                if (
                    property.Name
                    ==
                    "success"
                )
                {
                    nextContext =
                        "成功后";
                }
                else if (
                    property.Name
                    ==
                    "failed"
                )
                {
                    nextContext =
                        "失败后";
                }
                else if (
                    property.Name.StartsWith(
                        "case:",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    string tag =
                        property.Name[5..];

                    nextContext =
                        optionLabels.TryGetValue(
                            tag,
                            out var choice
                        )
                            ?
                            $"选择「{choice}」后"
                            :
                            "做出这个选择后";
                }

                if (
                    property.Name
                    ==
                    "condition"
                )
                {
                    // condition 里的 rite / !rite 只是状态判断，
                    // 不能当成“后续开启某仪式”。
                    continue;
                }

                bool nextInsideAction =
                    insideAction
                    ||
                    property.Name
                    ==
                    "action";

                if (
                    property.Name
                    ==
                    "event_on"
                    ||
                    property.Name
                    ==
                    "event"
                )
                {
                    AddTargetValues(
                        property.Value,
                        node,
                        nextContext,
                        NodeKind.Event
                    );

                    continue;
                }

                if (
                    property.Name
                    ==
                    "rite"
                    &&
                    insideAction
                )
                {
                    AddTargetValues(
                        property.Value,
                        node,
                        nextContext,
                        NodeKind.Rite
                    );

                    continue;
                }

                CollectLinks(
                    property.Value,
                    node,
                    nextContext,
                    nextInsideAction
                );
            }
        }
        else if (
            element.ValueKind
            ==
            JsonValueKind.Array
        )
        {
            foreach (
                var item
                in
                element.EnumerateArray()
            )
            {
                CollectLinks(
                    item,
                    node,
                    context,
                    insideAction
                );
            }
        }
    }

    private static void AddTargetValues(
        JsonElement value,
        GuideNode node,
        string label,
        NodeKind kind
    )
    {
        if (
            value.ValueKind
            ==
            JsonValueKind.Number
            &&
            value.TryGetInt32(
                out var id
            )
        )
        {
            node.Links.Add(
                new GuideLink(
                    label,
                    id,
                    kind
                )
            );

            return;
        }

        if (
            value.ValueKind
            ==
            JsonValueKind.Array
        )
        {
            foreach (
                var item
                in
                value.EnumerateArray()
            )
            {
                if (
                    item.ValueKind
                    ==
                    JsonValueKind.Number
                    &&
                    item.TryGetInt32(
                        out var itemId
                    )
                )
                {
                    node.Links.Add(
                        new GuideLink(
                            label,
                            itemId,
                            kind
                        )
                    );
                }
            }
        }
    }

    public string DescribeTransition(
        GuideLink link
    )
    {
        string target =
            DisplayTarget(
                link
            );

        string action =
            link.TargetKind
            ==
            NodeKind.Rite
                ?
                $"开启{target}"
                :
                $"进入{target}";

        if (
            string.IsNullOrWhiteSpace(
                link.Label
            )
        )
        {
            return
                $"接下来会{action}。";
        }

        return
            $"{link.Label}，会{action}。";
    }

    public string DisplayTarget(
        GuideLink link
    )
    {
        if (
            Nodes.TryGetValue(
                link.TargetId,
                out var node
            )
        )
        {
            return node.Kind
                ==
                NodeKind.Rite
                    ?
                    $"《{node.Name}》"
                    :
                    $"「{node.Name}」";
        }

        if (
            link.TargetKind
            ==
            NodeKind.Event
            &&
            EventNames.TryGetValue(
                link.TargetId,
                out var eventName
            )
        )
        {
            return
                $"「{eventName}」";
        }

        if (
            link.TargetKind
            ==
            NodeKind.Rite
            &&
            RiteNames.TryGetValue(
                link.TargetId,
                out var riteName
            )
        )
        {
            return
                $"《{riteName}》";
        }

        return
            link.TargetKind
            ==
            NodeKind.Rite
                ?
                "后续仪式"
                :
                "后续剧情";
    }

    public IEnumerable<GuideNode> Search(
        string query
    )
    {
        query =
            query.Trim();

        IEnumerable<GuideNode> result =
            Nodes.Values;

        if (
            query.Length > 0
        )
        {
            result =
                result.Where(
                    node =>
                        node.Name.Contains(
                            query,
                            StringComparison.OrdinalIgnoreCase
                        )
                        ||
                        node.Id.ToString()
                            .Contains(
                                query
                            )
                        ||
                        (
                            node.ResultText
                                ?.Contains(
                                    query,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            ??
                            false
                        )
                        ||
                        node.IncomingRelations.Any(
                            relation =>
                                relation.NodeName.Contains(
                                    query,
                                    StringComparison.OrdinalIgnoreCase
                                )
                        )
                        ||
                        node.OutgoingRelations.Any(
                            relation =>
                                relation.NodeName.Contains(
                                    query,
                                    StringComparison.OrdinalIgnoreCase
                                )
                        )
                );
        }

        return
            result
                .OrderBy(
                    node =>
                        node.Kind
                )
                .ThenBy(
                    node =>
                        node.Id
                )
                .Take(300);
    }

    public GuideNode? Get(
        int id
    )
    {
        return
            Nodes.TryGetValue(
                id,
                out var node
            )
                ?
                node
                :
                null;
    }

    public static string StripRichText(
        string text
    )
    {
        if (
            string.IsNullOrEmpty(
                text
            )
        )
        {
            return "";
        }

        text =
            Regex.Replace(
                text,
                @"<[^>]+>",
                ""
            );

        text =
            text.Replace(
                "\\n",
                "\n"
            );

        return
            text.Trim();
    }
}
