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
        new Rect(26, 70, 900, 680);

    private static bool _dragging = false;

    private static Vector2 _dragOffset =
        Vector2.zero;

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

            _loaded = _db.Nodes.Count > 0;

            _loadMessage = _loaded
                ? $"已读取 {_db.Nodes.Count} 个剧情节点、{_db.CardNames.Count} 张卡牌。"
                : (
                    _db.LastError.Length > 0
                        ? _db.LastError
                        : "没有读取到剧情数据。"
                );

            RefreshSearch();

            // 默认先尝试定位到“与正教决裂”
            var initial =
                _db.Search("与正教决裂").FirstOrDefault()
                ??
                _db.Nodes.Values
                    .OrderBy(x => x.Id)
                    .FirstOrDefault();

            if (initial != null)
            {
                _selectedId = initial.Id;
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
                "读取攻略数据失败：" + ex.Message;

            Log.LogError(ex);
        }
    }

    private void OnGUI()
    {
        /*
         * 不再使用：
         *
         * Input.GetKey(...)
         *
         * 因为《苏丹的游戏》当前 IL2CPP interop
         * 调用 legacy Input 会产生 SEHException。
         *
         * Ctrl + O 改由 IMGUI Event 监听。
         */

        var e = Event.current;

        if (
            e != null
            &&
            e.type == EventType.KeyDown
            &&
            e.keyCode == KeyCode.O
            &&
            e.control
        )
        {
            _visible = !_visible;

            e.Use();
        }

        EnsureStyles();

        // 完全隐藏时仍保留一个入口按钮
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
                _visible = true;
            }

            return;
        }

        // 最小化状态
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
                _minimized = false;
            }

            return;
        }

        HandleDrag(e);

        ClampPanel();

        DrawPanel();
    }

    private static void DrawPanel()
    {
        var oldColor = GUI.color;

        GUI.color = Color.white;

        GUI.Box(
            _panel,
            "",
            _boxStyle
        );

        float x = _panel.x;
        float y = _panel.y;
        float w = _panel.width;
        float h = _panel.height;

        // =========================
        // 标题栏
        // =========================

        GUI.Label(
            new Rect(
                x + 16,
                y + 10,
                500,
                30
            ),
            "苏丹的游戏 · 攻略助手",
            _title
        );

        GUI.Label(
            new Rect(
                x + 340,
                y + 15,
                300,
                22
            ),
            "v0.2.1 · 读取游戏真实配置",
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
            _minimized = true;
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
            _visible = false;
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

        // =========================
        // 搜索
        // =========================

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
                _search ?? ""
            );

        if (newSearch != _search)
        {
            _search = newSearch;

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
            _search = "";

            RefreshSearch();
        }

        // =========================
        // 主体区域
        // =========================

        float leftW =
            Math.Max(
                265,
                Math.Min(
                    340,
                    w * 0.36f
                )
            );

        float splitX =
            x + leftW + 14;

        float contentY =
            y + 108;

        float contentH =
            h - 124;

        // 左栏
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

        // 右栏
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

        GUI.color = oldColor;
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
                    80
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

        float rowH = 42f;

        float currentY = y;

        for (
            int i = start;
            i < end;
            i++
        )
        {
            var n = _results[i];

            string marker =
                n.Id == _selectedId
                    ? "▶ "
                    : "";

            string kind =
                KindName(
                    n.Kind
                );

            string label =
                $"{marker}[{kind}] {n.Name}\n#{n.Id}";

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
                    n.Id,
                    true
                );
            }

            currentY += rowH;
        }

        // =========================
        // 分页
        // =========================

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
            _resultPage + 1 < pageCount
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
                "从左侧选择一个事件、仪式或结局。",
                _body
            );

            return;
        }

        // =========================
        // 返回
        // =========================

        if (
            _history.Count > 0
            &&
            GUI.Button(
                new Rect(
                    x,
                    y,
                    68,
                    26
                ),
                "← 返回"
            )
        )
        {
            int prev =
                _history.Pop();

            _selectedId = prev;

            return;
        }

        // =========================
        // 标题
        // =========================

        GUI.Label(
            new Rect(
                x + 78,
                y + 1,
                w - 78,
                28
            ),
            $"{KindName(node.Kind)} · {node.Name}",
            _title
        );

        GUI.Label(
            new Rect(
                x + 78,
                y + 30,
                w - 78,
                20
            ),
            $"ID：{node.Id}",
            _small
        );

        float cy =
            y + 62;

        // =========================
        // 触发条件
        // =========================

        GUI.Label(
            new Rect(
                x,
                cy,
                w,
                24
            ),
            "触发条件",
            _subTitle
        );

        cy += 27;

        string condition =
            string.IsNullOrWhiteSpace(
                node.HumanCondition
            )
            ?
            "无特殊条件"
            :
            node.HumanCondition;

        var conditionContent =
            new GUIContent(
                condition
            );

        float calculatedConditionHeight =
            _body != null
                ?
                _body.CalcHeight(
                    conditionContent,
                    w - 12
                )
                :
                80;

        float condH =
            Math.Min(
                180,
                Math.Max(
                    52,
                    calculatedConditionHeight
                    +
                    14
                )
            );

        GUI.Box(
            new Rect(
                x,
                cy,
                w,
                condH
            ),
            ""
        );

        GUI.Label(
            new Rect(
                x + 8,
                cy + 6,
                w - 16,
                condH - 12
            ),
            condition,
            _body
        );

        cy +=
            condH
            +
            12;

        // =========================
        // 结局文本
        // =========================

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
                "结局内容",
                _subTitle
            );

            cy += 26;

            string txt =
                node.ResultText!;

            if (
                txt.Length
                >
                900
            )
            {
                txt =
                    txt[..900]
                    +
                    "\n……";
            }

            var resultContent =
                new GUIContent(
                    txt
                );

            float calculatedResultHeight =
                _body != null
                    ?
                    _body.CalcHeight(
                        resultContent,
                        w - 12
                    )
                    :
                    100;

            float resultH =
                Math.Min(
                    190,
                    Math.Max(
                        70,
                        calculatedResultHeight
                        +
                        12
                    )
                );

            GUI.Box(
                new Rect(
                    x,
                    cy,
                    w,
                    resultH
                ),
                ""
            );

            GUI.Label(
                new Rect(
                    x + 8,
                    cy + 6,
                    w - 16,
                    resultH - 12
                ),
                txt,
                _body
            );

            cy +=
                resultH
                +
                10;
        }

        // =========================
        // 后续分支
        // =========================

        GUI.Label(
            new Rect(
                x,
                cy,
                w,
                24
            ),
            $"后续分支（{node.Links.Count}）",
            _subTitle
        );

        cy += 27;

        if (
            node.Links.Count
            ==
            0
        )
        {
            GUI.Label(
                new Rect(
                    x,
                    cy,
                    w,
                    48
                ),
                node.Kind
                    ==
                    NodeKind.AfterStory
                    ?
                    "这是结局 / 后日谈节点。"
                    :
                    "当前配置中没有解析到直接后续节点。",
                _body
            );

            return;
        }

        int shown = 0;

        foreach (
            var link
            in
            node.Links
        )
        {
            if (
                shown
                >=
                7
            )
            {
                break;
            }

            string target =
                _db.DisplayTarget(
                    link
                );

            string label =
                $"{link.Label}\n→ {target}  #{link.TargetId}";

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

            cy += 48;

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
                $"还有 {node.Links.Count - shown} 条分支；可直接搜索目标 ID。",
                _small
            );
        }
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

        _selectedId = id;
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
            _db
                .Search(_search)
                .ToList();

        _resultPage = 0;
    }

    // =============================
    // 拖动窗口
    // =============================

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
                50
            );

        if (
            e.type == EventType.MouseDown
            &&
            e.button == 0
            &&
            titleBar.Contains(
                e.mousePosition
            )
        )
        {
            _dragging = true;

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
            _dragging = false;
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
                    Screen.width - _panel.width
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

    // =============================
    // IMGUI 样式
    // =============================

    private static void EnsureStyles()
    {
        if (_panelTex == null)
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

        if (_softTex == null)
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

        /*
         * 注意：
         *
         * 这里绝对不能写：
         *
         * new GUIStyle(GUI.skin.label)
         * new GUIStyle(GUI.skin.button)
         *
         * 因为游戏生成的 IL2CPP interop GUIStyle
         * 不存在普通 Unity 的复制构造函数。
         */

        if (_boxStyle == null)
        {
            _boxStyle =
                new GUIStyle();

            _boxStyle.normal.background =
                _panelTex;
        }

        if (_softBoxStyle == null)
        {
            _softBoxStyle =
                new GUIStyle();

            _softBoxStyle.normal.background =
                _softTex;
        }

        if (_title == null)
        {
            _title =
                new GUIStyle();

            _title.fontSize = 16;

            _title.fontStyle =
                FontStyle.Bold;

            _title.wordWrap =
                true;

            _title.normal.textColor =
                new Color(
                    0.86f,
                    0.94f,
                    1f,
                    1f
                );
        }

        if (_subTitle == null)
        {
            _subTitle =
                new GUIStyle();

            _subTitle.fontSize =
                13;

            _subTitle.fontStyle =
                FontStyle.Bold;

            _subTitle.normal.textColor =
                new Color(
                    0.64f,
                    0.84f,
                    0.98f,
                    1f
                );
        }

        if (_body == null)
        {
            _body =
                new GUIStyle();

            _body.fontSize =
                13;

            _body.wordWrap =
                true;

            _body.normal.textColor =
                Color.white;
        }

        if (_small == null)
        {
            _small =
                new GUIStyle();

            _small.fontSize =
                11;

            _small.wordWrap =
                true;

            _small.normal.textColor =
                new Color(
                    0.67f,
                    0.73f,
                    0.78f,
                    1f
                );
        }

        if (_wrapButton == null)
        {
            _wrapButton =
                new GUIStyle();

            _wrapButton.fontSize =
                12;

            _wrapButton.wordWrap =
                true;

            _wrapButton.alignment =
                TextAnchor.MiddleLeft;

            _wrapButton.padding =
                new RectOffset(
                    8,
                    8,
                    4,
                    4
                );

            // 自己设置按钮背景，
            // 不依赖 GUI.skin.button 的复制构造
            _wrapButton.normal.background =
                _softTex;

            _wrapButton.hover.background =
                _panelTex;

            _wrapButton.active.background =
                _panelTex;

            _wrapButton.normal.textColor =
                Color.white;

            _wrapButton.hover.textColor =
                Color.white;

            _wrapButton.active.textColor =
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
