苏丹的游戏 · 攻略助手 MOD（GitHub Actions v0.3）
================================================

这是 BepInEx 6 / Unity IL2CPP 插件源码。

v0.3 修正：
- 修复 GitHub Actions 中 UnityEngine / MonoBehaviour / Rect / Vector2 / GUIStyle 等类型找不到的问题。
- 构建时自动下载《苏丹的游戏》对应 Unity 2022.3.62 的参考程序集。
- 不需要把 Unity DLL 手动上传到 GitHub。

GitHub 在线编译：
1. 仓库根目录必须直接包含：
   .github/
   src/
   NuGet.config
   README.txt
2. 打开 Actions -> Build SultansGameGuide MOD。
3. Run workflow。
4. 成功后在 Artifacts 下载 SultansGameGuide-Mod。
5. 把其中：
   BepInEx/plugins/SultansGameGuide/SultansGameGuide.dll
   放到游戏同名目录。

运行前：
- 游戏需要先安装 BepInEx 6 Unity.IL2CPP-win-x64。
- 第一次安装 BepInEx 后建议先不放本 MOD，启动游戏一次，让 BepInEx 生成 interop 文件，再退出游戏安装本 MOD。

当前功能：
- 游戏内半透明窗口
- F8 显示/隐藏
- 可拖动、可最小化
- 读取 StreamingAssets/config
- 搜索事件、仪式、后日谈
- 尽量把内部条件 ID 转换为中文可读说明

说明：
当前 v0.x 仍是静态攻略查询阶段；“自动定位当前正在进行的游戏事件/读取当前局状态”会在后续运行时接入版本中加入。


v0.4 build fix: removed the compile-time IntPtr MonoBehaviour constructor. Modern Il2CppInterop ClassInjector supports creating an injected type without that constructor via its generated empty-constructor path. This avoids conflict with Unity reference assemblies used by GitHub Actions.


v0.7 修复：恢复 ClassInjector + GameObject.AddComponent 注入方式；保留 F8 显示/隐藏。


v0.8：快捷键改为 Ctrl+O，保留 IL2CPP ClassInjector 注入方式。


v0.9：窗口启动自动显示；即使 Ctrl+O 失效，隐藏后左上角仍保留“攻略助手”按钮；增加 OnGUI/Start 日志用于定位。


v0.10：修复 Logger 命名冲突（BepInEx.Logging.Logger 与 UnityEngine.Logger）。


v0.11 / 插件 0.1.7：改用用户游戏中 BepInEx be.788 实际生成的 IL2CPP interop DLL；恢复 BasePlugin.AddComponent<GuideOverlay>()；加入 IntPtr 构造；先用最小 IMGUI 验证 Start/OnGUI 生命周期。
