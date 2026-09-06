using System;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SultansGameGuide;

public sealed class GuideOverlay : MonoBehaviour
{
    private static readonly ManualLogSource Log =
        BepInEx.Logging.Logger.CreateLogSource("SultanGuideOverlay");

    public GuideOverlay(IntPtr ptr) : base(ptr)
    {
    }

    private sealed class RuntimeNodeItem
    {
        public GuideNode Node { get; init; } = null!;
        public string Prefix { get; init; } = "";
        public bool IsActive { get; init; }
    }

    private static GuideDatabase? _db;

    private static bool _visible = true;
    private static bool _minimized = false;
    private static bool _loaded = false;

    private static string _loadMessage = "正在读取游戏攻略数据……";

    // 左栏：0 = 当前剧情；1 = 全部搜索
    private static int _leftMode = 0;
    private static bool _autoFollow = true;

    private static string _search = "";
    private static string _lastSearch = "\u0000";
    private static List<GuideNode> _results = new();

    private static readonly List<RuntimeNodeItem> _runtimeNodes = new();
    private static readonly HashSet<int> _activeEventIds = new();
    private static string _runtimeStatus = "正在连接当前游戏状态……";
    private static DateTime _nextRuntimeRefreshUtc = DateTime.MinValue;
    private static string _activeSignature = "";

    private static int _resultPage = 0;
    private static int _selectedId = 0;

    private static readonly Stack<int> _history = new();

    // 右侧详情区的滚动位置
    private static Vector2 _detailScroll = Vector2.zero;

    private static Rect _panel =
        new Rect(26, 70, 940, 710);

    private static bool _dragging = false;
    private static Vector2 _dragOffset = Vector2.zero;

    // 独立的透明 UGUI 射线挡板：
    // 只覆盖攻略助手自身区域，拦截游戏 UI 的点击，
    // 但绝不关闭游戏自己的 EventSystem。
    private static GameObject? _inputBlockerRoot;
    private static RectTransform? _inputBlockerRect;
    private static Image? _inputBlockerImage;

    private static GUIStyle? _title;
    private static GUIStyle? _subTitle;
    private static GUIStyle? _body;
    private static GUIStyle? _small;
    private static GUIStyle? _wrapButton;
    private static GUIStyle? _boxStyle;
    private static GUIStyle? _softBoxStyle;
    private static GUIStyle? _activeButtonStyle;

    private static Texture2D? _panelTex;
    private static Texture2D? _softTex;
    private static Texture2D? _activeTex;

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
            RefreshRuntimeContext(force: true);

            if (_selectedId == 0)
            {
                var initial =
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
            }

            EnsureInputBlocker();

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

    private void OnDestroy()
    {
        DestroyInputBlocker();
    }

    private void OnDisable()
    {
        SetInputBlockerVisible(
            false
        );
    }

    private void OnGUI()
    {
        var e =
            Event.current;

        // Ctrl+O 仍作为备用开关。
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

            if (!_visible)
            {
                SetInputBlockerVisible(false);
            }

            e.Use();
        }

        EnsureStyles();

        if (!_visible)
        {
            Rect openRect =
                new Rect(
                    220,
                    80,
                    118,
                    38
                );

            UpdateInputBlockerRect(
                openRect
            );

            if (
                GUI.Button(
                    openRect,
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
            Rect miniRect =
                new Rect(
                    220,
                    80,
                    138,
                    40
                );

            UpdateInputBlockerRect(
                miniRect
            );

            if (
                GUI.Button(
                    miniRect,
                    "攻略助手 ＋"
                )
            )
            {
                _minimized =
                    false;
            }

            return;
        }

        RefreshRuntimeContext(force: false);

        HandleDrag(
            e
        );

        ClampPanel();

        // 先根据当前鼠标位置决定是否压住游戏 UI。
        bool mouseInside =
            e != null
            &&
            _panel.Contains(
                e.mousePosition
            );

        UpdateInputBlockerRect(
            _dragging
                ?
                new Rect(
                    0f,
                    0f,
                    Screen.width,
                    Screen.height
                )
                :
                _panel
        );

        DrawPanel();

        // IMGUI 自己的鼠标事件也吃掉。
        // 注意放在 DrawPanel 之后，否则攻略窗自己的按钮也收不到点击。
        if (
            (
                mouseInside
                ||
                _dragging
            )
            &&
            e != null
            &&
            IsMouseEvent(
                e.type
            )
            &&
            e.type
            !=
            EventType.Used
        )
        {
            e.Use();
        }
    }

    private static bool IsMouseEvent(
        EventType type
    )
    {
        return
            type
            ==
            EventType.MouseDown
            ||
            type
            ==
            EventType.MouseUp
            ||
            type
            ==
            EventType.MouseDrag
            ||
            type
            ==
            EventType.ScrollWheel;
    }

    private static void EnsureInputBlocker()
    {
        if (
            _inputBlockerRoot != null
            &&
            _inputBlockerRect != null
            &&
            _inputBlockerImage != null
        )
        {
            return;
        }

        try
        {
            _inputBlockerRoot =
                new GameObject(
                    "SultanGuideInputBlocker"
                );

            UnityEngine.Object.DontDestroyOnLoad(
                _inputBlockerRoot
            );

            var canvas =
                _inputBlockerRoot
                    .AddComponent<Canvas>();

            canvas.renderMode =
                RenderMode.ScreenSpaceOverlay;

            canvas.sortingOrder =
                32760;

            _inputBlockerRoot
                .AddComponent<GraphicRaycaster>();

            var blocker =
                new GameObject(
                    "BlockerRect"
                );

            blocker.transform.SetParent(
                _inputBlockerRoot.transform,
                false
            );

            _inputBlockerRect =
                blocker
                    .AddComponent<RectTransform>();

            _inputBlockerRect.anchorMin =
                new Vector2(
                    0f,
                    0f
                );

            _inputBlockerRect.anchorMax =
                new Vector2(
                    0f,
                    0f
                );

            _inputBlockerRect.pivot =
                new Vector2(
                    0f,
                    0f
                );

            _inputBlockerImage =
                blocker
                    .AddComponent<Image>();

            // 必须有 Graphic 才能参与 GraphicRaycaster；
            // alpha 极低，视觉上仍然完全透明。
            _inputBlockerImage.color =
                new Color(
                    0f,
                    0f,
                    0f,
                    0.001f
                );

            _inputBlockerImage.raycastTarget =
                true;

            SetInputBlockerVisible(
                false
            );

            Log.LogInfo(
                "UI raycast blocker ready"
            );
        }
        catch (Exception ex)
        {
            Log.LogWarning(
                "Create input blocker failed: "
                +
                ex.Message
            );

            DestroyInputBlocker();
        }
    }

    private static void UpdateInputBlockerRect(
        Rect guiRect
    )
    {
        EnsureInputBlocker();

        if (
            _inputBlockerRoot == null
            ||
            _inputBlockerRect == null
        )
        {
            return;
        }

        // IMGUI 原点在左上角；
        // ScreenSpaceOverlay RectTransform 原点按左下角换算。
        float bottomY =
            Screen.height
            -
            guiRect.y
            -
            guiRect.height;

        _inputBlockerRect.anchoredPosition =
            new Vector2(
                guiRect.x,
                bottomY
            );

        _inputBlockerRect.sizeDelta =
            new Vector2(
                guiRect.width,
                guiRect.height
            );

        SetInputBlockerVisible(
            true
        );
    }

    private static void SetInputBlockerVisible(
        bool visible
    )
    {
        if (
            _inputBlockerRoot == null
        )
        {
            return;
        }

        if (
            _inputBlockerRoot.activeSelf
            !=
            visible
        )
        {
            _inputBlockerRoot.SetActive(
                visible
            );
        }
    }

    private static void DestroyInputBlocker()
    {
        try
        {
            if (
                _inputBlockerRoot != null
            )
            {
                UnityEngine.Object.Destroy(
                    _inputBlockerRoot
                );
            }
        }
        catch
        {
        }

        _inputBlockerImage =
            null;

        _inputBlockerRect =
            null;

        _inputBlockerRoot =
            null;
    }

    // ============================================================
    // 运行时：读取当前活跃事件并自动构造“当前剧情”左栏
    // ============================================================

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryGetRuntimeRiteId(
        object riteController,
        out int riteId
    )
    {
        riteId = 0;

        string[] directNames =
        {
            "riteId",
            "rite_id",
            "id",
            "RiteId",
            "RiteID"
        };

        if (
            TryReadNumericMember(
                riteController,
                directNames,
                out riteId
            )
        )
        {
            return true;
        }

        string[] nestedNames =
        {
            "rite",
            "Rite",
            "riteConfig",
            "RiteConfig",
            "config",
            "Config",
            "riteNode",
            "RiteNode"
        };

        foreach (
            string nestedName
            in
            nestedNames
        )
        {
            object? nested =
                TryReadObjectMember(
                    riteController,
                    nestedName
                );

            if (
                nested == null
            )
            {
                continue;
            }

            if (
                TryReadNumericMember(
                    nested,
                    directNames,
                    out riteId
                )
            )
            {
                return true;
            }

            foreach (
                string innerName
                in
                nestedNames
            )
            {
                object? inner =
                    TryReadObjectMember(
                        nested,
                        innerName
                    );

                if (
                    inner != null
                    &&
                    TryReadNumericMember(
                        inner,
                        directNames,
                        out riteId
                    )
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadNumericMember(
        object target,
        IEnumerable<string> candidateNames,
        out int value
    )
    {
        value = 0;

        Type type =
            target.GetType();

        var flags =
            System.Reflection.BindingFlags.Instance
            |
            System.Reflection.BindingFlags.Public
            |
            System.Reflection.BindingFlags.NonPublic;

        foreach (
            string name
            in
            candidateNames
        )
        {
            try
            {
                var property =
                    type.GetProperty(
                        name,
                        flags
                    );

                if (
                    property != null
                    &&
                    TryConvertPositiveInt(
                        property.GetValue(
                            target
                        ),
                        out value
                    )
                )
                {
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                var field =
                    type.GetField(
                        name,
                        flags
                    );

                if (
                    field != null
                    &&
                    TryConvertPositiveInt(
                        field.GetValue(
                            target
                        ),
                        out value
                    )
                )
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static object? TryReadObjectMember(
        object target,
        string name
    )
    {
        Type type =
            target.GetType();

        var flags =
            System.Reflection.BindingFlags.Instance
            |
            System.Reflection.BindingFlags.Public
            |
            System.Reflection.BindingFlags.NonPublic;

        try
        {
            var property =
                type.GetProperty(
                    name,
                    flags
                );

            if (
                property != null
            )
            {
                return property.GetValue(
                    target
                );
            }
        }
        catch
        {
        }

        try
        {
            var field =
                type.GetField(
                    name,
                    flags
                );

            if (
                field != null
            )
            {
                return field.GetValue(
                    target
                );
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool TryConvertPositiveInt(
        object? raw,
        out int value
    )
    {
        value = 0;

        if (
            raw == null
        )
        {
            return false;
        }

        try
        {
            long parsed =
                Convert.ToInt64(
                    raw
                );

            if (
                parsed > 0
                &&
                parsed <= int.MaxValue
            )
            {
                value =
                    (int)parsed;

                return true;
            }
        }
        catch
        {
        }

        try
        {
            return
                int.TryParse(
                    raw.ToString(),
                    out value
                )
                &&
                value > 0;
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private static void RefreshRuntimeContext(
        bool force
    )
    {
        if (_db == null || !_loaded)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;

        if (!force && now < _nextRuntimeRefreshUtc)
        {
            return;
        }

        _nextRuntimeRefreshUtc = now.AddMilliseconds(700);

        try
        {
            var gc = GameController.Inst;

            if (gc == null)
            {
                _runtimeNodes.Clear();
                _activeEventIds.Clear();
                _runtimeStatus = "当前不在可读取的游戏局内。";
                return;
            }

            // GetActiveEvents() 是大量后台监听事件，并不等于玩家正在进行的剧情。
            // GetViewRites() 对应当前地图/桌面真正显示出来的仪式。
            var viewRites = gc.GetViewRites();
            var currentRiteIds = new List<int>();

            if (viewRites != null)
            {
                foreach (var riteController in viewRites)
                {
                    try
                    {
                        if (
                            riteController == null
                        )
                        {
                            continue;
                        }

                        if (
                            TryGetRuntimeRiteId(
                                riteController,
                                out int riteId
                            )
                            &&
                            riteId > 0
                        )
                        {
                            currentRiteIds.Add(
                                riteId
                            );
                        }
                    }
                    catch
                    {
                    }
                }
            }

            currentRiteIds = currentRiteIds.Distinct().ToList();

            string newSignature =
                string.Join(",", currentRiteIds.OrderBy(x => x));

            bool changed =
                !string.Equals(
                    _activeSignature,
                    newSignature,
                    StringComparison.Ordinal
                );

            _activeSignature = newSignature;
            _activeEventIds.Clear();

            foreach (int riteId in currentRiteIds)
            {
                _activeEventIds.Add(riteId);
            }

            _runtimeNodes.Clear();

            foreach (int riteId in currentRiteIds)
            {
                var node = _db.Get(riteId);

                if (node == null)
                {
                    continue;
                }

                _runtimeNodes.Add(
                    new RuntimeNodeItem
                    {
                        Node = node,
                        Prefix = "● 当前可处理",
                        IsActive = true
                    }
                );
            }

            var seen = new HashSet<int>(currentRiteIds);
            var seenNames =
                new HashSet<string>(
                    StringComparer.Ordinal
                );

            foreach (var item in _runtimeNodes)
            {
                seenNames.Add(
                    item.Node.Kind
                    +
                    ":"
                    +
                    item.Node.Name
                );
            }

            foreach (int riteId in currentRiteIds)
            {
                var current = _db.Get(riteId);

                if (current == null)
                {
                    continue;
                }

                foreach (var link in current.Links)
                {
                    if (
                        link == null
                        ||
                        seen.Contains(link.TargetId)
                    )
                    {
                        continue;
                    }

                    var target = _db.Get(link.TargetId);

                    if (target == null)
                    {
                        continue;
                    }

                    string nameKey =
                        target.Kind
                        +
                        ":"
                        +
                        target.Name;

                    if (seenNames.Contains(nameKey))
                    {
                        continue;
                    }

                    seen.Add(link.TargetId);
                    seenNames.Add(nameKey);

                    _runtimeNodes.Add(
                        new RuntimeNodeItem
                        {
                            Node = target,
                            Prefix = "→ 直接后续",
                            IsActive = false
                        }
                    );
                }
            }

            _runtimeStatus =
                currentRiteIds.Count > 0
                    ?
                    $"当前有 {currentRiteIds.Count} 个地图/桌面仪式；这里只显示它们和直接后续。"
                    :
                    "当前没有检测到地图/桌面上的可见仪式。";

            if (
                _autoFollow
                &&
                currentRiteIds.Count > 0
                &&
                (
                    changed
                    ||
                    !_activeEventIds.Contains(_selectedId)
                )
            )
            {
                int first = currentRiteIds[0];

                if (_db.Get(first) != null)
                {
                    _selectedId = first;
                    _detailScroll = Vector2.zero;
                    _history.Clear();
                }
            }
        }
        catch (Exception ex)
        {
            _runtimeStatus =
                "读取当前地图/桌面仪式失败，已保留手动搜索模式。";

            Log.LogWarning(
                "RefreshRuntimeContext failed: "
                +
                ex.Message
            );
        }
    }

    private static void DrawPanel()
    {
        var oldGuiColor = GUI.color;
        GUI.color = Color.white;

        if (_panelTex != null)
        {
            GUI.DrawTexture(
                _panel,
                _panelTex,
                ScaleMode.StretchToFill,
                false
            );
        }

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
                330,
                22
            ),
            "v0.4.78 · 运行时ID兼容",
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

            SetInputBlockerVisible(false);
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

            SetInputBlockerVisible(false);
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

        // 左栏模式选择
        if (
            GUI.Button(
                new Rect(
                    x + 16,
                    y + 69,
                    108,
                    29
                ),
                "当前剧情",
                _leftMode == 0
                    ?
                    _activeButtonStyle
                    :
                    _wrapButton
            )
        )
        {
            _leftMode =
                0;

            RefreshRuntimeContext(
                force: true
            );
        }

        if (
            GUI.Button(
                new Rect(
                    x + 130,
                    y + 69,
                    108,
                    29
                ),
                "全部搜索",
                _leftMode == 1
                    ?
                    _activeButtonStyle
                    :
                    _wrapButton
            )
        )
        {
            _leftMode =
                1;
        }

        string followText =
            _autoFollow
                ?
                "自动跟随：开"
                :
                "自动跟随：关";

        if (
            GUI.Button(
                new Rect(
                    x + 246,
                    y + 69,
                    112,
                    29
                ),
                followText,
                _autoFollow
                    ?
                    _activeButtonStyle
                    :
                    _wrapButton
            )
        )
        {
            _autoFollow =
                !_autoFollow;

            if (_autoFollow)
            {
                RefreshRuntimeContext(
                    force: true
                );
            }
        }

        // 搜索仍保留，但只在“全部搜索”模式下作为主入口。
        GUI.Label(
            new Rect(
                x + 382,
                y + 73,
                42,
                24
            ),
            "搜索",
            _small
        );

        string newSearch =
            GUI.TextField(
                new Rect(
                    x + 426,
                    y + 69,
                    w - 520,
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

            if (
                !string.IsNullOrWhiteSpace(
                    _search
                )
            )
            {
                _leftMode =
                    1;
            }
        }

        if (
            GUI.Button(
                new Rect(
                    x + w - 88,
                    y + 69,
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
                285,
                Math.Min(
                    350,
                    w * 0.36f
                )
            );

        float splitX =
            x + leftW + 14;

        float contentY =
            y + 108;

        float contentH =
            h - 124;

        var leftPanelRect =
            new Rect(
                x + 12,
                contentY,
                leftW - 12,
                contentH
            );

        if (_softTex != null)
        {
            GUI.DrawTexture(
                leftPanelRect,
                _softTex,
                ScaleMode.StretchToFill,
                false
            );
        }

        GUI.Box(
            leftPanelRect,
            "",
            _softBoxStyle
        );

        if (
            _leftMode == 0
        )
        {
            GUI.Label(
                new Rect(
                    x + 24,
                    contentY + 10,
                    leftW - 36,
                    24
                ),
                "与你当前进度相关",
                _subTitle
            );

            GUI.Label(
                new Rect(
                    x + 24,
                    contentY + 34,
                    leftW - 36,
                    38
                ),
                _runtimeStatus,
                _small
            );

            DrawRuntimeResults(
                x + 20,
                contentY + 76,
                leftW - 28,
                contentH - 88
            );
        }
        else
        {
            GUI.Label(
                new Rect(
                    x + 24,
                    contentY + 10,
                    leftW - 36,
                    24
                ),
                $"全部剧情（{_results.Count}）",
                _subTitle
            );

            DrawSearchResults(
                x + 20,
                contentY + 42,
                leftW - 28,
                contentH - 54
            );
        }

        var rightPanelRect =
            new Rect(
                splitX,
                contentY,
                w - (splitX - x) - 12,
                contentH
            );

        if (_softTex != null)
        {
            GUI.DrawTexture(
                rightPanelRect,
                _softTex,
                ScaleMode.StretchToFill,
                false
            );
        }

        GUI.Box(
            rightPanelRect,
            "",
            _softBoxStyle
        );

        DrawDetails(
            splitX + 14,
            contentY + 12,
            w - (splitX - x) - 40,
            contentH - 24
        );

        GUI.color = oldGuiColor;
    }

    private static void DrawRuntimeResults(
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
                    60
                ),
                "攻略数据库尚未加载。",
                _body
            );

            return;
        }

        if (
            _runtimeNodes.Count == 0
        )
        {
            GUI.Label(
                new Rect(
                    x,
                    y,
                    w,
                    90
                ),
                "当前没有检测到活跃剧情。\n进入一局游戏后，这里会自动显示正在进行的事件和紧接着可能出现的后续。",
                _body
            );

            return;
        }

        float cy =
            y;

        int shown =
            0;

        foreach (
            var item
            in
            _runtimeNodes
        )
        {
            if (
                cy + 46
                >
                y + h
            )
            {
                break;
            }

            string marker =
                item.Node.Id
                ==
                _selectedId
                    ?
                    "▶ "
                    :
                    "";

            string label =
                $"{marker}{item.Prefix}  {item.Node.Name}";

            GUIStyle style =
                item.IsActive
                    ?
                    _activeButtonStyle!
                    :
                    _wrapButton!;

            if (
                GUI.Button(
                    new Rect(
                        x,
                        cy,
                        w,
                        40
                    ),
                    label,
                    style
                )
            )
            {
                NavigateTo(
                    item.Node.Id,
                    true
                );
            }

            cy +=
                44;

            shown++;
        }

        if (
            shown
            <
            _runtimeNodes.Count
        )
        {
            GUI.Label(
                new Rect(
                    x,
                    cy,
                    w,
                    28
                ),
                $"还有 {_runtimeNodes.Count - shown} 个相关节点，可点“全部搜索”查看。",
                _small
            );
        }
    }

    private static void DrawSearchResults(
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

        // 先估算整个右侧详情所需高度，
        // 再创建一个真正的垂直 ScrollView。
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
                58
            );

        float outcomeHeight =
            0f;

        if (
            !string.IsNullOrWhiteSpace(
                node.HumanOutcome
            )
        )
        {
            outcomeHeight =
                EstimateTextHeight(
                    node.HumanOutcome,
                    52
                );
        }

        string result =
            node.ResultText
            ??
            "";

        // 有滚动以后，不需要像以前那样过早截断。
        // 仍设置一个较高上限，避免极端配置让单个节点无限拉长。
        if (
            result.Length
            >
            3500
        )
        {
            result =
                result[..3500]
                +
                "\n……";
        }

        float resultHeight =
            0f;

        if (
            !string.IsNullOrWhiteSpace(
                result
            )
        )
        {
            resultHeight =
                EstimateTextHeight(
                    result,
                    70
                );
        }

        int linkCount =
            node.Links.Count;

        float contentHeight =
            62f                         // 标题
            +
            27f + conditionHeight + 16f
            +
            (
                outcomeHeight > 0
                    ?
                    27f + outcomeHeight + 16f
                    :
                    0f
            )
            +
            (
                resultHeight > 0
                    ?
                    27f + resultHeight + 16f
                    :
                    0f
            )
            +
            30f
            +
            Math.Max(
                1,
                linkCount
            )
            *
            50f
            +
            40f;

        float viewWidth =
            Math.Max(
                100f,
                w - 22f
            );

        float viewHeight =
            Math.Max(
                h,
                contentHeight
            );

        var scrollRect =
            new Rect(
                x,
                y,
                w,
                h
            );

        var viewRect =
            new Rect(
                0,
                0,
                viewWidth,
                viewHeight
            );

        _detailScroll =
            GUI.BeginScrollView(
                scrollRect,
                _detailScroll,
                viewRect
            );

        // ScrollView 内部使用相对坐标。
        float localX =
            6f;

        float localW =
            viewWidth
            -
            12f;

        float cy =
            4f;

        if (
            _history.Count > 0
            &&
            GUI.Button(
                new Rect(
                    localX,
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

            _detailScroll =
                Vector2.zero;

            GUI.EndScrollView();

            return;
        }

        string stateTag =
            _activeEventIds.Contains(
                node.Id
            )
                ?
                "【当前正在进行】 "
                :
                "";

        GUI.Label(
            new Rect(
                localX + 80,
                cy,
                localW - 80,
                38
            ),
            $"{stateTag}{KindName(node.Kind)} · {node.Name}",
            _title
        );

        cy +=
            50f;

        // =========================
        // 怎么触发
        // =========================
        GUI.Label(
            new Rect(
                localX,
                cy,
                localW,
                24
            ),
            "怎么触发？",
            _subTitle
        );

        cy +=
            27f;

        GUI.Box(
            new Rect(
                localX,
                cy,
                localW,
                conditionHeight
            ),
            ""
        );

        GUI.Label(
            new Rect(
                localX + 9,
                cy + 7,
                localW - 18,
                conditionHeight - 14
            ),
            condition,
            _body
        );

        cy +=
            conditionHeight
            +
            14f;

        // =========================
        // 接下来会怎样
        // =========================
        if (
            outcomeHeight > 0
        )
        {
            GUI.Label(
                new Rect(
                    localX,
                    cy,
                    localW,
                    24
                ),
                "接下来会怎样？",
                _subTitle
            );

            cy +=
                27f;

            GUI.Box(
                new Rect(
                    localX,
                    cy,
                    localW,
                    outcomeHeight
                ),
                ""
            );

            GUI.Label(
                new Rect(
                    localX + 9,
                    cy + 7,
                    localW - 18,
                    outcomeHeight - 14
                ),
                node.HumanOutcome,
                _body
            );

            cy +=
                outcomeHeight
                +
                14f;
        }

        // =========================
        // 结局说明
        // =========================
        if (
            resultHeight > 0
        )
        {
            GUI.Label(
                new Rect(
                    localX,
                    cy,
                    localW,
                    24
                ),
                "结局说明",
                _subTitle
            );

            cy +=
                27f;

            GUI.Box(
                new Rect(
                    localX,
                    cy,
                    localW,
                    resultHeight
                ),
                ""
            );

            GUI.Label(
                new Rect(
                    localX + 9,
                    cy + 7,
                    localW - 18,
                    resultHeight - 14
                ),
                result,
                _body
            );

            cy +=
                resultHeight
                +
                14f;
        }

        // =========================
        // 后续分支
        // =========================
        GUI.Label(
            new Rect(
                localX,
                cy,
                localW,
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
            27f;

        if (
            node.Links.Count == 0
        )
        {
            GUI.Label(
                new Rect(
                    localX,
                    cy,
                    localW,
                    46
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
        }
        else
        {
            // 有了滚动以后，不再只显示前 6 条。
            // 所有后续分支都可以在右侧滚动查看。
            foreach (
                var link
                in
                node.Links
            )
            {
                string label =
                    _db.DescribeTransition(
                        link
                    );

                if (
                    GUI.Button(
                        new Rect(
                            localX,
                            cy,
                            localW,
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
                    48f;
            }
        }

        GUI.EndScrollView();
    }

    private static float EstimateTextHeight(
        string text,
        float minimum
    )
    {
        if (
            string.IsNullOrEmpty(
                text
            )
        )
        {
            return minimum;
        }

        // 这里故意“略微高估”中文换行高度。
        // 右栏宽度大约 500~560px，13px 中文字体实际一行通常能放 30 多字；
        // 按 27 字一行估算，可以确保文本框宁可多留一点空白，也绝不和下一块内容重叠。
        const int CharactersPerVisualLine = 27;
        const float LineHeight = 20f;
        const float VerticalPadding = 20f;

        int visualLines = 0;

        string normalized =
            text.Replace(
                "\r\n",
                "\n"
            )
            .Replace(
                '\r',
                '\n'
            );

        string[] logicalLines =
            normalized.Split(
                '\n'
            );

        foreach (
            string line
            in
            logicalLines
        )
        {
            int length =
                Math.Max(
                    1,
                    line.Length
                );

            visualLines +=
                Math.Max(
                    1,
                    (
                        length
                        +
                        CharactersPerVisualLine
                        -
                        1
                    )
                    /
                    CharactersPerVisualLine
                );
        }

        float height =
            visualLines
            *
            LineHeight
            +
            VerticalPadding;

        return
            Math.Max(
                minimum,
                height
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

        _detailScroll =
            Vector2.zero;
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

            UpdateInputBlockerRect(
                new Rect(
                    0f,
                    0f,
                    Screen.width,
                    Screen.height
                )
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
            &&
            _dragging
        )
        {
            _dragging =
                false;

            UpdateInputBlockerRect(
                _panel
            );

            e.Use();
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

            // 完全不透明
            _panelTex.SetPixel(
                0,
                0,
                new Color(
                    0.055f,
                    0.075f,
                    0.105f,
                    1.00f
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

            // 内部区域也不透明
            _softTex.SetPixel(
                0,
                0,
                new Color(
                    0.085f,
                    0.115f,
                    0.150f,
                    1.00f
                )
            );

            _softTex.Apply();
        }

        if (
            _activeTex
            ==
            null
        )
        {
            _activeTex =
                new Texture2D(
                    1,
                    1
                );

            _activeTex.SetPixel(
                0,
                0,
                new Color(
                    0.125f,
                    0.245f,
                    0.335f,
                    1.00f
                )
            );

            _activeTex.Apply();
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

            _wrapButton.padding =
                new RectOffset(
                    8,
                    8,
                    4,
                    4
                );

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

        if (
            _activeButtonStyle
            ==
            null
        )
        {
            _activeButtonStyle =
                new GUIStyle();

            _activeButtonStyle.fontSize =
                12;

            _activeButtonStyle.fontStyle =
                FontStyle.Bold;

            _activeButtonStyle.wordWrap =
                true;

            _activeButtonStyle.alignment =
                TextAnchor.MiddleLeft;

            _activeButtonStyle.padding =
                new RectOffset(
                    8,
                    8,
                    4,
                    4
                );

            _activeButtonStyle
                .normal
                .background =
                    _activeTex;

            _activeButtonStyle
                .hover
                .background =
                    _activeTex;

            _activeButtonStyle
                .active
                .background =
                    _activeTex;

            _activeButtonStyle
                .normal
                .textColor =
                    Color.white;

            _activeButtonStyle
                .hover
                .textColor =
                    Color.white;

            _activeButtonStyle
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
