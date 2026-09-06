using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace SultansGameGuide;

[BepInPlugin(
    "wharman.sultansgame.guide",
    "苏丹的游戏·攻略助手",
    "0.4.86"
)]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        var overlay =
            AddComponent<GuideOverlay>();

        Log.LogInfo(
            "苏丹攻略助手 0.4.86 已加载：真实地图仪式 + 自动跟随 + 不透明防穿透"
        );

        Log.LogInfo(
            $"GuideOverlay component pointer: 0x{overlay.Pointer.ToString("X")}"
        );
    }
}
