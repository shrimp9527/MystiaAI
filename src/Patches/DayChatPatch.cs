using System;
using System.Collections.Generic;
using Common.DialogUtility;
using GameData.Profile;
using GameData.RunTime.DaySceneUtility;
using HarmonyLib;
using MystiaAI.Core;

namespace MystiaAI.Patches;

/// <summary>
/// 白天地图 NPC 闲聊的取数点。
/// RunTimeDayScene.GetCharacterChatData 返回闲聊 DialogPackage 后，
/// 只登记对话元数据（各段 dialogId + 说话人是否 Self）进 PendingReplacementStore，
/// 不发起生成任务——连贯改写需要整段原文（textFile），而原文要等面板打开才有；
/// 生成任务由 DialogPannelPatch.OnExecutingDialogLoopCore 为每个非 Self 段发起。
/// Self（米斯蒂娅）的段一律保持原文，完全不进后续替换流程。
/// </summary>
[HarmonyPatch(typeof(RunTimeDayScene), nameof(RunTimeDayScene.GetCharacterChatData))]
internal static class DayChatPatch
{
    private static void Postfix(string characterKey, bool isPostChat, DialogPackage __result)
    {
        try
        {
            if (!PluginContext.Settings.Enabled) return;
            if (__result == null) return;

            // 首次对话时一次性预建全量稀客别名表（白天是最早的对话场景；
            // 白天 NPC 人设查找也走 stringId → 中文名 别名换算）
            SpecialGuestNames.EnsurePrebuilt(PluginContext.Personas);

            var metas = __result.dialogMeta;
            if (metas == null || metas.Length == 0) return;

            var segments = new List<DialogSegment>(metas.Length);
            var npcCount = 0;
            foreach (var meta in metas)
            {
                if (meta == null) continue;
                var isSelf = meta.speakerIdentity.speakerType == SpeakerIdentity.Identity.Self;
                segments.Add(new DialogSegment { DialogId = meta.dialogId, IsSelf = isSelf });
                if (!isSelf) npcCount++;
            }

            if (segments.Count == 0) return;

            var replacement = new PendingReplacement
            {
                CharacterKey = characterKey,
                Segments = segments,
            };
            PendingReplacementStore.Register(__result, replacement);
            // 自动续聊登记：新一轮对话（重置跨轮记录）；播完后由 OpenDialogMenuPatch
            // 包装的结束回调驱动无缝重开
            DialogContinuation.Register(__result, replacement);

            // 诊断日志：打印匹配键（包名优先，指针兜底）与 native 指针，
            // 便于和 OpenDialogMenuPatch 的诊断输出对照定位匹配失败
            PluginContext.Log.LogInfo(
                $"[MystiaAI] DayChat: 角色 {characterKey} 闲聊共 {segments.Count} 段，" +
                $"其中 NPC 段 {npcCount} 段（Self 段保持原文），" +
                $"包 key={PendingReplacementStore.KeyOf(__result)} ptr=0x{__result.Pointer:X} (isPostChat={isPostChat})");
        }
        catch (Exception ex)
        {
            // Patch 里绝不向游戏抛异常，只记日志
            PluginContext.Log.LogError($"[MystiaAI] DayChatPatch 异常（角色 {characterKey}）: {ex}");
        }
    }
}
