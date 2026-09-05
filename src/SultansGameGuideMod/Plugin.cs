using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace SultansGameGuide;

[BepInPlugin(
    "wharman.sultansgame.guide",
    "苏丹的游戏·攻略助手",
    "0.2.1"
)]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        var overlay = AddComponent<GuideOverlay>();

        Log.LogInfo(
            "苏丹攻略助手 0.2.1 已加载：" +
            "读取游戏 StreamingAssets/config；Ctrl+O 显示/隐藏"
        );

        Log.LogInfo(
            $"GuideOverlay component pointer: 0x{overlay.Pointer.ToString("X")}"
        );
    }
}
