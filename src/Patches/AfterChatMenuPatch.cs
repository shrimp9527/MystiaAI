using System;
using HarmonyLib;
using MystiaAI.Core;

namespace MystiaAI.Patches;

/// <summary>
/// 聊后菜单抑制：稀客聊天结束后游戏会重新打开选项菜单（闲聊/邀请/关于食材…，
/// DaySceneChatSelectionPannel.cs:450 → UIManager.OpenAfterChatMenu），
/// 与自动续聊重开的下一轮对话互相覆盖。续聊条件满足时（玩家有输入且未点「结束对话」）
/// 拦截菜单打开，让对话无缝进入下一轮；对话真正结束时菜单照常弹出（可继续邀请等操作）。
/// 只 patch 稀客重载（首参 string specialCharacterLabel）；普客/通用重载不受影响。
/// </summary>
[HarmonyPatch(typeof(DayScene.UI.UIManager), nameof(DayScene.UI.UIManager.OpenAfterChatMenu),
    typeof(string), typeof(Il2CppSystem.Action), typeof(Il2CppSystem.Action<Il2CppSystem.Action>),
    typeof(bool), typeof(DEYU.AdpUISystem.Managers.AdpUIPanelManager.PanelVisualMode))]
internal static class AfterChatMenuPatch
{
    private static bool Prefix(string specialCharacterLabel)
    {
        try
        {
            var suppress = DialogContinuation.ShouldSuppressAfterChatMenu(specialCharacterLabel);
            PluginContext.Log.LogInfo(
                $"[MystiaAI] AfterChatMenu: 调用（label={specialCharacterLabel}，抑制={suppress}）");
            if (suppress) return false; // 跳过菜单打开
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] AfterChatMenuPatch 异常: {ex}");
        }
        return true;
    }
}
