using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GameData.RunTime.Common;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MystiaAI.Core;
using NightScene.GuestManagementUtility;
using UnityEngine;
using DialogBoxUI = NightScene.UI.GuestManagementUtility.DialogBoxUI;

namespace MystiaAI.Patches;

/// <summary>
/// 营业场景（夜晚）稀客气泡文本的取数点：稀客闲聊 + 上菜后评价语。
///
/// 与白天 DialogPackage 管线不同，营业场景的闲聊/评价是
/// 「方法直接返回 string → GuestsManager.ShowTargetDialog 出 DialogBoxUI 气泡」，
/// 返回时必须立刻给文本，等不了 AI（1.5s+）。因此采用「原文先行 + 原地改写」：
/// 1. postfix 不改 __result——游戏立即用原文出气泡（失败/超时天然回退原文，零残留占位符）；
/// 2. 同帧在主线程预取上下文并发起 AI 生成；
/// 3. DialogBoxUI.SetMessage/SetMessageAsync postfix 按「原文 + 跟随目标」匹配到气泡实例；
/// 4. watcher 等任务完成/超时后 MainThreadDispatcher.Post 回主线程，
///    气泡还活着且文本仍是原文就原地改写为 AI 文本；任何失败都不动，原文已显示。
///
/// 甄别（不碰清单）：
/// - 顶仓点单 bark / 点单对话：不经 OnRequestIdleDialog / OnRequestEvaluationDialog，且本类不 patch
///   ShowTargetDialog 统一出口，bark 气泡即使也走 DialogBoxUI 也不会命中匹配（原文不同）；
/// - 普通客人：闲聊只 patch SpecialGuestsController 的 override；评价 postfix 内 TryCast 过滤；
/// - 羁绊升级对话 / 剧情对话：走 DialogPackage / OpenDialogMenu 管线，与这两个方法无关；
/// - MetaMystia 共存：postfix 不改 __result，对链上其他 patch 零干扰。
/// </summary>
internal static class NightChatPatch
{
    /// <summary>一条待改写的营业气泡：原文已先行显示，AI 文本到位后原地替换。</summary>
    private sealed class PendingBubble
    {
        public string CharacterKey = string.Empty; // 稀客内部名（stringId，如 "Wriggle"）
        public ChatScene Scene;                    // NightChat / Evaluation
        public string Original = string.Empty;     // 先行显示的原文（匹配锚点 + 回退基线）
        public IntPtr SpeakerPtr;                  // OnRequestXxxDialog out speaker 的 native 指针，零值=无
        public Task<string> AiTask = Task.FromResult(string.Empty);
        public DialogBoxUI? Box;                   // 匹配到的气泡
        public bool Matched;
        public bool Resolved;
    }

    /// <summary>待匹配/待终态的气泡列表。只在主线程访问（postfix 与 Post 回调都在主线程）。</summary>
    private static readonly List<PendingBubble> Pending = new();

    /// <summary>手动注册本类全部 patch 并自检（与 DialogPannelPatch.Install 同一模式）。</summary>
    public static void Install(Harmony harmony)
    {
        // 取数点 1：稀客闲聊（只 patch 稀客 override，普通客人的同名虚方法不受影响）
        PatchMethod(harmony, typeof(SpecialGuestsController), nameof(SpecialGuestsController.OnRequestIdleDialog),
            match: p => p.Length == 1 && p[0].IsOut,
            postfix: nameof(OnRequestIdleDialog_Postfix));

        // 取数点 2：上菜评价语（基类 concrete 方法，postfix 内过滤稀客实例）
        PatchMethod(harmony, typeof(GuestGroupController), nameof(GuestGroupController.OnRequestEvaluationDialog),
            match: p => p.Length == 2 && !p[0].IsOut && p[1].IsOut,
            postfix: nameof(OnRequestEvaluationDialog_Postfix));

        // 气泡捕获点：DialogBoxUI 的两个入口（同步 IEnumerator 版 + UniTask 版）
        PatchMethod(harmony, typeof(DialogBoxUI), nameof(DialogBoxUI.SetMessage),
            match: p => p.Length >= 2 && p[0].ParameterType == typeof(string),
            postfix: nameof(DialogBoxSetMessage_Postfix));
        PatchMethod(harmony, typeof(DialogBoxUI), nameof(DialogBoxUI.SetMessageAsync),
            match: p => p.Length >= 2 && p[0].ParameterType == typeof(string),
            postfix: nameof(DialogBoxSetMessage_Postfix));
    }

    /// <summary>列出候选重载打进日志，按参数形态选定目标注册 postfix，并回读注册结果自检。</summary>
    private static void PatchMethod(Harmony harmony, Type targetType, string targetName,
        Func<ParameterInfo[], bool> match, string postfix)
    {
        try
        {
            var candidates = targetType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name == targetName)
                .ToList();

            foreach (var candidate in candidates)
                PluginContext.Log.LogInfo($"[MystiaAI] NightChat 自检: 发现候选方法 {Describe(candidate)}");

            var target = candidates.FirstOrDefault(m => match(m.GetParameters()));
            if (target == null)
            {
                PluginContext.Log.LogError(
                    $"[MystiaAI] NightChat 自检失败: 无法解析 {targetType.Name}.{targetName}，本拦截点不生效！");
                return;
            }

            harmony.Patch(target,
                postfix: new HarmonyMethod(typeof(NightChatPatch), postfix));

            var info = Harmony.GetPatchInfo(target);
            var postfixCount = info?.Postfixes?.Count ?? 0;
            if (postfixCount == 0)
            {
                PluginContext.Log.LogError(
                    $"[MystiaAI] NightChat 自检失败: {targetType.Name}.{targetName} 注册后回读不到 postfix！");
                return;
            }
            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightChat 自检通过: {Describe(target)} 已注册 (postfix={postfixCount})");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] NightChat 自检失败: 注册 {targetType.Name}.{targetName} 时异常: {ex}");
        }
    }

    private static string Describe(MethodBase method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(p =>
            $"{(p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "")}{p.ParameterType.FullName} {p.Name}"));
        return $"{method.DeclaringType?.FullName}.{method.Name}({parameters})";
    }

    // ---- 取数点 postfix：登记 pending 并发起生成，不改 __result（原文先行显示）----

    /// <summary>
    /// 营业相位闸门：当前是否处于夜晚营业中（Work/Challenge 系）。
    /// postfix 的第一道防线——启动加载期（含「读档时游戏自动补调 PostEvaluation」的启动瞬间）
    /// 游戏对象处于半成品状态，此时 postfix 里任何 IL2CPP 调用（PeekOrders/SpecialGuest 属性等）
    /// 都可能 native AV（签名 A，coreclr+0x1d1fdd）。闸门本身只读一个 static 枚举属性，
    /// 任何异常一律视为「非营业」，宁可漏接绝不冒险。
    /// </summary>
    internal static bool IsNightWorkPhase()
    {
        try
        {
            var phase = GameData.RunTime.Common.RunTimeScheduler.CurrentGamePhase;
            return phase is GameData.RunTime.Common.RunTimeScheduler.GamePhase.Work
                or GameData.RunTime.Common.RunTimeScheduler.GamePhase.BeforeChallengeStart
                or GameData.RunTime.Common.RunTimeScheduler.GamePhase.Challenge;
        }
        catch
        {
            return false;
        }
    }

    // 注意：原方法的 out Transform speaker 参数刻意不接收——IL2CPP 下 out 参数经 Harmony
    // 封送到 managed postfix 会崩在 coreclr thunk（coreclr+0x1d1fdd 实锤）。speaker 缺失时
    // 气泡匹配退化为仅按原文唯一匹配（RegisterPending 内部已处理 null）。
    private static void OnRequestIdleDialog_Postfix(
        SpecialGuestsController __instance, string __result)
    {
        try
        {
            if (!PluginContext.Settings.Enabled) return;
            if (string.IsNullOrWhiteSpace(__result)) return;
            if (!IsNightWorkPhase()) return; // 启动加载期/非营业场景一律不碰 IL2CPP

            var guest = __instance.SpecialGuest;
            if (guest == null) return;

            RegisterPending(guest.stringId, guest.Id, ChatScene.NightChat, __result, null, null, null);
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] NightChatPatch.OnRequestIdleDialog 异常: {ex}");
        }
    }

    private static void OnRequestEvaluationDialog_Postfix(
        GuestGroupController __instance, GuestGroupController.EvaluationResult evaluation,
        string __result)
    {
        try
        {
            if (!PluginContext.Settings.Enabled) return;
            if (string.IsNullOrWhiteSpace(__result)) return;
            if (!IsNightWorkPhase()) return; // 启动加载期/非营业场景一律不碰 IL2CPP（PeekOrders 崩过）

            // 只接稀客评价；普通客人评价保持原文
            var special = __instance.TryCast<SpecialGuestsController>();
            var guest = special?.SpecialGuest;
            if (guest == null) return;

            var rating = RatingText(evaluation);
            if (rating.Length == 0) return; // Null 等非常规等级不生成

            var food = ResolveServFood(__instance);
            RegisterPending(guest.stringId, guest.Id, ChatScene.Evaluation, __result, null, null, rating, food);
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] NightChatPatch.OnRequestEvaluationDialog 异常: {ex}");
        }
    }

    // ---- 气泡捕获：DialogBoxUI 出文本时按「原文 + 跟随目标」认领 pending ----

    private static void DialogBoxSetMessage_Postfix(
        DialogBoxUI __instance, string message, Transform followTarget)
    {
        try
        {
            if (string.IsNullOrEmpty(message) || Pending.Count == 0) return;

            // 严格匹配：原文一致且跟随目标指针一致（speaker 缺失时退化为仅按原文唯一匹配）
            PendingBubble? hit = null;
            var followPtr = SafePtr(followTarget);
            foreach (var p in Pending)
            {
                if (p.Matched || p.Resolved || p.Original != message) continue;
                if (p.SpeakerPtr != IntPtr.Zero && followPtr != IntPtr.Zero && p.SpeakerPtr != followPtr) continue;
                if (hit != null) { hit = null; break; } // 多条候选无法区分，放弃匹配（保原文）
                hit = p;
            }
            if (hit == null) return;

            hit.Matched = true;
            hit.Box = __instance;
            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightChat: {hit.Scene} 气泡已匹配（{hit.CharacterKey}，原文「{Truncate(hit.Original)}」），等待 AI 文本");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] NightChatPatch.DialogBoxSetMessage 异常: {ex}");
        }
    }

    // ---- 登记 + 生成 + watcher ----

    /// <summary>主线程预取上下文并发起 AI 生成；dish/rating 仅评价场景非空（food 非空时附带料理简介/配方食材变量）。speaker 可为 null（匹配退化为仅按原文）。</summary>
    private static void RegisterPending(string characterKey, int characterId, ChatScene scene,
        string original, Transform? speaker, string? dish, string? rating,
        GameData.Core.Collections.Sellable? food = null)
    {
        // 首次对话时一次性预建全量稀客别名表（已建则直接返回，失败下次重试，不影响流程）
        SpecialGuestNames.EnsurePrebuilt(PluginContext.Personas);

        var extra = new Dictionary<string, string>
        {
            ["characterKey"] = characterKey,
            ["location"] = "夜晚居酒屋营业中",
            ["news"] = NewspaperReader.GetTodayNewsSummary(),
            // 长期记忆：该角色最近几段对话的原文尾部（空串=无记忆，prompt 侧判空）
            ["memories"] = MemoryStore.GetRecentText(characterKey, PluginContext.Settings.MemoryInjectCount),
        };
        if (food != null)
        {
            // 评价场景：菜品名 + 料理简介 + 配方食材（名 / 名+简介）
            var dishName = food.Text?.Name;
            if (!string.IsNullOrWhiteSpace(dishName)) extra["dish"] = dishName.Trim();
            var dishDesc = DishInfo.GetDescription(food);
            if (dishDesc.Length > 0) extra["dishDesc"] = dishDesc;
            var ingredients = DishInfo.GetIngredients(food, withDesc: false);
            if (ingredients.Length > 0) extra["dishIngredients"] = ingredients;
            var ingredientsDesc = DishInfo.GetIngredients(food, withDesc: true);
            if (ingredientsDesc.Length > 0) extra["dishIngredientsDesc"] = ingredientsDesc;
        }
        else if (!string.IsNullOrWhiteSpace(dish))
        {
            extra["dish"] = dish;
        }
        if (!string.IsNullOrWhiteSpace(rating)) extra["rating"] = rating;

        var context = new GenerationContext
        {
            CharacterId = characterId,
            CharacterName = characterKey,
            Scene = scene,
            GameTime = DialogPannelPatch.GetGameTimeText(),
            Language = DialogPannelPatch.GetCurrentLanguage(),
            KizunaLevel = GetKizunaLevel(characterKey),
            MaxLength = PluginContext.Settings.MaxLength,
            Extra = extra,
        };

        var pending = new PendingBubble
        {
            CharacterKey = characterKey,
            Scene = scene,
            Original = original,
            SpeakerPtr = SafePtr(speaker),
            AiTask = DialogPannelPatch.StartGeneration(context),
        };
        Pending.Add(pending);
        StartWatcher(pending);

        PluginContext.Log.LogInfo(
            $"[MystiaAI] NightChat: 登记 {scene}（{characterKey} id={characterId} 羁绊={context.KizunaLevel}" +
            $"{(dish != null ? $" 菜品「{dish}」评价「{rating}」" : "")}），原文「{Truncate(original)}」，AI 生成中");
    }

    /// <summary>异步等待生成结果（复刻 DialogPannelPatch.StartWatcher 模式），尘埃落定回主线程终态化。</summary>
    private static void StartWatcher(PendingBubble pending)
    {
        var timeoutSeconds = PluginContext.Settings.TimeoutSeconds;
        _ = Task.Run(async () =>
        {
            string? aiText = null;
            try
            {
                var timeout = Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1f, timeoutSeconds)));
                var finished = await Task.WhenAny(pending.AiTask, timeout).ConfigureAwait(false);
                if (finished == pending.AiTask && pending.AiTask.IsCompletedSuccessfully)
                    aiText = pending.AiTask.Result;
            }
            catch (Exception ex)
            {
                PluginContext.Log.LogError($"[MystiaAI] NightChat watcher 异常: {ex}");
            }

            // 回主线程改写 UI（线程池里绝不能碰 Unity 对象）
            var captured = aiText;
            MainThreadDispatcher.Post(() => FinalizeBubble(pending, captured));
        });
    }

    /// <summary>主线程终态化：成功且气泡仍显示原文 → 原地改写 AI 文本；其余一律不动（原文已是回退态）。</summary>
    private static void FinalizeBubble(PendingBubble pending, string? aiText)
    {
        try
        {
            if (pending.Resolved) return;
            pending.Resolved = true;
            Pending.Remove(pending);

            if (aiText == null)
            {
                PluginContext.Log.LogWarning(
                    $"[MystiaAI] NightChat: {pending.Scene}（{pending.CharacterKey}）生成超时/失败，保持原文");
                return;
            }
            if (!pending.Matched || pending.Box == null)
            {
                PluginContext.Log.LogWarning(
                    $"[MystiaAI] NightChat: {pending.Scene}（{pending.CharacterKey}）气泡未匹配到，AI 文本丢弃（原文已显示）");
                return;
            }

            var box = pending.Box;
            if (UnityObjectGuard.IsDead(box)) return;           // 气泡已淡出销毁
            var tmp = box.text;
            if (UnityObjectGuard.IsDead(tmp)) return;
            if (tmp.text != pending.Original) return;            // 气泡已被复用/文本已变，防串

            tmp.text = aiText;
            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightChat: {pending.Scene}（{pending.CharacterKey}）气泡原地改写为 AI 文本「{Truncate(aiText)}」");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] NightChatPatch.FinalizeBubble 异常: {ex}");
        }
    }

    // ---- 上下文预取辅助（全部主线程调用，异常只记日志）----

    /// <summary>评价等级 → 中文描述（prompt 用；Null 等返回空串表示不生成）。</summary>
    private static string RatingText(GuestGroupController.EvaluationResult result)
    {
        switch (result)
        {
            case GuestGroupController.EvaluationResult.Exbad: return "极差评价";
            case GuestGroupController.EvaluationResult.Bad: return "差评";
            case GuestGroupController.EvaluationResult.Normal: return "普通评价";
            case GuestGroupController.EvaluationResult.Good: return "好评";
            case GuestGroupController.EvaluationResult.ExGood: return "极好评";
            default: return string.Empty;
        }
    }

    /// <summary>刚吃完的菜品：客人订单栈顶（最新一单）的实际出餐 Sellable；取不到返回 null。</summary>
    private static GameData.Core.Collections.Sellable? ResolveServFood(GuestGroupController controller)
    {
        try
        {
            var food = controller.PeekOrders()?.ServFood;
            if (food == null)
                PluginContext.Log.LogWarning("[MystiaAI] NightChat: 评价场景取出餐失败（订单栈顶无出餐），料理变量用兜底");
            return food;
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] NightChat: 评价场景取出餐异常（料理变量用兜底）: {ex.Message}");
            return null;
        }
    }

    private static int GetKizunaLevel(string characterKey)
    {
        try
        {
            RunTimeAlbum.GetCharacterKizuna(characterKey, out _, out var level);
            // 未收录的角色返回 -1（RunTimeAlbum.GetCharacterKizuna 语义），按未知=0 处理
            return level < 0 ? 0 : level;
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] NightChat: 读取羁绊等级失败（{characterKey}，按 0 处理）: {ex.Message}");
            return 0;
        }
    }

    /// <summary>安全取 Unity 对象 native 指针；对象缺失/已死返回零值。</summary>
    private static IntPtr SafePtr(UnityEngine.Object? obj)
    {
        if (UnityObjectGuard.IsDead(obj)) return IntPtr.Zero;
        try { return obj!.Pointer; } catch { return IntPtr.Zero; }
    }

    /// <summary>取文本前 10 字供日志对照。</summary>
    private static string Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "<空>";
        return s.Length <= 10 ? s : s.Substring(0, 10) + "…";
    }
}
