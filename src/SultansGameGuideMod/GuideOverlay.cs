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
        public int RuntimeUid { get; init; }
        public bool IsCurrent { get; init; }
        public bool IsStarted { get; init; }
    }

    private sealed class RuntimeRiteState
    {
        public int Id { get; init; }
        public int Uid { get; init; }
        public bool IsStarted { get; init; }
        public bool IsCurrent { get; init; }
    }

    private static GuideDatabase? _db;

    private static bool _visible = true;
    private static bool _minimized = false;
    private static bool _loaded = false;

    private static string _loadMessage = "正在读取游戏攻略数据……";

    // 左栏：0 = 当前地图仪式；1 = 全部搜索
    private static int _leftMode = 0;
    private static bool _autoFollow = true;

    private static string _search = "";
    private static string _lastSearch = "\u0000";
    private static List<GuideNode> _results = new();

    private static readonly List<RuntimeNodeItem> _runtimeNodes = new();
    private static readonly HashSet<int> _runtimeRiteIds = new();
    private static readonly HashSet<int> _startedRiteIds = new();
    private static string _runtimeStatus = "正在读取地图仪式……";
    private static DateTime _nextRuntimeRefreshUtc = DateTime.MinValue;
    private static string _runtimeSignature = "";
    private static int _currentRiteId = 0;
    private static int _currentRiteUid = 0;

    private static int _resultPage = 0;
    private static int _selectedId = 0;

    private static readonly Stack<int> _history = new();

    // 关系图分支默认折叠。
    private static readonly HashSet<string> _expandedIncomingRelations =
        new();

    private static readonly HashSet<string> _expandedOutgoingRelations =
        new();

    // 左侧“当前仪式”列表滚动位置
    private static Vector2 _runtimeScroll = Vector2.zero;

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
    private static GUIStyle? _triggerTitleStyle;
    private static GUIStyle? _branchHeaderLineStyle;
    private static GUIStyle? _activeButtonStyle;
    private static GUIStyle? _selectedButtonStyle;

    private static Texture2D? _panelTex;
    private static Texture2D? _softTex;
    private static Texture2D? _triggerGroupTex;
    private static Texture2D? _triggerBorderTex;
    private static Texture2D? _branchBorderTex;
    private static Texture2D? _activeTex;
    private static Texture2D? _selectedTex;

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
    // 运行时：扫描当前场景中的真实 RiteController 并构造“当前仪式”左栏
    // ============================================================

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RefreshRuntimeContext(
        bool force
    )
    {
        if (
            _db == null
            ||
            !_loaded
        )
        {
            return;
        }

        DateTime now =
            DateTime.UtcNow;

        if (
            !force
            &&
            now
            <
            _nextRuntimeRefreshUtc
        )
        {
            return;
        }

        _nextRuntimeRefreshUtc =
            now.AddMilliseconds(
                700
            );

        try
        {
            var gc =
                GameController.Inst;

            if (
                gc == null
            )
            {
                _runtimeNodes.Clear();
                _runtimeRiteIds.Clear();
                _startedRiteIds.Clear();

                _currentRiteId =
                    0;

                _currentRiteUid =
                    0;

                _runtimeStatus =
                    "当前不在可读取的游戏局内。";

                return;
            }

            int currentRiteId =
                0;

            int currentRiteUid =
                0;

            try
            {
                var panel =
                    gc.ritePanel;

                if (
                    panel != null
                    &&
                    panel.isActiveAndEnabled
                    &&
                    panel.tmpRite != null
                )
                {
                    currentRiteId =
                        panel.tmpRite.id;

                    currentRiteUid =
                        panel.tmpRite.uid;
                }
            }
            catch
            {
                currentRiteId =
                    0;

                currentRiteUid =
                    0;
            }

            var states =
                new List<RuntimeRiteState>();

            var seenInstances =
                new HashSet<string>();

            var controllers =
                Resources.FindObjectsOfTypeAll<RiteController>();

            foreach (
                var controller
                in
                controllers
            )
            {
                try
                {
                    if (
                        controller == null
                    )
                    {
                        continue;
                    }

                    var go =
                        controller.gameObject;

                    if (
                        go == null
                        ||
                        !go.scene.IsValid()
                        ||
                        !go.activeInHierarchy
                        ||
                        !controller.isActiveAndEnabled
                    )
                    {
                        continue;
                    }

                    var rite =
                        controller.rite;

                    if (
                        rite == null
                        ||
                        rite.id <= 0
                    )
                    {
                        continue;
                    }

                    string instanceKey =
                        rite.uid > 0
                            ?
                            "uid:"
                            +
                            rite.uid
                            :
                            "instance:"
                            +
                            controller.GetInstanceID();

                    if (
                        !seenInstances.Add(
                            instanceKey
                        )
                    )
                    {
                        continue;
                    }

                    bool isCurrent =
                        currentRiteId > 0
                        &&
                        rite.id
                        ==
                        currentRiteId
                        &&
                        (
                            currentRiteUid <= 0
                            ||
                            rite.uid
                            ==
                            currentRiteUid
                        );

                    states.Add(
                        new RuntimeRiteState
                        {
                            Id =
                                rite.id,

                            Uid =
                                rite.uid,

                            IsStarted =
                                rite.start,

                            IsCurrent =
                                isCurrent
                        }
                    );
                }
                catch
                {
                }
            }

            states =
                states
                    .OrderByDescending(
                        x =>
                            x.IsCurrent
                    )
                    .ThenBy(
                        x =>
                            x.IsStarted
                    )
                    .ThenBy(
                        x =>
                            x.Id
                    )
                    .ThenBy(
                        x =>
                            x.Uid
                    )
                    .ToList();

            string newSignature =
                string.Join(
                    "|",
                    states.Select(
                        x =>
                            x.Id
                            +
                            ":"
                            +
                            x.Uid
                            +
                            ":"
                            +
                            (
                                x.IsStarted
                                    ?
                                    "1"
                                    :
                                    "0"
                            )
                            +
                            ":"
                            +
                            (
                                x.IsCurrent
                                    ?
                                    "1"
                                    :
                                    "0"
                            )
                    )
                );

            bool changed =
                !string.Equals(
                    _runtimeSignature,
                    newSignature,
                    StringComparison.Ordinal
                );

            _runtimeSignature =
                newSignature;

            _currentRiteId =
                currentRiteId;

            _currentRiteUid =
                currentRiteUid;

            _runtimeNodes.Clear();
            _runtimeRiteIds.Clear();
            _startedRiteIds.Clear();

            int matchedCount =
                0;

            int playableCount =
                0;

            int startedCount =
                0;

            foreach (
                var state
                in
                states
            )
            {
                _runtimeRiteIds.Add(
                    state.Id
                );

                if (
                    state.IsStarted
                )
                {
                    _startedRiteIds.Add(
                        state.Id
                    );

                    startedCount++;
                }
                else
                {
                    playableCount++;
                }

                var node =
                    _db.Get(
                        state.Id
                    );

                if (
                    node == null
                )
                {
                    continue;
                }

                matchedCount++;

                string prefix =
                    state.IsCurrent
                        ?
                        "◆ 正在操作"
                        :
                        (
                            state.IsStarted
                                ?
                                "● 已开始"
                                :
                                "○ 可操作"
                        );

                _runtimeNodes.Add(
                    new RuntimeNodeItem
                    {
                        Node =
                            node,

                        Prefix =
                            prefix,

                        RuntimeUid =
                            state.Uid,

                        IsCurrent =
                            state.IsCurrent,

                        IsStarted =
                            state.IsStarted
                    }
                );
            }

            if (
                states.Count == 0
            )
            {
                _runtimeStatus =
                    "当前地图没有检测到仪式。";
            }
            else
            {
                _runtimeStatus =
                    $"地图仪式 {states.Count} 个：可操作 {playableCount} 个，已开始 {startedCount} 个。";

                if (
                    matchedCount
                    <
                    states.Count
                )
                {
                    _runtimeStatus +=
                        $" 其中 {states.Count - matchedCount} 个暂未匹配到攻略配置。";
                }
            }

            if (
                _autoFollow
                &&
                states.Count > 0
            )
            {
                int targetId =
                    0;

                if (
                    currentRiteId > 0
                    &&
                    _db.Get(
                        currentRiteId
                    )
                    !=
                    null
                )
                {
                    targetId =
                        currentRiteId;
                }
                else if (
                    changed
                    ||
                    !_runtimeRiteIds.Contains(
                        _selectedId
                    )
                )
                {
                    var firstPlayable =
                        states.FirstOrDefault(
                            x =>
                                !x.IsStarted
                                &&
                                _db.Get(
                                    x.Id
                                )
                                !=
                                null
                        );

                    if (
                        firstPlayable != null
                    )
                    {
                        targetId =
                            firstPlayable.Id;
                    }
                    else
                    {
                        var firstMatched =
                            states.FirstOrDefault(
                                x =>
                                    _db.Get(
                                        x.Id
                                    )
                                    !=
                                    null
                            );

                        if (
                            firstMatched != null
                        )
                        {
                            targetId =
                                firstMatched.Id;
                        }
                    }
                }

                if (
                    targetId > 0
                    &&
                    targetId
                    !=
                    _selectedId
                )
                {
                    _selectedId =
                        targetId;

                    _detailScroll =
                        Vector2.zero;

                    _history.Clear();
                }
            }
        }
        catch (
            Exception ex
        )
        {
            _runtimeStatus =
                "读取地图仪式失败，已保留手动搜索模式。";

            Log.LogWarning(
                "RefreshRuntimeContext failed: "
                +
                ex.Message
            );
        }
    }

    // ============================================================
    // UI
    // ============================================================

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
            "v0.5.2 · 早期错误审计清理",
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
                "当前仪式",
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

            _runtimeScroll =
                Vector2.zero;

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
                "当前没有检测到地图仪式。\n进入一局游戏后，这里会自动显示地图上真实存在的仪式。",
                _body
            );

            return;
        }

        const float rowH = 44f;
        const float bottomPadding = 16f;

        float contentHeight =
            Math.Max(
                h,
                _runtimeNodes.Count
                *
                rowH
                +
                bottomPadding
            );

        float viewWidth =
            Math.Max(
                80f,
                w - 18f
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
                0f,
                0f,
                viewWidth,
                contentHeight
            );

        _runtimeScroll =
            GUI.BeginScrollView(
                scrollRect,
                _runtimeScroll,
                viewRect
            );

        float cy =
            0f;

        foreach (
            var item
            in
            _runtimeNodes
        )
        {
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
                item.Node.Id
                ==
                _selectedId
                    ?
                    _selectedButtonStyle!
                    :
                    (
                        item.IsCurrent
                        ||
                        !item.IsStarted
                            ?
                            _activeButtonStyle!
                            :
                            _wrapButton!
                    );

            if (
                GUI.Button(
                    new Rect(
                        0f,
                        cy,
                        viewWidth,
                        40f
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
                rowH;
        }

        GUI.EndScrollView();
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

            GUIStyle searchStyle =
                node.Id
                ==
                _selectedId
                    ?
                    _selectedButtonStyle!
                    :
                    _wrapButton!;

            if (
                GUI.Button(
                    new Rect(
                        x,
                        currentY,
                        w,
                        rowH - 4
                    ),
                    label,
                    searchStyle
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

        if (
            node
            ==
            null
        )
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

        string result =
            node.ResultText
            ??
            "";

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

        float incomingHeight =
            EstimateRelationSectionHeight(
                node,
                node.IncomingRelations,
                true
            );

        float outgoingHeight =
            EstimateRelationSectionHeight(
                node,
                node.OutgoingRelations,
                false
            );

        float contentHeight =
            62f
            +
            incomingHeight
            +
            12f
            +
            outgoingHeight
            +
            (
                resultHeight > 0
                    ?
                    27f
                    +
                    resultHeight
                    +
                    18f
                    :
                    0f
            )
            +
            70f;

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

        GUI.Label(
            new Rect(
                localX + 80,
                cy,
                localW - 80,
                38
            ),
            $"{KindName(node.Kind)} · {node.Name}",
            _title
        );

        cy +=
            50f;

        DrawRelationSection(
            node,
            node.IncomingRelations,
            true,
            "触发机制",
            localX,
            ref cy,
            localW
        );

        cy +=
            12f;

        DrawRelationSection(
            node,
            node.OutgoingRelations,
            false,
            node.Kind
            ==
            NodeKind.Rite
                ?
                "仪式走向"
                :
                (
                    node.Kind
                    ==
                    NodeKind.Event
                        ?
                        "事件走向"
                        :
                        "后续走向"
                ),
            localX,
            ref cy,
            localW
        );

        if (
            resultHeight > 0
        )
        {
            cy +=
                14f;

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

        GUI.EndScrollView();
    }

    private static void DrawRelationSection(
        GuideNode currentNode,
        List<GuideRelationBranch> branches,
        bool incoming,
        string title,
        float x,
        ref float cy,
        float w
    )
    {
        GUI.Label(
            new Rect(
                x,
                cy,
                w,
                24f
            ),
            title,
            _triggerTitleStyle
        );

        cy +=
            32f;

        float groupHeight =
            EstimateRelationGroupHeight(
                currentNode,
                branches,
                incoming
            );

        float groupY =
            cy;

        DrawTriggerGroupFrame(
            new Rect(
                x,
                groupY,
                w,
                groupHeight
            )
        );

        float innerX =
            x + 10f;

        float innerW =
            w - 20f;

        cy +=
            10f;

        if (
            branches.Count
            ==
            0
        )
        {
            GUI.Label(
                new Rect(
                    innerX,
                    cy,
                    innerW,
                    42f
                ),
                incoming
                    ?
                    "未找到明确的事件 / 仪式上游来源。"
                    :
                    "未找到明确的后续事件 / 仪式。",
                _body
            );

            cy +=
                42f;
        }
        else
        {
            for (
                int i = 0;
                i < branches.Count;
                i++
            )
            {
                var branch =
                    branches[i];

                string key =
                    BuildRelationBranchKey(
                        currentNode,
                        branch,
                        incoming
                    );

                var expandedSet =
                    incoming
                        ?
                        _expandedIncomingRelations
                        :
                        _expandedOutgoingRelations;

                bool expanded =
                    expandedSet.Contains(
                        key
                    );

                float branchStartY =
                    cy;

                float branchHeight =
                    EstimateRelationBranchHeight(
                        currentNode,
                        branch,
                        incoming,
                        expanded
                    );

                DrawTriggerBranchFrame(
                    new Rect(
                        innerX,
                        branchStartY,
                        innerW,
                        branchHeight
                    )
                );

                var buttonRect =
                    new Rect(
                        innerX + 6f,
                        branchStartY + 6f,
                        innerW - 12f,
                        36f
                    );

                if (
                    GUI.Button(
                        buttonRect,
                        "",
                        _wrapButton
                    )
                )
                {
                    if (
                        expanded
                    )
                    {
                        expandedSet.Remove(
                            key
                        );
                    }
                    else
                    {
                        expandedSet.Add(
                            key
                        );
                    }
                }

                string arrow =
                    expanded
                        ?
                        "▼"
                        :
                        "▶";

                string header =
                    arrow
                    +
                    "  "
                    +
                    RelationKindName(
                        branch.NodeKind
                    )
                    +
                    " · "
                    +
                    EscapeRichTextDisplay(
                        branch.NodeName
                    );

                GUI.Label(
                    buttonRect,
                    header,
                    _branchHeaderLineStyle
                );

                cy =
                    branchStartY
                    +
                    48f;

                if (
                    expanded
                )
                {
                    DrawExpandedRelationBranch(
                        currentNode,
                        branch,
                        incoming,
                        innerX + 12f,
                        ref cy,
                        innerW - 24f
                    );
                }

                cy =
                    branchStartY
                    +
                    branchHeight
                    +
                    8f;
            }
        }

        cy =
            groupY
            +
            groupHeight;
    }

    private static float EstimateRelationSectionHeight(
        GuideNode currentNode,
        List<GuideRelationBranch> branches,
        bool incoming
    )
    {
        return
            32f
            +
            EstimateRelationGroupHeight(
                currentNode,
                branches,
                incoming
            );
    }

    private static float EstimateRelationGroupHeight(
        GuideNode currentNode,
        List<GuideRelationBranch> branches,
        bool incoming
    )
    {
        float height =
            20f;

        if (
            branches.Count
            ==
            0
        )
        {
            return
                height
                +
                42f;
        }

        foreach (
            var branch
            in
            branches
        )
        {
            string key =
                BuildRelationBranchKey(
                    currentNode,
                    branch,
                    incoming
                );

            bool expanded =
                (
                    incoming
                        ?
                        _expandedIncomingRelations
                        :
                        _expandedOutgoingRelations
                )
                    .Contains(
                        key
                    );

            height +=
                EstimateRelationBranchHeight(
                    currentNode,
                    branch,
                    incoming,
                    expanded
                )
                +
                8f;
        }

        return
            height;
    }

    private static float EstimateRelationBranchHeight(
        GuideNode currentNode,
        GuideRelationBranch branch,
        bool incoming,
        bool expanded
    )
    {
        float height =
            54f;

        if (
            !expanded
        )
        {
            return
                height;
        }

        for (
            int i = 0;
            i < branch.Paths.Count;
            i++
        )
        {
            string pathText =
                BuildRelationPathText(
                    currentNode,
                    branch,
                    branch.Paths[i],
                    incoming,
                    branch.Paths.Count,
                    i
                );

            height +=
                EstimateTextHeight(
                    pathText,
                    54f
                )
                +
                10f;
        }

        // “可以继续看”已经合并到关系分支里：
        // 展开后直接提供查看来源 / 查看后续按钮。
        height +=
            38f;

        return
            height;
    }

    private static void DrawExpandedRelationBranch(
        GuideNode currentNode,
        GuideRelationBranch branch,
        bool incoming,
        float x,
        ref float cy,
        float w
    )
    {
        for (
            int i = 0;
            i < branch.Paths.Count;
            i++
        )
        {
            string pathText =
                BuildRelationPathText(
                    currentNode,
                    branch,
                    branch.Paths[i],
                    incoming,
                    branch.Paths.Count,
                    i
                );

            float textHeight =
                EstimateTextHeight(
                    pathText,
                    54f
                );

            GUI.Label(
                new Rect(
                    x,
                    cy,
                    w,
                    textHeight
                ),
                pathText,
                _body
            );

            cy +=
                textHeight
                +
                10f;
        }

        string buttonText =
            incoming
                ?
                "查看来源 →"
                :
                "查看后续 →";

        if (
            GUI.Button(
                new Rect(
                    x,
                    cy,
                    112f,
                    28f
                ),
                buttonText
            )
        )
        {
            NavigateTo(
                branch.NodeId,
                true
            );
        }

        cy +=
            34f;
    }

    private static string BuildRelationPathText(
        GuideNode currentNode,
        GuideRelationBranch branch,
        GuideRelationPath path,
        bool incoming,
        int pathCount,
        int index
    )
    {
        var lines =
            new List<string>();

        if (
            pathCount > 1
        )
        {
            lines.Add(
                $"路径 {index + 1}"
            );
        }

        if (
            incoming
            &&
            !string.IsNullOrWhiteSpace(
                path.Timing
            )
        )
        {
            lines.Add(
                path.Timing
            );
        }

        if (
            !string.IsNullOrWhiteSpace(
                path.Context
            )
        )
        {
            lines.Add(
                $"分支：{path.Context}"
            );
        }

        if (
            !string.IsNullOrWhiteSpace(
                path.ActionText
            )
        )
        {
            lines.Add(
                path.ActionText
            );
        }

        if (
            lines.Count
            ==
            0
        )
        {
            lines.Add(
                incoming
                    ?
                    $"{RelationKindName(branch.NodeKind)}「{branch.NodeName}」会产生当前节点。"
                    :
                    $"当前节点会产生{RelationKindName(branch.NodeKind)}「{branch.NodeName}」。"
            );
        }

        return
            string.Join(
                "\n",
                lines
            );
    }

    private static string BuildRelationBranchKey(
        GuideNode currentNode,
        GuideRelationBranch branch,
        bool incoming
    )
    {
        return
            (
                incoming
                    ?
                    "IN"
                    :
                    "OUT"
            )
            +
            ":"
            +
            currentNode.Id
            +
            ":"
            +
            currentNode.Kind
            +
            ":"
            +
            branch.NodeId
            +
            ":"
            +
            branch.NodeKind;
    }

    private static string RelationKindName(
        NodeKind kind
    )
    {
        return
            kind switch
            {
                NodeKind.Rite =>
                    "仪式",

                NodeKind.Event =>
                    "事件",

                _ =>
                    "后日谈"
            };
    }

    private static void DrawTriggerGroupFrame(
        Rect rect
    )
    {
        // 右侧详情面板本身已经使用 _softTex；
        // 如果这里只画同样的 _softTex，视觉上等于“没有框”。
        // 所以这里显式画一层亮边框，再内缩画独立底色。
        if (
            _triggerBorderTex != null
        )
        {
            GUI.DrawTexture(
                rect,
                _triggerBorderTex,
                ScaleMode.StretchToFill,
                false
            );
        }

        if (
            _triggerGroupTex != null
        )
        {
            GUI.DrawTexture(
                new Rect(
                    rect.x + 2f,
                    rect.y + 2f,
                    Math.Max(
                        0f,
                        rect.width - 4f
                    ),
                    Math.Max(
                        0f,
                        rect.height - 4f
                    )
                ),
                _triggerGroupTex,
                ScaleMode.StretchToFill,
                false
            );
        }
    }

    private static void DrawTriggerBranchFrame(
        Rect rect
    )
    {
        if (
            _branchBorderTex != null
        )
        {
            GUI.DrawTexture(
                rect,
                _branchBorderTex,
                ScaleMode.StretchToFill,
                false
            );
        }

        if (
            _softTex != null
        )
        {
            GUI.DrawTexture(
                new Rect(
                    rect.x + 1f,
                    rect.y + 1f,
                    Math.Max(
                        0f,
                        rect.width - 2f
                    ),
                    Math.Max(
                        0f,
                        rect.height - 2f
                    )
                ),
                _softTex,
                ScaleMode.StretchToFill,
                false
            );
        }
    }

    private static string EscapeRichTextDisplay(
        string? text
    )
    {
        if (
            string.IsNullOrEmpty(
                text
            )
        )
        {
            return
                "";
        }

        // Unity IMGUI richText 对 <...> 会按标签解析。
        // 分支名如果恰好带尖括号，就替换成全角字符，避免误吞文字。
        return
            text
                .Replace(
                    "<",
                    "＜",
                    StringComparison.Ordinal
                )
                .Replace(
                    ">",
                    "＞",
                    StringComparison.Ordinal
                );
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
            _triggerGroupTex
            ==
            null
        )
        {
            _triggerGroupTex =
                new Texture2D(
                    1,
                    1
                );

            _triggerGroupTex.SetPixel(
                0,
                0,
                new Color(
                    0.065f,
                    0.090f,
                    0.120f,
                    1.00f
                )
            );

            _triggerGroupTex.Apply();
        }

        if (
            _triggerBorderTex
            ==
            null
        )
        {
            _triggerBorderTex =
                new Texture2D(
                    1,
                    1
                );

            _triggerBorderTex.SetPixel(
                0,
                0,
                new Color(
                    0.22f,
                    0.40f,
                    0.53f,
                    1.00f
                )
            );

            _triggerBorderTex.Apply();
        }

        if (
            _branchBorderTex
            ==
            null
        )
        {
            _branchBorderTex =
                new Texture2D(
                    1,
                    1
                );

            _branchBorderTex.SetPixel(
                0,
                0,
                new Color(
                    0.16f,
                    0.28f,
                    0.36f,
                    1.00f
                )
            );

            _branchBorderTex.Apply();
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
            _selectedTex
            ==
            null
        )
        {
            _selectedTex =
                new Texture2D(
                    1,
                    1
                );

            // 选中项使用明显的暖金棕色，与“当前可操作”的蓝色区分。
            _selectedTex.SetPixel(
                0,
                0,
                new Color(
                    0.48f,
                    0.27f,
                    0.07f,
                    1.00f
                )
            );

            _selectedTex.Apply();
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
            _triggerTitleStyle
            ==
            null
        )
        {
            _triggerTitleStyle =
                new GUIStyle();

            _triggerTitleStyle.fontSize =
                15;

            _triggerTitleStyle.fontStyle =
                FontStyle.Bold;

            _triggerTitleStyle
                .normal
                .textColor =
                    new Color(
                        0.70f,
                        0.88f,
                        1.00f,
                        1f
                    );
        }

        if (
            _branchHeaderLineStyle
            ==
            null
        )
        {
            _branchHeaderLineStyle =
                new GUIStyle();

            _branchHeaderLineStyle.fontSize =
                13;

            _branchHeaderLineStyle.fontStyle =
                FontStyle.Normal;

            _branchHeaderLineStyle.alignment =
                TextAnchor.MiddleLeft;

            _branchHeaderLineStyle.richText =
                true;

            _branchHeaderLineStyle.padding =
                new RectOffset(
                    8,
                    8,
                    0,
                    0
                );

            _branchHeaderLineStyle
                .normal
                .textColor =
                    Color.white;
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

        if (
            _selectedButtonStyle
            ==
            null
        )
        {
            _selectedButtonStyle =
                new GUIStyle();

            _selectedButtonStyle.fontSize =
                12;

            _selectedButtonStyle.fontStyle =
                FontStyle.Bold;

            _selectedButtonStyle.wordWrap =
                true;

            _selectedButtonStyle.alignment =
                TextAnchor.MiddleLeft;

            _selectedButtonStyle.padding =
                new RectOffset(
                    8,
                    8,
                    4,
                    4
                );

            _selectedButtonStyle
                .normal
                .background =
                    _selectedTex;

            _selectedButtonStyle
                .hover
                .background =
                    _selectedTex;

            _selectedButtonStyle
                .active
                .background =
                    _selectedTex;

            _selectedButtonStyle
                .normal
                .textColor =
                    new Color(
                        1.00f,
                        0.93f,
                        0.72f,
                        1.00f
                    );

            _selectedButtonStyle
                .hover
                .textColor =
                    Color.white;

            _selectedButtonStyle
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
