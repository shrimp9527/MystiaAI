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
    public const string PluginVersion = "0.1.0";

    internal static new ManualLogSource Log = null!;

    public override void Load()
    {
        Log = base.Log;

        Core.PluginContext.Initialize(Config, Log);

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(Plugin).Assembly);
        // DialogPannel 相关 patch 手动注册并自检（第四轮实测证明特性式 patch 可能静默失效）
        Patches.DialogPannelPatch.Install(harmony);
        // 【暂时停用】营业场景 patch 全部下线回滚：存档含待结算评价，加载时游戏自调
        // PostEvaluation → OnRequestEvaluationDialog，我们的 postfix 在半成品实例上崩（签名 A）。
        // 白天链路（05:53 实机验证）不受影响。夜晚功能待按「纯安全锚点」方案重做后再恢复。
        // Patches.NightChatPatch.Install(harmony);
        // Patches.NightDiagPatch.Install(harmony);

        Log.LogInfo($"[MystiaAI] v{PluginVersion} loaded.");
    }
}
