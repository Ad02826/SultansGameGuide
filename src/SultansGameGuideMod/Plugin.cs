using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace SultansGameGuide;

[BepInPlugin(
    "wharman.sultansgame.guide",
    "苏丹的游戏·攻略助手",
    "0.4.88"
)]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        var overlay =
            AddComponent<GuideOverlay>();

        Log.LogInfo(
            "苏丹攻略助手 0.4.88 已加载：全节点触发机制预览 + 实时条件状态"
        );

        Log.LogInfo(
            $"GuideOverlay component pointer: 0x{overlay.Pointer.ToString("X")}"
        );
    }
}
