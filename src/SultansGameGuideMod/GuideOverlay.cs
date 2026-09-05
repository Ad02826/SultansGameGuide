using System;
using BepInEx.Logging;
using UnityEngine;

namespace SultansGameGuide;

public sealed class GuideOverlay : MonoBehaviour
{
    private static readonly ManualLogSource Log =
        BepInEx.Logging.Logger.CreateLogSource("SultanGuideOverlay");

    public GuideOverlay(IntPtr ptr) : base(ptr)
    {
    }

    private static GuideDatabase? _db;

    private static bool _visible = true;
    private static bool _minimized = false;
    private static bool _loaded = false;

    private static string _loadMessage = "正在读取游戏攻略数据……";
    private static string _search = "";
    private static string _lastSearch = "\u0000";

    private static List<GuideNode> _results = new();
    private static int _resultPage = 0;
    private static int _selectedId = 0;

    private static readonly Stack<int> _history = new();

    private static Rect _panel =
        new Rect(26, 70, 920, 700);

    private static bool _dragging = false;
    private static Vector2 _dragOffset = Vector2.zero;

    private static GUIStyle? _title;
    private static GUIStyle? _subTitle;
    private static GUIStyle? _body;
    private static GUIStyle? _small;
    private static GUIStyle? _wrapButton;
    private static GUIStyle? _boxStyle;
    private static GUIStyle? _softBoxStyle;

    private static Texture2D? _panelTex;
    private static Texture2D? _softTex;

    private const int ResultsPerPage = 11;

    private void Start()
    {
        _visible = true;

        try
        {
            _db = new GuideDatabase();
            _db.Load();

            _loaded =
                _db.Nodes.Count > 0;

            _loadMessage =
                _loaded
                    ?
                    $"已读取 {_db.Nodes.Count} 个剧情节点。"
                    :
                    (
                        _db.LastError.Length > 0
                            ?
                            _db.LastError
                            :
                            "没有读取到剧情数据。"
                    );

            RefreshSearch();

            var initial =
                _db.Search(
                    "向神殿求助"
                )
                .FirstOrDefault()
                ??
                _db.Search(
                    "与正教决裂"
                )
                .FirstOrDefault()
                ??
                _db.Nodes
                    .Values
                    .OrderBy(
                        x =>
                            x.Id
                    )
                    .FirstOrDefault();

            if (initial != null)
            {
                _selectedId =
                    initial.Id;
            }

            Log.LogInfo(
                "GuideOverlay.Start invoked"
            );

            Log.LogInfo(
                _loadMessage
            );
        }
        catch (Exception ex)
        {
            _loaded = false;

            _loadMessage =
                "读取攻略数据失败："
                +
                ex.Message;

            Log.LogError(
                ex
            );
        }
    }

    private void OnGUI()
    {
        // 不使用 UnityEngine.Input.GetKey。
        // 当前游戏的 legacy Input 在 IL2CPP 下会抛 SEHException。
        var e =
            Event.current;

        if (
            e != null
            &&
            e.type
            ==
            EventType.KeyDown
            &&
            e.keyCode
            ==
            KeyCode.O
            &&
            e.control
        )
        {
            _visible =
                !_visible;

            e.Use();
        }

        EnsureStyles();

        if (!_visible)
        {
            if (
                GUI.Button(
                    new Rect(
                        14,
                        80,
                        112,
                        36
                    ),
                    "攻略助手"
                )
            )
            {
                _visible =
                    true;
            }

            return;
        }

        if (_minimized)
        {
            if (
                GUI.Button(
                    new Rect(
                        14,
                        80,
                        132,
                        38
                    ),
                    "攻略助手 ＋"
                )
            )
            {
                _minimized =
                    false;
            }

            return;
        }

        HandleDrag(
            e
        );

        ClampPanel();
        DrawPanel();
    }

    private static void DrawPanel()
    {
        GUI.Box(
            _panel,
            "",
            _boxStyle
        );

        float x =
            _panel.x;

        float y =
            _panel.y;

        float w =
            _panel.width;

        float h =
            _panel.height;

        GUI.Label(
            new Rect(
                x + 16,
                y + 10,
                480,
                30
            ),
            "苏丹的游戏 · 攻略助手",
            _title
        );

        GUI.Label(
            new Rect(
                x + 385,
                y + 15,
                310,
                22
            ),
            "v0.3.0 · 人话语义版",
            _small
        );

        if (
            GUI.Button(
                new Rect(
                    x + w - 82,
                    y + 9,
                    30,
                    26
                ),
                "—"
            )
        )
        {
            _minimized =
                true;
        }

        if (
            GUI.Button(
                new Rect(
                    x + w - 44,
                    y + 9,
                    30,
                    26
                ),
                "×"
            )
        )
        {
            _visible =
                false;
        }

        GUI.Label(
            new Rect(
                x + 16,
                y + 43,
                w - 32,
                20
            ),
            _loadMessage,
            _small
        );

        GUI.Label(
            new Rect(
                x + 16,
                y + 70,
                52,
                26
            ),
            "搜索",
            _subTitle
        );

        string newSearch =
            GUI.TextField(
                new Rect(
                    x + 68,
                    y + 68,
                    w - 166,
                    29
                ),
                _search
                ??
                ""
            );

        if (
            newSearch
            !=
            _search
        )
        {
            _search =
                newSearch;

            RefreshSearch();
        }

        if (
            GUI.Button(
                new Rect(
                    x + w - 88,
                    y + 68,
                    72,
                    29
                ),
                "清空"
            )
        )
        {
            _search =
                "";

            RefreshSearch();
        }

        float leftW =
            Math.Max(
                270,
                Math.Min(
                    330,
                    w * 0.34f
                )
            );

        float splitX =
            x + leftW + 14;

        float contentY =
            y + 108;

        float contentH =
            h - 124;

        GUI.Box(
            new Rect(
                x + 12,
                contentY,
                leftW - 12,
                contentH
            ),
            "",
            _softBoxStyle
        );

        GUI.Label(
            new Rect(
                x + 24,
                contentY + 10,
                leftW - 36,
                24
            ),
            $"搜索结果（{_results.Count}）",
            _subTitle
        );

        DrawResults(
            x + 20,
            contentY + 42,
            leftW - 28,
            contentH - 54
        );

        GUI.Box(
            new Rect(
                splitX,
                contentY,
                w - (splitX - x) - 12,
                contentH
            ),
            "",
            _softBoxStyle
        );

        DrawDetails(
            splitX + 14,
            contentY + 12,
            w - (splitX - x) - 40,
            contentH - 24
        );
    }

    private static void DrawResults(
        float x,
        float y,
        float w,
        float h
    )
    {
        if (
            !_loaded
            ||
            _db == null
        )
        {
            GUI.Label(
                new Rect(
                    x,
                    y,
                    w,
                    70
                ),
                "攻略数据库尚未加载。",
                _body
            );

            return;
        }

        int pageCount =
            Math.Max(
                1,
                (
                    _results.Count
                    +
                    ResultsPerPage
                    -
                    1
                )
                /
                ResultsPerPage
            );

        _resultPage =
            Math.Max(
                0,
                Math.Min(
                    _resultPage,
                    pageCount - 1
                )
            );

        int start =
            _resultPage
            *
            ResultsPerPage;

        int end =
            Math.Min(
                _results.Count,
                start + ResultsPerPage
            );

        float rowH =
            42f;

        float currentY =
            y;

        for (
            int i = start;
            i < end;
            i++
        )
        {
            var node =
                _results[i];

            string marker =
                node.Id
                ==
                _selectedId
                    ?
                    "▶ "
                    :
                    "";

            string label =
                $"{marker}[{KindName(node.Kind)}] {node.Name}";

            if (
                GUI.Button(
                    new Rect(
                        x,
                        currentY,
                        w,
                        rowH - 4
                    ),
                    label,
                    _wrapButton
                )
            )
            {
                NavigateTo(
                    node.Id,
                    true
                );
            }

            currentY +=
                rowH;
        }

        float navY =
            y + h - 32;

        if (
            GUI.Button(
                new Rect(
                    x,
                    navY,
                    62,
                    26
                ),
                "上一页"
            )
            &&
            _resultPage > 0
        )
        {
            _resultPage--;
        }

        if (
            GUI.Button(
                new Rect(
                    x + 68,
                    navY,
                    62,
                    26
                ),
                "下一页"
            )
            &&
            _resultPage + 1
            <
            pageCount
        )
        {
            _resultPage++;
        }

        GUI.Label(
            new Rect(
                x + 140,
                navY + 3,
                w - 140,
                22
            ),
            $"{_resultPage + 1} / {pageCount}",
            _small
        );
    }

    private static void DrawDetails(
        float x,
        float y,
        float w,
        float h
    )
    {
        if (
            !_loaded
            ||
            _db == null
        )
        {
            GUI.Label(
                new Rect(
                    x,
                    y,
                    w,
                    100
                ),
                _loadMessage,
                _body
            );

            return;
        }

        var node =
            _db.Get(
                _selectedId
            );

        if (node == null)
        {
            GUI.Label(
                new Rect(
                    x,
                    y,
                    w,
                    80
                ),
                "从左侧选择一个剧情节点。",
                _body
            );

            return;
        }

        float cy =
            y;

        if (
            _history.Count > 0
            &&
            GUI.Button(
                new Rect(
                    x,
                    cy,
                    70,
                    26
                ),
                "← 返回"
            )
        )
        {
            _selectedId =
                _history.Pop();

            return;
        }

        GUI.Label(
            new Rect(
                x + 80,
                cy,
                w - 80,
                34
            ),
            $"{KindName(node.Kind)} · {node.Name}",
            _title
        );

        cy +=
            48;

        GUI.Label(
            new Rect(
                x,
                cy,
                w,
                24
            ),
            "怎么触发？",
            _subTitle
        );

        cy +=
            27;

        string condition =
            string.IsNullOrWhiteSpace(
                node.HumanCondition
            )
                ?
                "没有额外要求。"
                :
                node.HumanCondition;

        float conditionHeight =
            EstimateTextHeight(
                condition,
                58,
                190
            );

        GUI.Box(
            new Rect(
                x,
                cy,
                w,
                conditionHeight
            ),
            ""
        );

        GUI.Label(
            new Rect(
                x + 9,
                cy + 7,
                w - 18,
                conditionHeight - 14
            ),
            condition,
            _body
        );

        cy +=
            conditionHeight
            +
            12;

        if (
            !string.IsNullOrWhiteSpace(
                node.HumanOutcome
            )
        )
        {
            GUI.Label(
                new Rect(
                    x,
                    cy,
                    w,
                    24
                ),
                "接下来会怎样？",
                _subTitle
            );

            cy +=
                27;

            float outcomeHeight =
                EstimateTextHeight(
                    node.HumanOutcome,
                    52,
                    135
                );

            GUI.Box(
                new Rect(
                    x,
                    cy,
                    w,
                    outcomeHeight
                ),
                ""
            );

            GUI.Label(
                new Rect(
                    x + 9,
                    cy + 7,
                    w - 18,
                    outcomeHeight - 14
                ),
                node.HumanOutcome,
                _body
            );

            cy +=
                outcomeHeight
                +
                12;
        }

        if (
            !string.IsNullOrWhiteSpace(
                node.ResultText
            )
        )
        {
            GUI.Label(
                new Rect(
                    x,
                    cy,
                    w,
                    24
                ),
                "结局说明",
                _subTitle
            );

            cy +=
                27;

            string result =
                node.ResultText!;

            if (
                result.Length
                >
                700
            )
            {
                result =
                    result[..700]
                    +
                    "\n……";
            }

            float resultHeight =
                EstimateTextHeight(
                    result,
                    65,
                    145
                );

            GUI.Box(
                new Rect(
                    x,
                    cy,
                    w,
                    resultHeight
                ),
                ""
            );

            GUI.Label(
                new Rect(
                    x + 9,
                    cy + 7,
                    w - 18,
                    resultHeight - 14
                ),
                result,
                _body
            );

            cy +=
                resultHeight
                +
                12;
        }

        GUI.Label(
            new Rect(
                x,
                cy,
                w,
                24
            ),
            node.Links.Count > 0
                ?
                "可以继续看："
                :
                "后续",
            _subTitle
        );

        cy +=
            27;

        if (
            node.Links.Count == 0
        )
        {
            GUI.Label(
                new Rect(
                    x,
                    cy,
                    w,
                    45
                ),
                node.Kind
                ==
                NodeKind.AfterStory
                    ?
                    "这里已经是结局 / 后日谈。"
                    :
                    "没有解析到直接后续剧情。",
                _body
            );

            return;
        }

        int shown =
            0;

        foreach (
            var link
            in
            node.Links
        )
        {
            if (
                shown >= 6
            )
            {
                break;
            }

            string label =
                _db.DescribeTransition(
                    link
                );

            if (
                GUI.Button(
                    new Rect(
                        x,
                        cy,
                        w,
                        44
                    ),
                    label,
                    _wrapButton
                )
            )
            {
                NavigateTo(
                    link.TargetId,
                    true
                );
            }

            cy +=
                48;

            shown++;
        }

        if (
            node.Links.Count
            >
            shown
        )
        {
            GUI.Label(
                new Rect(
                    x,
                    cy,
                    w,
                    24
                ),
                $"还有 {node.Links.Count - shown} 条分支，可搜索剧情名称继续查看。",
                _small
            );
        }
    }

    private static float EstimateTextHeight(
        string text,
        float minimum,
        float maximum
    )
    {
        int lines =
            1;

        foreach (
            char c
            in
            text
        )
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        // 再按中文大约每 28~32 字一行估算自动换行。
        lines +=
            text.Length
            /
            34;

        float height =
            18f
            *
            lines
            +
            18f;

        return
            Math.Max(
                minimum,
                Math.Min(
                    maximum,
                    height
                )
            );
    }

    private static void NavigateTo(
        int id,
        bool pushHistory
    )
    {
        if (
            _db == null
            ||
            _db.Get(id) == null
        )
        {
            return;
        }

        if (
            pushHistory
            &&
            _selectedId != 0
            &&
            _selectedId != id
        )
        {
            _history.Push(
                _selectedId
            );
        }

        _selectedId =
            id;
    }

    private static void RefreshSearch()
    {
        if (_db == null)
        {
            return;
        }

        if (
            _lastSearch == _search
            &&
            _results.Count > 0
        )
        {
            return;
        }

        _lastSearch =
            _search;

        _results =
            _db.Search(
                _search
            )
            .ToList();

        _resultPage =
            0;
    }

    private static void HandleDrag(
        Event? e
    )
    {
        if (e == null)
        {
            return;
        }

        var titleBar =
            new Rect(
                _panel.x,
                _panel.y,
                _panel.width - 100,
                52
            );

        if (
            e.type
            ==
            EventType.MouseDown
            &&
            e.button
            ==
            0
            &&
            titleBar.Contains(
                e.mousePosition
            )
        )
        {
            _dragging =
                true;

            _dragOffset =
                new Vector2(
                    e.mousePosition.x
                    -
                    _panel.x,

                    e.mousePosition.y
                    -
                    _panel.y
                );

            e.Use();
        }
        else if (
            e.type
            ==
            EventType.MouseDrag
            &&
            _dragging
        )
        {
            _panel.x =
                e.mousePosition.x
                -
                _dragOffset.x;

            _panel.y =
                e.mousePosition.y
                -
                _dragOffset.y;

            e.Use();
        }
        else if (
            e.type
            ==
            EventType.MouseUp
        )
        {
            _dragging =
                false;
        }
    }

    private static void ClampPanel()
    {
        _panel.width =
            Math.Min(
                _panel.width,
                Screen.width - 20
            );

        _panel.height =
            Math.Min(
                _panel.height,
                Screen.height - 20
            );

        _panel.x =
            Math.Max(
                0,
                Math.Min(
                    _panel.x,
                    Screen.width
                    -
                    _panel.width
                )
            );

        _panel.y =
            Math.Max(
                0,
                Math.Min(
                    _panel.y,
                    Screen.height - 45
                )
            );
    }

    private static void EnsureStyles()
    {
        if (
            _panelTex
            ==
            null
        )
        {
            _panelTex =
                new Texture2D(
                    1,
                    1
                );

            _panelTex.SetPixel(
                0,
                0,
                new Color(
                    0.045f,
                    0.065f,
                    0.085f,
                    0.91f
                )
            );

            _panelTex.Apply();
        }

        if (
            _softTex
            ==
            null
        )
        {
            _softTex =
                new Texture2D(
                    1,
                    1
                );

            _softTex.SetPixel(
                0,
                0,
                new Color(
                    0.07f,
                    0.10f,
                    0.13f,
                    0.76f
                )
            );

            _softTex.Apply();
        }

        if (
            _boxStyle
            ==
            null
        )
        {
            _boxStyle =
                new GUIStyle();

            _boxStyle
                .normal
                .background =
                    _panelTex;
        }

        if (
            _softBoxStyle
            ==
            null
        )
        {
            _softBoxStyle =
                new GUIStyle();

            _softBoxStyle
                .normal
                .background =
                    _softTex;
        }

        if (
            _title
            ==
            null
        )
        {
            _title =
                new GUIStyle();

            _title.fontSize =
                16;

            _title.fontStyle =
                FontStyle.Bold;

            _title.wordWrap =
                true;

            _title
                .normal
                .textColor =
                    new Color(
                        0.86f,
                        0.94f,
                        1f,
                        1f
                    );
        }

        if (
            _subTitle
            ==
            null
        )
        {
            _subTitle =
                new GUIStyle();

            _subTitle.fontSize =
                13;

            _subTitle.fontStyle =
                FontStyle.Bold;

            _subTitle
                .normal
                .textColor =
                    new Color(
                        0.64f,
                        0.84f,
                        0.98f,
                        1f
                    );
        }

        if (
            _body
            ==
            null
        )
        {
            _body =
                new GUIStyle();

            _body.fontSize =
                13;

            _body.wordWrap =
                true;

            _body
                .normal
                .textColor =
                    Color.white;
        }

        if (
            _small
            ==
            null
        )
        {
            _small =
                new GUIStyle();

            _small.fontSize =
                11;

            _small.wordWrap =
                true;

            _small
                .normal
                .textColor =
                    new Color(
                        0.67f,
                        0.73f,
                        0.78f,
                        1f
                    );
        }

        if (
            _wrapButton
            ==
            null
        )
        {
            _wrapButton =
                new GUIStyle();

            _wrapButton.fontSize =
                12;

            _wrapButton.wordWrap =
                true;

            _wrapButton.alignment =
                TextAnchor.MiddleLeft;

            _wrapButton
                .normal
                .background =
                    _softTex;

            _wrapButton
                .hover
                .background =
                    _panelTex;

            _wrapButton
                .active
                .background =
                    _panelTex;

            _wrapButton
                .normal
                .textColor =
                    Color.white;

            _wrapButton
                .hover
                .textColor =
                    Color.white;

            _wrapButton
                .active
                .textColor =
                    Color.white;
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
}
