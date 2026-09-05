using System;
using BepInEx.Logging;
using UnityEngine;

namespace SultansGameGuide;

public sealed class GuideOverlay : MonoBehaviour
{
    private static readonly ManualLogSource Log =
        BepInEx.Logging.Logger.CreateLogSource("SultanGuideOverlay");

    // 由 IL2CPP 侧创建组件时使用。
    public GuideOverlay(IntPtr ptr) : base(ptr) { }

    // 用 static 避免把复杂托管字段注入到 IL2CPP 对象布局里。
    private static bool _visible = true;
    private static bool _minimized = false;
    private static bool _loggedOnGui = false;
    private static string _search = "";

    private void Start()
    {
        _visible = true;
        Log.LogInfo("GuideOverlay.Start invoked");
    }

    private void Update()
    {
        bool ctrlHeld =
            Input.GetKey(KeyCode.LeftControl) ||
            Input.GetKey(KeyCode.RightControl);

        if (ctrlHeld && Input.GetKeyDown(KeyCode.O))
        {
            _visible = !_visible;
            Log.LogInfo("Ctrl+O toggle from Update: " + _visible);
        }
    }

    private void OnGUI()
    {
        if (!_loggedOnGui)
        {
            _loggedOnGui = true;
            Log.LogInfo("GuideOverlay.OnGUI invoked");
        }

        var e = Event.current;
        if (e != null &&
            e.type == EventType.KeyDown &&
            e.keyCode == KeyCode.O &&
            e.control)
        {
            _visible = !_visible;
            e.Use();
            Log.LogInfo("Ctrl+O toggle from OnGUI: " + _visible);
        }

        // 即使隐藏，也保留一个固定入口，避免快捷键被游戏吃掉。
        if (!_visible)
        {
            if (GUI.Button(new Rect(18, 85, 105, 36), "攻略助手"))
                _visible = true;
            return;
        }

        if (_minimized)
        {
            GUI.Box(new Rect(18, 85, 130, 45), "");
            if (GUI.Button(new Rect(25, 92, 116, 31), "攻略助手  ＋"))
                _minimized = false;
            return;
        }

        // 先用最朴素、无委托的 IMGUI 验证渲染链路。
        // 确认显示后，再换成最终半透明可拖动树状窗口。
        const float x = 32f;
        const float y = 78f;
        const float w = 640f;
        const float h = 430f;

        GUI.Box(new Rect(x, y, w, h), "");

        GUI.Label(
            new Rect(x + 18, y + 16, 430, 28),
            "苏丹的游戏 · 攻略助手  v0.1.7");

        if (GUI.Button(new Rect(x + w - 86, y + 12, 32, 28), "—"))
            _minimized = true;

        if (GUI.Button(new Rect(x + w - 46, y + 12, 32, 28), "×"))
            _visible = false;

        GUI.Label(new Rect(x + 18, y + 58, 80, 26), "搜索：");
        _search = GUI.TextField(
            new Rect(x + 72, y + 56, w - 100, 28),
            _search ?? "");

        GUI.Label(
            new Rect(x + 18, y + 105, w - 36, 28),
            "如果你能看到这里，说明 IL2CPP MonoBehaviour + OnGUI 已经真正运行。");

        GUI.Label(
            new Rect(x + 18, y + 140, w - 36, 28),
            "Ctrl + O：显示/隐藏；左上固定入口可兜底重新打开。");

        GUI.Label(
            new Rect(x + 18, y + 185, w - 36, 80),
            "下一步接入真实剧情数据：人物 → 事件 → 人话条件 → 选择分支 → 可达结局。");

        GUI.Label(
            new Rect(x + 18, y + h - 55, w - 36, 28),
            "当前为 interop 修复验证版，不修改游戏数值和存档。");
    }
}
