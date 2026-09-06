using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private enum ConditionRuntimeState
    {
        Unknown,
        Met,
        Unmet
    }

    private sealed class ConditionCheckRow
    {
        public ConditionRuntimeState State { get; init; }
        public string Text { get; init; } = "";
        public string Detail { get; init; } = "";
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

    // “触发机制”分支默认折叠；点击分支名后展开。
    private static readonly HashSet<string> _expandedTriggerBranches =
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
    private static GUIStyle? _statusTitleStyle;
    private static GUIStyle? _stateIconSymbolStyle;
    private static GUIStyle? _activeButtonStyle;
    private static GUIStyle? _selectedButtonStyle;

    private static Texture2D? _panelTex;
    private static Texture2D? _softTex;
    private static Texture2D? _triggerGroupTex;
    private static Texture2D? _triggerBorderTex;
    private static Texture2D? _branchBorderTex;
    private static Texture2D? _stateMetCircleTex;
    private static Texture2D? _stateUnmetCircleTex;
    private static Texture2D? _stateUnknownCircleTex;
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
            "v0.4.98 · 状态图标对齐精简",
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

        float triggerHeight =
            EstimateTriggerMechanismHeight(
                node
            );

        float contentHeight =
            62f
            +
            triggerHeight
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
            60f;

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

        string stateTag =
            node.Id
            ==
            _currentRiteId
                ?
                "【正在操作】 "
                :
                (
                    _runtimeRiteIds.Contains(
                        node.Id
                    )
                        ?
                        (
                            _startedRiteIds.Contains(
                                node.Id
                            )
                                ?
                                "【地图·已开始】 "
                                :
                                "【地图·可操作】 "
                        )
                        :
                        ""
                );

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
        // 触发机制
        // =========================
        GUI.Label(
            new Rect(
                localX,
                cy,
                localW,
                24
            ),
            "触发机制",
            _triggerTitleStyle
        );

        cy +=
            32f;

        // 用一个完整的大框把当前节点的所有触发分支包起来。
        // 分支仍然保持默认折叠；展开后的条件详情继续显示在框内。
        float triggerGroupHeight =
            EstimateTriggerBranchGroupHeight(
                node
            );

        float triggerGroupY =
            cy;

        DrawTriggerGroupFrame(
            new Rect(
                localX,
                triggerGroupY,
                localW,
                triggerGroupHeight
            )
        );

        float triggerInnerX =
            localX + 10f;

        float triggerInnerW =
            localW - 20f;

        cy +=
            10f;

        if (
            node.TriggerBranches.Count
            ==
            0
        )
        {
            GUI.Label(
                new Rect(
                    triggerInnerX,
                    cy,
                    triggerInnerW,
                    44
                ),
                "没有解析到可展示的触发分支。",
                _body
            );

            cy +=
                44f;
        }
        else
        {
            for (
                int i = 0;
                i
                <
                node.TriggerBranches.Count;
                i++
            )
            {
                var branch =
                    node.TriggerBranches[i];

                string branchKey =
                    BuildTriggerBranchKey(
                        node,
                        branch,
                        i
                    );

                bool expanded =
                    _expandedTriggerBranches.Contains(
                        branchKey
                    );

                var state =
                    EvaluateTriggerBranch(
                        branch,
                        out var rows
                    );

                float branchStartY =
                    cy;

                float branchHeight =
                    EstimateTriggerBranchContainerHeight(
                        branch,
                        rows,
                        expanded
                    );

                DrawTriggerBranchFrame(
                    new Rect(
                        triggerInnerX,
                        branchStartY,
                        triggerInnerW,
                        branchHeight
                    )
                );

                float headerX =
                    triggerInnerX + 6f;

                float headerY =
                    branchStartY + 6f;

                float headerW =
                    triggerInnerW - 12f;

                var buttonRect =
                    new Rect(
                        headerX,
                        headerY,
                        headerW,
                        36f
                    );

                // 用空文字按钮只负责点击/悬停。
                // 箭头、状态图标、分支名分开绘制，彻底避免文字与图标重叠。
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
                        _expandedTriggerBranches.Remove(
                            branchKey
                        );
                    }
                    else
                    {
                        _expandedTriggerBranches.Add(
                            branchKey
                        );
                    }
                }

                string arrow =
                    expanded
                        ?
                        "▼"
                        :
                        "▶";

                GUI.Label(
                    new Rect(
                        headerX + 8f,
                        headerY + 6f,
                        18f,
                        24f
                    ),
                    arrow,
                    _body
                );

                GUI.Label(
                    new Rect(
                        headerX + 32f,
                        headerY + 4f,
                        Math.Max(
                            40f,
                            headerW - 40f
                        ),
                        28f
                    ),
                    branch.Name,
                    _body
                );

                cy =
                    branchStartY
                    +
                    48f;

                if (
                    expanded
                )
                {
                    DrawExpandedTriggerBranch(
                        branch,
                        rows,
                        triggerInnerX + 12f,
                        ref cy,
                        triggerInnerW - 24f
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

        // 对齐到大框底部，避免展开/折叠后后续区域位置漂移。
        cy =
            triggerGroupY
            +
            triggerGroupHeight
            +
            10f;

        // =========================
        // 节点走向
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
                node.Kind
                ==
                NodeKind.Rite
                    ?
                    "仪式走向"
                    :
                    "事件走向",
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

    private static float EstimateTriggerMechanismHeight(
        GuideNode node
    )
    {
        return
            32f
            +
            EstimateTriggerBranchGroupHeight(
                node
            )
            +
            10f;
    }

    private static float EstimateTriggerBranchGroupHeight(
        GuideNode node
    )
    {
        // 上下各留 10px 内边距，让所有分支视觉上属于同一个“触发机制”容器。
        float height =
            20f;

        if (
            node.TriggerBranches.Count
            ==
            0
        )
        {
            return
                height
                +
                44f;
        }

        for (
            int i = 0;
            i
            <
            node.TriggerBranches.Count;
            i++
        )
        {
            var branch =
                node.TriggerBranches[i];

            string key =
                BuildTriggerBranchKey(
                    node,
                    branch,
                    i
                );

            bool expanded =
                _expandedTriggerBranches.Contains(
                    key
                );

            EvaluateTriggerBranch(
                branch,
                out var rows
            );

            height +=
                EstimateTriggerBranchContainerHeight(
                    branch,
                    rows,
                    expanded
                )
                +
                8f;
        }

        return
            height;
    }

    private static float EstimateTriggerBranchContainerHeight(
        GuideTriggerBranch branch,
        List<ConditionCheckRow> rows,
        bool expanded
    )
    {
        float height =
            48f;

        if (
            expanded
        )
        {
            height +=
                EstimateExpandedTriggerBranchHeight(
                    branch,
                    rows
                );
        }

        return
            height
            +
            6f;
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

    private static float EstimateExpandedTriggerBranchHeight(
        GuideTriggerBranch branch,
        List<ConditionCheckRow> rows
    )
    {
        string explanation =
            BuildNaturalTriggerExplanation(
                branch
            );

        float height =
            12f
            +
            EstimateTextHeight(
                explanation,
                48f
            )
            +
            8f;

        if (
            rows.Count > 0
        )
        {
            height +=
                24f;

            foreach (
                var row
                in
                rows
            )
            {
                height +=
                    EstimateConditionRowHeight(
                        row
                    );
            }
        }

        return
            height
            +
            12f;
    }

    private static float EstimateConditionRowHeight(
        ConditionCheckRow row
    )
    {
        float textHeight =
            EstimateTextHeight(
                row.Text,
                24f
            );

        float detailHeight =
            string.IsNullOrWhiteSpace(
                row.Detail
            )
                ?
                0f
                :
                EstimateTextHeight(
                    row.Detail,
                    22f
                );

        return
            Math.Max(
                42f,
                textHeight
                +
                detailHeight
                +
                8f
            );
    }

    private static void DrawExpandedTriggerBranch(
        GuideTriggerBranch branch,
        List<ConditionCheckRow> rows,
        float x,
        ref float cy,
        float w
    )
    {
        float startY =
            cy;

        float estimated =
            EstimateExpandedTriggerBranchHeight(
                branch,
                rows
            );

        float innerX =
            x;

        float innerW =
            w;

        cy +=
            4f;

        string explanation =
            BuildNaturalTriggerExplanation(
                branch
            );

        float explanationHeight =
            EstimateTextHeight(
                explanation,
                48f
            );

        GUI.Label(
            new Rect(
                innerX,
                cy,
                innerW,
                explanationHeight
            ),
            explanation,
            _body
        );

        cy +=
            explanationHeight
            +
            8f;

        // 实时条件状态仍保留，但不再拆成“检查阶段 / 条件 / 满足后”。
        if (
            rows.Count > 0
        )
        {
            GUI.Label(
                new Rect(
                    innerX,
                    cy,
                    76f,
                    22f
                ),
                "当前状态",
                _statusTitleStyle
            );

            DrawConditionStateIcon(
                new Rect(
                    innerX + 74f,
                    cy + 3f,
                    16f,
                    16f
                ),
                GetConditionRowsOverallState(
                    rows
                )
            );

            cy +=
                22f;

            foreach (
                var row
                in
                rows
            )
            {
                float rowHeight =
                    EstimateConditionRowHeight(
                        row
                    );

                GUI.Label(
                    new Rect(
                        innerX,
                        cy,
                        innerW,
                        rowHeight
                    ),
                    string.IsNullOrWhiteSpace(
                        row.Detail
                    )
                        ?
                        row.Text
                        :
                        row.Text
                        +
                        "\n"
                        +
                        row.Detail,
                    _body
                );

                cy +=
                    rowHeight;
            }
        }

        cy +=
            10f;

        cy =
            Math.Max(
                cy,
                startY
                +
                estimated
            );
    }

    private static string BuildNaturalTriggerExplanation(
        GuideTriggerBranch branch
    )
    {
        string source =
            string.IsNullOrWhiteSpace(
                branch.SourceName
            )
                ?
                branch.Name
                :
                branch.SourceName;

        string timing =
            string.IsNullOrWhiteSpace(
                branch.Timing
            )
                ?
                ""
                :
                branch.Timing.Trim();

        string effect =
            string.IsNullOrWhiteSpace(
                branch.Effect
            )
                ?
                "执行该分支。"
                :
                branch.Effect.Trim();

        // 展示层只做轻量去重，不改变数据库里的原始触发关系。
        effect =
            effect.Replace(
                "满足后生成",
                "生成",
                StringComparison.Ordinal
            );

        if (
            !effect.EndsWith(
                "。",
                StringComparison.Ordinal
            )
        )
        {
            effect +=
                "。";
        }

        string sourceKind =
            branch.SourceKind
            ==
            NodeKind.Rite
                ?
                "仪式"
                :
                "事件";

        string sourceSentence =
            branch.SourceId > 0
                ?
                $"{sourceKind}「{source}」{effect}"
                :
                effect;

        if (
            string.IsNullOrWhiteSpace(
                timing
            )
        )
        {
            return
                sourceSentence;
        }

        return
            timing
            +
            "\n"
            +
            sourceSentence;
    }

    private static string BuildTriggerBranchKey(
        GuideNode node,
        GuideTriggerBranch branch,
        int index
    )
    {
        return
            node.Id
            +
            ":"
            +
            index
            +
            ":"
            +
            branch.SourceId
            +
            ":"
            +
            branch.Name;
    }

    private static ConditionRuntimeState GetConditionRowsOverallState(
        List<ConditionCheckRow> rows
    )
    {
        bool hasUnknown =
            false;

        foreach (
            var row
            in
            rows
        )
        {
            if (
                row.State
                ==
                ConditionRuntimeState.Unmet
            )
            {
                return
                    ConditionRuntimeState.Unmet;
            }

            if (
                row.State
                ==
                ConditionRuntimeState.Unknown
            )
            {
                hasUnknown =
                    true;
            }
        }

        return
            hasUnknown
                ?
                ConditionRuntimeState.Unknown
                :
                ConditionRuntimeState.Met;
    }

    private static void DrawConditionStateIcon(
        Rect rect,
        ConditionRuntimeState state
    )
    {
        Texture2D? circle =
            state switch
            {
                ConditionRuntimeState.Met =>
                    _stateMetCircleTex,

                ConditionRuntimeState.Unmet =>
                    _stateUnmetCircleTex,

                _ =>
                    _stateUnknownCircleTex
            };

        if (
            circle != null
        )
        {
            GUI.DrawTexture(
                rect,
                circle,
                ScaleMode.StretchToFill,
                true
            );
        }

        string symbol =
            state switch
            {
                ConditionRuntimeState.Met =>
                    "✓",

                ConditionRuntimeState.Unmet =>
                    "×",

                _ =>
                    "?"
            };

        GUI.Label(
            rect,
            symbol,
            _stateIconSymbolStyle
        );
    }

    private static ConditionRuntimeState EvaluateTriggerBranch(
        GuideTriggerBranch branch,
        out List<ConditionCheckRow> rows
    )
    {
        rows =
            new List<ConditionCheckRow>();

        if (
            branch.IsFallback
        )
        {
            string fallbackCondition =
                !string.IsNullOrWhiteSpace(
                    branch.RawCondition
                )
                    ?
                    branch.RawCondition.Trim()
                    :
                    branch.HumanCondition;

            if (
                !string.IsNullOrWhiteSpace(
                    fallbackCondition
                )
                &&
                !IsNoExtraConditionText(
                    fallbackCondition
                )
            )
            {
                rows.Add(
                    new ConditionCheckRow
                    {
                        State =
                            ConditionRuntimeState.Unknown,
                        Text =
                            "待适配条件",
                        Detail =
                            "原始条件："
                            +
                            fallbackCondition
                    }
                );
            }

            return
                ConditionRuntimeState.Unknown;
        }

        if (
            string.IsNullOrWhiteSpace(
                branch.RawCondition
            )
            ||
            IsNoExtraConditionText(
                branch.HumanCondition
            )
        )
        {
            // 没有额外要求就是“无条件分支”：
            // 分支本身显示绿色 ✓ 即可，不再展开“当前状态 / 没有额外要求”。
            return
                ConditionRuntimeState.Met;
        }

        try
        {
            using var doc =
                JsonDocument.Parse(
                    branch.RawCondition
                );

            return
                EvaluateConditionElement(
                    doc.RootElement,
                    rows,
                    true
                );
        }
        catch
        {
            string raw =
                string.IsNullOrWhiteSpace(
                    branch.RawCondition
                )
                    ?
                    branch.HumanCondition
                    :
                    branch.RawCondition.Trim();

            if (
                !string.IsNullOrWhiteSpace(
                    raw
                )
                &&
                !IsNoExtraConditionText(
                    raw
                )
            )
            {
                rows.Add(
                    new ConditionCheckRow
                    {
                        State =
                            ConditionRuntimeState.Unknown,
                        Text =
                            "待适配条件",
                        Detail =
                            "原始条件："
                            +
                            raw
                    }
                );
            }

            return
                ConditionRuntimeState.Unknown;
        }
    }

    private static bool IsNoExtraConditionText(
        string? text
    )
    {
        if (
            string.IsNullOrWhiteSpace(
                text
            )
        )
        {
            return
                true;
        }

        string normalized =
            text
                .Trim()
                .TrimEnd(
                    '。',
                    '.'
                );

        return
            normalized.Equals(
                "没有额外要求",
                StringComparison.Ordinal
            )
            ||
            normalized.Equals(
                "没有额外条件",
                StringComparison.Ordinal
            );
    }

    private static ConditionRuntimeState EvaluateConditionElement(
        JsonElement element,
        List<ConditionCheckRow> rows,
        bool allByDefault
    )
    {
        if (
            element.ValueKind
            ==
            JsonValueKind.Object
        )
        {
            var states =
                new List<ConditionRuntimeState>();

            foreach (
                var property
                in
                element.EnumerateObject()
            )
            {
                if (
                    property.Name.Equals(
                        "all",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    states.Add(
                        EvaluateConditionElement(
                            property.Value,
                            rows,
                            true
                        )
                    );

                    continue;
                }

                if (
                    property.Name.Equals(
                        "any",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    states.Add(
                        EvaluateConditionElement(
                            property.Value,
                            rows,
                            false
                        )
                    );

                    continue;
                }

                states.Add(
                    EvaluateConditionAtom(
                        property.Name,
                        property.Value,
                        rows
                    )
                );
            }

            return
                CombineConditionStates(
                    states,
                    allByDefault
                );
        }

        if (
            element.ValueKind
            ==
            JsonValueKind.Array
        )
        {
            var states =
                new List<ConditionRuntimeState>();

            foreach (
                var item
                in
                element.EnumerateArray()
            )
            {
                states.Add(
                    EvaluateConditionElement(
                        item,
                        rows,
                        allByDefault
                    )
                );
            }

            return
                CombineConditionStates(
                    states,
                    allByDefault
                );
        }

        return
            ConditionRuntimeState.Unknown;
    }

    private static ConditionRuntimeState CombineConditionStates(
        List<ConditionRuntimeState> states,
        bool all
    )
    {
        if (
            states.Count
            ==
            0
        )
        {
            return
                ConditionRuntimeState.Met;
        }

        if (all)
        {
            if (
                states.Any(
                    x =>
                        x
                        ==
                        ConditionRuntimeState.Unmet
                )
            )
            {
                return
                    ConditionRuntimeState.Unmet;
            }

            if (
                states.All(
                    x =>
                        x
                        ==
                        ConditionRuntimeState.Met
                )
            )
            {
                return
                    ConditionRuntimeState.Met;
            }

            return
                ConditionRuntimeState.Unknown;
        }

        if (
            states.Any(
                x =>
                    x
                    ==
                    ConditionRuntimeState.Met
            )
        )
        {
            return
                ConditionRuntimeState.Met;
        }

        if (
            states.All(
                x =>
                    x
                    ==
                    ConditionRuntimeState.Unmet
            )
        )
        {
            return
                ConditionRuntimeState.Unmet;
        }

        return
            ConditionRuntimeState.Unknown;
    }

    private static ConditionRuntimeState EvaluateConditionAtom(
        string key,
        JsonElement value,
        List<ConditionCheckRow> rows
    )
    {
        string human =
            _db != null
                ?
                _db.HumanizeConditionAtom(
                    key,
                    value
                )
                :
                key;

        var counterMatch =
            Regex.Match(
                key,
                @"^counter\.(\d+)(>=|<=|>|<|=)?$"
            );

        if (
            counterMatch.Success
            &&
            value.ValueKind
            ==
            JsonValueKind.Number
            &&
            value.TryGetInt32(
                out var target
            )
        )
        {
            try
            {
                var player =
                    Common.Player;

                if (
                    player
                    ==
                    null
                )
                {
                    rows.Add(
                        new ConditionCheckRow
                        {
                            State =
                                ConditionRuntimeState.Unknown,
                            Text =
                                human,
                            Detail =
                                "当前不在可读取的游戏局内。"
                        }
                    );

                    return
                        ConditionRuntimeState.Unknown;
                }

                int counterId =
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

                int current =
                    PlayerExtensions.GetCounter(
                        player,
                        counterId
                    );

                bool met =
                    CompareCounter(
                        current,
                        target,
                        op
                    );

                rows.Add(
                    new ConditionCheckRow
                    {
                        State =
                            met
                                ?
                                ConditionRuntimeState.Met
                                :
                                ConditionRuntimeState.Unmet,
                        Text =
                            human,
                        Detail =
                            BuildCounterProgressText(
                                current,
                                target,
                                op,
                                met
                            )
                    }
                );

                return
                    met
                        ?
                        ConditionRuntimeState.Met
                        :
                        ConditionRuntimeState.Unmet;
            }
            catch
            {
                rows.Add(
                    new ConditionCheckRow
                    {
                        State =
                            ConditionRuntimeState.Unknown,
                        Text =
                            human,
                        Detail =
                            "这个计数条件当前读取失败。"
                    }
                );

                return
                    ConditionRuntimeState.Unknown;
            }
        }

        if (
            (
                key.Equals(
                    "rite",
                    StringComparison.OrdinalIgnoreCase
                )
                ||
                key.Equals(
                    "!rite",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            &&
            value.ValueKind
            ==
            JsonValueKind.Number
            &&
            value.TryGetInt32(
                out var riteId
            )
        )
        {
            bool shouldExist =
                !key.StartsWith(
                    "!",
                    StringComparison.Ordinal
                );

            bool exists =
                _runtimeRiteIds.Contains(
                    riteId
                );

            bool met =
                shouldExist
                    ?
                    exists
                    :
                    !exists;

            string riteName =
                _db != null
                &&
                _db.RiteNames.TryGetValue(
                    riteId,
                    out var name
                )
                    ?
                    $"《{name}》"
                    :
                    "对应仪式";

            rows.Add(
                new ConditionCheckRow
                {
                    State =
                        met
                            ?
                            ConditionRuntimeState.Met
                            :
                            ConditionRuntimeState.Unmet,
                    Text =
                        human,
                    Detail =
                        $"当前：{riteName}{(exists ? "存在于地图" : "不在地图上")}。"
                }
            );

            return
                met
                    ?
                    ConditionRuntimeState.Met
                    :
                    ConditionRuntimeState.Unmet;
        }

        string rawAtom =
            key
            +
            " = "
            +
            value.GetRawText();

        rows.Add(
            new ConditionCheckRow
            {
                State =
                    ConditionRuntimeState.Unknown,
                Text =
                    string.IsNullOrWhiteSpace(
                        human
                    )
                        ?
                        "待适配条件"
                        :
                        human,
                Detail =
                    "原始条件："
                    +
                    rawAtom
            }
        );

        return
            ConditionRuntimeState.Unknown;
    }

    private static bool CompareCounter(
        int current,
        int target,
        string op
    )
    {
        return op switch
        {
            ">=" =>
                current >= target,

            "<=" =>
                current <= target,

            ">" =>
                current > target,

            "<" =>
                current < target,

            "=" =>
                current == target,

            _ =>
                false
        };
    }

    private static string BuildCounterProgressText(
        int current,
        int target,
        string op,
        bool met
    )
    {
        string requirement =
            op
            +
            target;

        string progress =
            $"当前：{current}；要求：{requirement}。";

        if (met)
        {
            return
                progress
                +
                " 已满足。";
        }

        if (
            op
            ==
            ">="
        )
        {
            return
                progress
                +
                $" 还差：{Math.Max(0, target - current)}。";
        }

        if (
            op
            ==
            ">"
        )
        {
            return
                progress
                +
                $" 还差：{Math.Max(0, target + 1 - current)}。";
        }

        if (
            op
            ==
            "<="
        )
        {
            return
                progress
                +
                $" 当前已超过上限 {Math.Max(0, current - target)}。";
        }

        if (
            op
            ==
            "<"
        )
        {
            return
                progress
                +
                $" 当前已超过允许范围 {Math.Max(0, current - (target - 1))}。";
        }

        return
            progress;
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

    private static Texture2D CreateStateCircleTexture(
        Color fill
    )
    {
        const int size =
            32;

        var tex =
            new Texture2D(
                size,
                size
            );

        float center =
            (size - 1)
            *
            0.5f;

        float radius =
            center
            -
            1f;

        for (
            int y = 0;
            y < size;
            y++
        )
        {
            for (
                int x = 0;
                x < size;
                x++
            )
            {
                float dx =
                    x
                    -
                    center;

                float dy =
                    y
                    -
                    center;

                float distance =
                    Mathf.Sqrt(
                        dx * dx
                        +
                        dy * dy
                    );

                // 1px 左右的柔和边缘，缩放到 20~24px 时不会显得锯齿太重。
                float alpha =
                    Mathf.Clamp01(
                        radius
                        -
                        distance
                        +
                        1f
                    );

                tex.SetPixel(
                    x,
                    y,
                    new Color(
                        fill.r,
                        fill.g,
                        fill.b,
                        fill.a
                        *
                        alpha
                    )
                );
            }
        }

        tex.Apply();

        return
            tex;
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
            _stateMetCircleTex
            ==
            null
        )
        {
            _stateMetCircleTex =
                CreateStateCircleTexture(
                    new Color(
                        0.10f,
                        0.55f,
                        0.27f,
                        1f
                    )
                );
        }

        if (
            _stateUnmetCircleTex
            ==
            null
        )
        {
            _stateUnmetCircleTex =
                CreateStateCircleTexture(
                    new Color(
                        0.82f,
                        0.13f,
                        0.19f,
                        1f
                    )
                );
        }

        if (
            _stateUnknownCircleTex
            ==
            null
        )
        {
            _stateUnknownCircleTex =
                CreateStateCircleTexture(
                    new Color(
                        0.38f,
                        0.43f,
                        0.49f,
                        1f
                    )
                );
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
            _statusTitleStyle
            ==
            null
        )
        {
            _statusTitleStyle =
                new GUIStyle();

            _statusTitleStyle.fontSize =
                12;

            _statusTitleStyle.fontStyle =
                FontStyle.Bold;

            _statusTitleStyle
                .normal
                .textColor =
                    new Color(
                        0.66f,
                        0.76f,
                        0.84f,
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
            _stateIconSymbolStyle
            ==
            null
        )
        {
            _stateIconSymbolStyle =
                new GUIStyle();

            _stateIconSymbolStyle.fontSize =
                11;

            _stateIconSymbolStyle.fontStyle =
                FontStyle.Bold;

            _stateIconSymbolStyle.alignment =
                TextAnchor.MiddleCenter;

            _stateIconSymbolStyle
                .normal
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
