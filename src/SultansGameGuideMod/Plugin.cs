using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace SultansGameGuide;

[BepInPlugin("wharman.sultansgame.guide", "苏丹的游戏·攻略助手", "0.1.7")]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        var overlay = AddComponent<GuideOverlay>();
        Log.LogInfo("苏丹攻略助手已加载：使用游戏实际 IL2CPP interop；窗口启动自动显示；Ctrl+O 显示/隐藏");
        Log.LogInfo($"GuideOverlay component pointer: 0x{overlay.Pointer.ToString("X")}");
    }
}
