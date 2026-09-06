using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace SultansGameGuide;

[BepInPlugin(
    "wharman.sultansgame.guide",
    "苏丹的游戏·攻略助手",
    "0.5.2"
)]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        var overlay =
            AddComponent<GuideOverlay>();

        Log.LogInfo(
            "苏丹攻略助手 0.5.2 已加载：剧情关系图 + 早期错误审计清理"
        );

        Log.LogInfo(
            $"GuideOverlay component pointer: 0x{overlay.Pointer.ToString("X")}"
        );
    }
}
