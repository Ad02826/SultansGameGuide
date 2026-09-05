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
    public readonly Dictionary<string, string> ConditionHints =
        new(StringComparer.OrdinalIgnoreCase);

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
        ConditionHints.Clear();

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

                    // 记录“具体机器键 -> 策划注释”
                    // 例如 table_have.2000728 -> 任一正教的理念闲置
                    var keyMatch =
                        Regex.Match(
                            code,
                            "\"([^\"]+)\"\\s*:"
                        );

                    if (keyMatch.Success)
                    {
                        string machineKey =
                            keyMatch
                                .Groups[1]
                                .Value;

                        if (
                            !ConditionHints.ContainsKey(
                                machineKey
                            )
                        )
                        {
                            ConditionHints[
                                machineKey
                            ] =
                                comment;
                        }
                    }

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
                            &&
                            !CounterHints.ContainsKey(id)
                        )
                        {
                            CounterHints[id] =
                                comment;
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

                node.HumanOutcome =
                    BuildHumanOutcome(
                        root,
                        node
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

        string? semanticHint =
            node.Children
                .Select(
                    child =>
                        ConditionHints.TryGetValue(
                            child.Key.TrimStart('!'),
                            out var hint
                        )
                            ?
                            hint
                            :
                            null
                )
                .FirstOrDefault(
                    hint =>
                        hint != null
                        &&
                        (
                            hint.Contains("任一")
                            ||
                            hint.Contains("任意")
                        )
                );

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
                semanticHint
            )
        )
        {
            string natural =
                NaturalizeGroupHint(
                    semanticHint!
                );

            text =
                natural
                +
                $" 可用的包括：{JoinNames(names)}。";

            return true;
        }

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
            // 单独出现裸值时无法确定语义。
            // 不把 JSON 原样扔给玩家。
            return "还存在一个隐藏剧情条件";
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

        if (
            key.StartsWith(
                "have."
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

                bool character =
                    IsCharacter(id);

                if (negated)
                {
                    return character
                        ?
                        $"当前没有{name}"
                        :
                        $"还没有{name}";
                }

                return character
                    ?
                    $"{name}仍在"
                    :
                    $"已经拥有{name}";
            }
        }

        if (
            key.StartsWith(
                "table_have."
            )
        )
        {
            string token =
                key[11..]
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

                if (negated)
                {
                    return $"{name}当前不能使用";
                }

                return IsCharacter(id)
                    ?
                    $"{name}当前空闲，可以出面"
                    :
                    $"{name}当前没有被占用，可以使用";
            }
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
                CounterHints.TryGetValue(
                    id,
                    out var humanHint
                )
                    ?
                    humanHint
                    :
                    "";

            return HumanizeCounter(
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

        // 如果策划已经写了注释，优先使用注释而不是机器字段。
        string originalKey =
            atom.Key.TrimStart('!');

        if (
            ConditionHints.TryGetValue(
                originalKey,
                out var comment
            )
        )
        {
            return NaturalizeComment(
                comment,
                negated
            );
        }

        // 最后一层兜底也绝不展示 JSON / DSL。
        return "还存在一个隐藏剧情条件";
    }

    private string HumanizeCounter(
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
            if (
                trueLike
                &&
                (
                    op == ">="
                    ||
                    op == "="
                    ||
                    op == ">"
                )
            )
            {
                return "某个前置剧情已经完成";
            }

            if (
                trueLike
                &&
                op == "<"
            )
            {
                return "某个后续剧情还没有发生";
            }

            return "还需要满足一个隐藏剧情进度";
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

    private static string NaturalizeComment(
        string comment,
        bool negated
    )
    {
        comment =
            CleanupComment(
                comment
            );

        comment =
            comment.Replace(
                "闲置",
                "当前可用"
            );

        if (
            negated
            &&
            !ContainsNegativeMeaning(
                comment
            )
        )
        {
            return
                "不能满足："
                +
                comment;
        }

        return comment;
    }

    private static string NaturalizeGroupHint(
        string hint
    )
    {
        string text =
            CleanupComment(
                hint
            );

        text =
            text.Replace(
                "任一",
                "任意一种"
            );

        text =
            text.Replace(
                "任意一个",
                "任意一种"
            );

        text =
            text.Replace(
                "闲置",
                "当前可用"
            );

        if (
            !text.EndsWith("即可")
            &&
            !text.EndsWith("。")
        )
        {
            text +=
                "即可";
        }

        if (
            !text.EndsWith("。")
        )
        {
            text +=
                "。";
        }

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
    // 后续事件 / 仪式：翻译成“接下来会怎样”
    // ============================================================

    private string BuildHumanOutcome(
        JsonElement root,
        GuideNode node
    )
    {
        var pieces =
            new List<string>();

        string? prompt =
            FindFirstPromptText(
                root
            );

        if (
            !string.IsNullOrWhiteSpace(
                prompt
            )
        )
        {
            pieces.Add(
                StripRichText(
                    prompt!
                )
            );
        }

        var direct =
            node.Links
                .Take(4)
                .Select(
                    DescribeTransition
                )
                .Distinct()
                .ToList();

        if (
            direct.Count > 0
        )
        {
            pieces.Add(
                string.Join(
                    "\n",
                    direct.Select(
                        x =>
                            "• "
                            +
                            x
                    )
                )
            );
        }

        return
            string.Join(
                "\n",
                pieces
            );
    }

    private static string? FindFirstPromptText(
        JsonElement element
    )
    {
        if (
            element.ValueKind
            ==
            JsonValueKind.Object
        )
        {
            if (
                element.TryGetProperty(
                    "prompt",
                    out var prompt
                )
                &&
                prompt.ValueKind
                ==
                JsonValueKind.Object
                &&
                prompt.TryGetProperty(
                    "text",
                    out var text
                )
            )
            {
                return
                    text.GetString();
            }

            foreach (
                var property
                in
                element.EnumerateObject()
            )
            {
                var found =
                    FindFirstPromptText(
                        property.Value
                    );

                if (
                    !string.IsNullOrWhiteSpace(
                        found
                    )
                )
                {
                    return found;
                }
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
                var found =
                    FindFirstPromptText(
                        item
                    );

                if (
                    !string.IsNullOrWhiteSpace(
                        found
                    )
                )
                {
                    return found;
                }
            }
        }

        return null;
    }

    private void CollectLinks(
        JsonElement element,
        GuideNode node,
        string? context
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
                    nextContext
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
                    context
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
                        node.HumanCondition.Contains(
                            query,
                            StringComparison.OrdinalIgnoreCase
                        )
                        ||
                        node.HumanOutcome.Contains(
                            query,
                            StringComparison.OrdinalIgnoreCase
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
