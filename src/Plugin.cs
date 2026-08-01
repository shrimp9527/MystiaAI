using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace MystiaAI;

/// <summary>
/// MystiaAI 插件入口。
/// 仅用 AI 生成的文本替换「闲聊类」文本（白天地图闲聊 / 营业中稀客闲聊 / 上菜后评价语），
/// 剧情对话包、羁绊升级对话、点单文本一律不碰。
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "cc.mystia.ai";
    public const string PluginName = "MystiaAI";
    public const string PluginVersion = "0.1.7"; // 与最新 Release 标签保持一致（每次发版更新）

    internal static new ManualLogSource Log = null!;

    public override void Load()
    {
        Log = base.Log;

        Core.PluginContext.Initialize(Config, Log);

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(Plugin).Assembly);
        // DialogPannel 相关 patch 手动注册并自检（第四轮实测证明特性式 patch 可能静默失效）
        Patches.DialogPannelPatch.Install(harmony);
        // 夜晚营业气泡（闲聊+评价语）：纯安全锚点方案——零夜场景 patch，
        // 帧泵挂在 DialogPannelPatch 的 EventSystem.Update postfix 同链（见 NightBubblePatch 类注释）。
        Patches.NightBubblePatch.Install(harmony);
        // 【保持下线】旧的夜场景方法 patch 方案（启动闪退根因），代码仅作参考保留：
        // Patches.NightChatPatch.Install(harmony);
        // Patches.NightDiagPatch.Install(harmony);

        Log.LogInfo($"[MystiaAI] v{PluginVersion} loaded.");
    }
}
