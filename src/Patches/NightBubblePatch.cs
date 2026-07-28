using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Common.CharacterUtility;
using GameData.Core.Collections.NightSceneUtility;
using GameData.CoreLanguage.Collections;
using GameData.RunTime.Common;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MystiaAI.Core;
using NightScene.GuestManagementUtility;
using UnityEngine;
using DialogBoxUI = NightScene.UI.GuestManagementUtility.DialogBoxUI;
using EvalulationBoxUI = NightScene.UI.GuestManagementUtility.EvalulationBoxUI;
using StructPtrString = DEYU.Utils.UnityEngineExtensionStatic.StructPtr<string>;

namespace MystiaAI.Patches;

/// <summary>
/// 夜晚营业气泡 AI 替换（纯安全锚点方案，NightChatPatch/NightDiagPatch 的后继）。
///
/// 硬性原则（ARCHITECTURE.md 第 5 章血泪教训）：
/// - 不 patch 任何夜场景方法；帧泵挂进 DialogPannelPatch 的 EventSystem.Update postfix 同链；
/// - 只在营业相位工作（Work/BeforeChallengeStart/Challenge/YuyukoStageChange），读取异常按非营业处理；
/// - 一切 IL2CPP 读取都在主线程帧回调；线程池只碰托管字符串；AI 回调一律 MainThreadDispatcher.Post；
/// - 不订阅任何游戏事件（HasEvaluated 轮询翻转检测），零 IL2CPP 委托移交；
/// - 每个实例逐条 try-catch；任何失败保持原文（原文已先行显示，天然回退）。
///
/// 工作流（依据 docs/game-api.md 第 G 章可行性调查）：
/// 平时每 30 帧（评价窗口存活或有未决 Pending 时提频到 5 帧）：
/// 相位闸门 → 刷新控制器状态（HasEvaluated 翻转检测 + 提前缓存菜品；
///   翻转当刻立即登记评价预生成，rating 此刻读不到故 prompt 降级不带）
/// → FindObjectsOfType&lt;DialogBoxUI&gt;(true) 扫气泡 → 逐个甄别：
///   评价气泡（EvalulationBoxUI）：followTarget 反查客人 + HasEvaluated 翻转窗口内
///     → 优先认领预生成任务（AI 已就绪则当帧直接改写，无原文闪现；未就绪写占位符「……」）；
///     无预生成则登记 Evaluation 生成 + 占位符，等级由 box.sprite 与 5 套皮肤 Sprite 引用比较反推；
///     窗口外的一律不动（符卡 bark 同型气泡，诊断日志验证 bark 不翻转 HasEvaluated）；
///   闲聊气泡（普通 DialogBoxUI）：followTarget 反查客人 + 文本命中闲聊池
///     （稀客 SpecialConversation / 普客 NormalConversation，普客受 NormalGuestAiEnabled 开关控制）
///     → 登记 NightChat 生成 + 占位符（未命中的文本打诊断日志供校准匹配规则）。
/// AI 文本到位 → 主线程终态化：气泡活着且文本仍是原文或占位符 → tmp.text 原地改写；
/// 生成失败 → 占位符还原原文（原文是天然回退态）。
/// 改写成功登记 RewrittenTexts 让下一轮扫描认出自己的 AI 文本（防「未命中池/疑似 bark」误报）；
/// 预生成等不到气泡（客人走了/窗口过期）由 EvictStaleAwaitingEval 淘汰，不泄漏。
/// </summary>
internal static class NightBubblePatch
{
    private const int FrameInterval = 30;        // 扫描间隔（帧）
    private const int BoostFrameInterval = 5;    // 提频间隔（评价窗口存活或有未决 Pending 时，FindObjectsOfType 开销大，仅限这两种情况）
    private const int StartupWarmupFrames = 600; // 启动预热（约 10s，读档半成品期绝不碰场景）
    private const int EvalWindowFrames = 600;    // HasEvaluated 翻转后评价气泡认领窗口（约 20s）
    private const string Placeholder = "……";     // AI 未就绪时的占位文本（生成失败会还原原文）

    /// <summary>一条待改写的营业气泡：原文已先行显示（或被占位符覆盖），AI 文本到位后原地替换。</summary>
    private sealed class PendingBubble
    {
        public string CharacterKey = string.Empty; // 稀客 stringId（如 "Rumia"）/ 普客本地化名
        public ChatScene Scene;                    // NightChat / Evaluation
        public string Original = string.Empty;     // 先行显示的原文（改写前的防串校验基线；预生成待绑定时为空）
        public Task<string> AiTask = Task.FromResult(string.Empty);
        public IntPtr BoxPtr;                      // 气泡 native 指针（去重 + 存活校验）
        public DialogBoxUI? Box;                   // null = 评价预生成待绑定（气泡还没出现）
        public IntPtr ControllerPtr;               // 预生成评价的来源控制器指针（绑定/淘汰用）
        public bool Resolved;
        public bool AiDone;                        // watcher 已尘埃落定（成功/失败/超时）
        public string? ReadyText;                  // AI 结果（null = 失败/超时）
    }

    /// <summary>每个在场客人控制器的轮询状态（按 controller.Pointer 索引）。</summary>
    private sealed class ControllerState
    {
        public GuestGroupController Controller = null!;
        public bool HasEvaluated;                  // 上轮观察值
        public int EvalFlippedFrame = -1;          // HasEvaluated false→true 翻转发生的帧号
        public string Dish = string.Empty;         // 最近观察到的菜品名（提前缓存，评价时订单可能已出栈）
    }

    private static int _frame;
    private static int _nonBusinessTicks;
    private static bool _insideFrame;            // 防重入闸门

    /// <summary>待 AI 文本的气泡。只在主线程访问。</summary>
    private static readonly List<PendingBubble> Pending = new();

    /// <summary>在场控制器状态表。</summary>
    private static readonly Dictionary<IntPtr, ControllerState> Controllers = new();

    /// <summary>气泡指针 → 已处理过的文本（同一气泡同一文本只甄别一次）。</summary>
    private static readonly Dictionary<IntPtr, string> SeenBubbles = new();

    /// <summary>
    /// 气泡指针 → 已原地改写的 AI 文本。改写后下一轮扫描会再见到该气泡（指针同、文本变），
    /// 命中此表且文本一致说明是自己的 AI 文本，直接跳过并同步 SeenBubbles，避免误报
    /// 「未命中闲聊池」/「疑似符卡 bark」；文本不一致说明气泡被对象池复用，删条目照常甄别。
    /// </summary>
    private static readonly Dictionary<IntPtr, string> RewrittenTexts = new();

    /// <summary>
    /// 不注册任何 Harmony patch（方案核心）。Install 只做成员存在性自检，
    /// 让「反编译签名与游戏实际不符」在启动日志里可见而不是运行时静默失效。
    /// </summary>
    public static void Install(Harmony harmony)
    {
        Check(HasMember(typeof(DialogBoxUI), "text"), "DialogBoxUI.text");
        Check(HasMember(typeof(DialogBoxUI), "m_WorldSpaceUITracker"), "DialogBoxUI.m_WorldSpaceUITracker");
        Check(HasMember(typeof(Common.WorldSpaceUITracker), "m_FollowTarget"), "WorldSpaceUITracker.m_FollowTarget");
        Check(HasMember(typeof(EvalulationBoxUI), "exGoodSkin"), "EvalulationBoxUI.exGoodSkin");
        Check(HasMember(typeof(EvalulationBoxUI), "box"), "EvalulationBoxUI.box");
        Check(HasMember(typeof(GuestsManager), "AllPresentedGuestGroupController"), "GuestsManager.AllPresentedGuestGroupController");
        Check(HasMember(typeof(GuestGroupController), "guestInstances"), "GuestGroupController.guestInstances");
        Check(HasMember(typeof(GuestGroupController), "HasEvaluated"), "GuestGroupController.HasEvaluated");
        Check(typeof(GuestGroupController).GetMethod("PeekOrders") != null, "GuestGroupController.PeekOrders");
        Check(HasMember(typeof(NightSceneLanguage), "SpecialConversation"), "NightSceneLanguage.SpecialConversation");
        Check(HasMember(typeof(NightSceneLanguage), "NormalConversation"), "NightSceneLanguage.NormalConversation");
        Check(HasMember(typeof(NormalGuestsController), "NormalGuestsGroups"), "NormalGuestsController.NormalGuestsGroups");
        PluginContext.Log.LogInfo("[MystiaAI] NightBubble 注册完毕（零夜场景 patch，帧泵挂入 EventSystem.Update 同链）");

        static void Check(bool ok, string name)
        {
            if (ok)
                PluginContext.Log.LogInfo($"[MystiaAI] NightBubble 自检通过: {name}");
            else
                PluginContext.Log.LogError($"[MystiaAI] NightBubble 自检失败: 解析不到 {name}，对应功能不生效！");
        }

        // Il2CppInterop 包装类把 IL2CPP 字段也生成为 managed 属性（get/set 包装 native 字段访问），
        // 且成员可能声明在基类包装类上（如 EvalulationBoxUI 的 text 在 DialogBoxUI 上）——
        // 因此字段/属性都查，并沿基类链（同为包装类）逐级上溯。
        static bool HasMember(Type type, string name)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.GetField(name, Flags) != null) return true;
                if (t.GetProperty(name, Flags) != null) return true;
            }
            return false;
        }
    }

    /// <summary>帧泵入口：由 DialogPannelPatch.EventSystemUpdate_Postfix 同链调用（主线程）。</summary>
    internal static void OnEventSystemFrame()
    {
        try
        {
            if (!PluginContext.Settings.Enabled) return;
            var f = ++_frame;
            if (f < StartupWarmupFrames || f % (NeedsBoost() ? BoostFrameInterval : FrameInterval) != 0) return;
            if (_insideFrame) return; // 防重入：帧回调读游戏数据意外触发重入时直接跳过
            _insideFrame = true;
            try
            {
                Tick();
            }
            finally
            {
                _insideFrame = false;
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] NightBubble 帧回调异常: {ex}");
        }
    }

    // ---- 帧主流程 ----

    /// <summary>是否需要提频扫描：有未决 Pending（等 AI 或等绑定），或任一控制器评价翻转窗口存活。</summary>
    private static bool NeedsBoost()
    {
        foreach (var p in Pending)
            if (!p.Resolved) return true;
        foreach (var kv in Controllers)
        {
            var flip = kv.Value.EvalFlippedFrame;
            if (flip >= 0 && _frame - flip <= EvalWindowFrames) return true;
        }
        return false;
    }

    private static void Tick()
    {
        if (!IsBusinessPhase())
        {
            // 连续离开营业相位约 10s 后清状态（跨天/切场景后旧指针全部作废）
            if (++_nonBusinessTicks >= 10 && (Controllers.Count > 0 || SeenBubbles.Count > 0))
            {
                Controllers.Clear();
                SeenBubbles.Clear();
                RewrittenTexts.Clear();
                // 未绑定的预生成任务等不到气泡了，一并淘汰（已绑定的由 watcher 终态化，气泡死亡自然不动）
                foreach (var p in Pending)
                    if (p.Box == null) { p.Resolved = true; _scratchPending.Add(p); }
                foreach (var p in _scratchPending) Pending.Remove(p);
                _scratchPending.Clear();
                PluginContext.Log.LogInfo("[MystiaAI] NightBubble: 离开营业相位，状态已清空");
            }
            return;
        }
        _nonBusinessTicks = 0;

        var manager = GuestsManager.Instance;
        if (UnityObjectGuard.IsDead(manager)) return;

        RefreshControllers(manager!);
        EvictStaleAwaitingEval();
        SweepBubbles();
    }

    /// <summary>淘汰等不到气泡的预生成评价任务：来源控制器已离场，或翻转窗口已过期。</summary>
    private static void EvictStaleAwaitingEval()
    {
        _scratchPending.Clear();
        foreach (var p in Pending)
        {
            if (p.Resolved || p.Box != null) continue;
            if (!Controllers.TryGetValue(p.ControllerPtr, out var st) ||
                st.EvalFlippedFrame < 0 || _frame - st.EvalFlippedFrame > EvalWindowFrames)
                _scratchPending.Add(p);
        }
        foreach (var p in _scratchPending)
        {
            p.Resolved = true;
            Pending.Remove(p);
            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightBubble: 预生成评价（{p.CharacterKey}）在窗口内未等到气泡，任务淘汰");
        }
        _scratchPending.Clear();
    }

    private static readonly List<PendingBubble> _scratchPending = new();

    /// <summary>营业相位闸门：只读一个 static 枚举属性，任何异常一律视为非营业。</summary>
    private static bool IsBusinessPhase()
    {
        try
        {
            var phase = RunTimeScheduler.CurrentGamePhase;
            return phase is RunTimeScheduler.GamePhase.Work
                or RunTimeScheduler.GamePhase.BeforeChallengeStart
                or RunTimeScheduler.GamePhase.Challenge
                or RunTimeScheduler.GamePhase.YuyukoStageChange;
        }
        catch
        {
            return false;
        }
    }

    // ---- 控制器状态刷新：HasEvaluated 翻转检测 + 菜品提前缓存 ----

    private static void RefreshControllers(GuestsManager manager)
    {
        Il2CppSystem.Collections.Generic.HashSet<GuestGroupController>? presented;
        try
        {
            presented = manager.AllPresentedGuestGroupController;
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] NightBubble: 读取在场控制器列表失败: {ex.Message}");
            return;
        }
        if (presented == null) return;

        var alive = new HashSet<IntPtr>();
        foreach (var controller in presented)
        {
            try
            {
                if (controller == null) continue;
                var ptr = controller.Pointer;
                if (ptr == IntPtr.Zero) continue;
                alive.Add(ptr);

                if (!Controllers.TryGetValue(ptr, out var state))
                {
                    state = new ControllerState { Controller = controller };
                    Controllers[ptr] = state;
                }

                // HasEvaluated 翻转检测（吃饭评价完成的标志；符卡 bark 预期不翻转——待实测验证）
                var evaluated = controller.HasEvaluated;
                if (evaluated && !state.HasEvaluated)
                {
                    state.EvalFlippedFrame = _frame;
                    PluginContext.Log.LogInfo(
                        $"[MystiaAI] NightBubble: 检测到评价完成翻转（{DescribeController(controller)}），" +
                        $"后续 {EvalWindowFrames} 帧内的评价气泡按吃饭评价认领");
                    // 翻转即预生成：气泡出现时 AI 文本往往已就绪，第一轮扫到直接改写，消除原文闪现
                    RegisterPreEvaluation(controller, state);
                }
                state.HasEvaluated = evaluated;

                // 菜品提前缓存：订单在时就记下来，评价气泡弹出时订单可能已出栈
                var dish = ReadDishName(controller);
                if (dish.Length > 0) state.Dish = dish;
            }
            catch (Exception ex)
            {
                // 单个控制器（含包装已被 GC 的）失败不影响其他
                PluginContext.Log.LogWarning($"[MystiaAI] NightBubble: 单控制器状态刷新失败: {ex.Message}");
            }
        }

        // 淘汰已离场的控制器（指针本轮未出现）
        _stalePtrs.Clear();
        foreach (var ptr in Controllers.Keys)
            if (!alive.Contains(ptr)) _stalePtrs.Add(ptr);
        foreach (var ptr in _stalePtrs)
            Controllers.Remove(ptr);
    }

    private static readonly List<IntPtr> _stalePtrs = new();

    /// <summary>读取订单栈顶出餐菜品名；取不到返回空串（调用方保留旧缓存）。</summary>
    private static string ReadDishName(GuestGroupController controller)
    {
        try
        {
            var name = controller.PeekOrders()?.ServFood?.Text?.Name;
            return string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        }
        catch
        {
            return string.Empty; // 订单为空栈等常规情况，静默
        }
    }

    // ---- 气泡扫描与甄别 ----

    private static void SweepBubbles()
    {
        var all = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(typeof(DialogBoxUI)), true);

        var seenThisSweep = new HashSet<IntPtr>();
        foreach (var obj in all)
        {
            try
            {
                var box = obj.TryCast<DialogBoxUI>();
                if (UnityObjectGuard.IsDead(box)) continue;
                var go = box!.gameObject;
                if (UnityObjectGuard.IsDead(go) || !go.activeInHierarchy) continue;

                var tmp = box.text;
                if (UnityObjectGuard.IsDead(tmp)) continue;
                var text = tmp!.text;
                if (string.IsNullOrWhiteSpace(text)) continue;

                var boxPtr = box.Pointer;
                seenThisSweep.Add(boxPtr);

                // 自己改写过的气泡：文本仍是 AI 文本 → 跳过并同步 SeenBubbles（防重扫误报）；
                // 文本已变 → 气泡被复用，删条目后照常甄别
                if (RewrittenTexts.TryGetValue(boxPtr, out var aiText))
                {
                    if (aiText == text)
                    {
                        SeenBubbles[boxPtr] = text;
                        continue;
                    }
                    RewrittenTexts.Remove(boxPtr);
                }

                // 同一气泡同一文本只甄别一次；已登记等 AI 的也不重复
                if (SeenBubbles.TryGetValue(boxPtr, out var handled) && handled == text) continue;
                if (IsPending(boxPtr)) continue;
                SeenBubbles[boxPtr] = text;

                ClassifyBubble(box, boxPtr, text);
            }
            catch (Exception ex)
            {
                PluginContext.Log.LogWarning($"[MystiaAI] NightBubble: 单气泡处理失败: {ex.Message}");
            }
        }

        // 淘汰已消失的气泡
        _stalePtrs.Clear();
        foreach (var ptr in SeenBubbles.Keys)
            if (!seenThisSweep.Contains(ptr)) _stalePtrs.Add(ptr);
        foreach (var ptr in _stalePtrs)
            SeenBubbles.Remove(ptr);

        _stalePtrs.Clear();
        foreach (var ptr in RewrittenTexts.Keys)
            if (!seenThisSweep.Contains(ptr)) _stalePtrs.Add(ptr);
        foreach (var ptr in _stalePtrs)
            RewrittenTexts.Remove(ptr);
    }

    private static bool IsPending(IntPtr boxPtr)
    {
        foreach (var p in Pending)
            if (!p.Resolved && p.BoxPtr == boxPtr) return true;
        return false;
    }

    /// <summary>新气泡甄别：评价（翻转窗口内）/ 闲聊（池命中）登记生成，其余保持原文。</summary>
    private static void ClassifyBubble(DialogBoxUI box, IntPtr boxPtr, string text)
    {
        var evalBox = box.TryCast<EvalulationBoxUI>();
        var controller = FindControllerByFollowTarget(box);
        if (controller == null)
        {
            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightBubble: 气泡（{(evalBox != null ? "评价型" : "普通型")}）" +
                $"followTarget 未匹配到任何在场客人，跳过 文本「{Truncate(text)}」");
            return;
        }

        var ptr = controller.Pointer;
        Controllers.TryGetValue(ptr, out var state);

        var special = controller.TryCast<SpecialGuestsController>();
        if (special?.SpecialGuest != null)
        {
            var guest = special.SpecialGuest;
            ClassifyGuestBubble(box, boxPtr, ptr, text, evalBox, state, guest.stringId, guest.Id,
                isSpecial: true, idleHit: IsIdleChatText(guest.Id, text));
            return;
        }

        // 普通客人：开关关闭时保持原文（稀客不受此开关影响）
        var normal = controller.TryCast<NormalGuestsController>();
        if (normal == null || !PluginContext.Settings.NormalGuestAiEnabled)
        {
            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightBubble: 气泡属普通客人，跳过 文本「{Truncate(text)}」");
            return;
        }

        var identity = FindNormalGuestIdentity(normal);
        if (identity == null)
        {
            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightBubble: 普通客人身份读取失败，跳过 文本「{Truncate(text)}」");
            return;
        }

        ClassifyGuestBubble(box, boxPtr, ptr, text, evalBox, state, identity.Value.Name, identity.Value.Id,
            isSpecial: false, idleHit: IsNormalIdleChatText(normal, text));
    }

    /// <summary>单个客人（稀客/普客）气泡的甄别与登记；idleHit 由调用方按各自闲聊池判定。</summary>
    private static void ClassifyGuestBubble(DialogBoxUI box, IntPtr boxPtr, IntPtr controllerPtr, string text,
        EvalulationBoxUI? evalBox, ControllerState? state, string characterKey, int characterId,
        bool isSpecial, bool idleHit)
    {
        if (evalBox != null)
        {
            // 评价型气泡：只有 HasEvaluated 翻转窗口内的才算吃饭评价（bark 不动）
            if (state == null || state.EvalFlippedFrame < 0 || _frame - state.EvalFlippedFrame > EvalWindowFrames)
            {
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] NightBubble: 评价型气泡不在评价翻转窗口内（疑似符卡 bark/残留），" +
                    $"保持原文 角色={characterKey} 文本「{Truncate(text)}」");
                return;
            }
            var rating = RatingFromSkin(evalBox);

            // 优先认领翻转时登记的预生成任务
            var pre = FindAwaitingEval(controllerPtr);
            if (pre != null)
            {
                if (pre.AiDone && pre.ReadyText == null)
                {
                    // 预生成已失败/超时：淘汰，走下方新登记
                    pre.Resolved = true;
                    Pending.Remove(pre);
                }
                else
                {
                    pre.Box = box;
                    pre.BoxPtr = boxPtr;
                    pre.Original = text;
                    if (pre.AiDone)
                    {
                        // AI 文本已就绪：当帧直接改写，用户看不到原文
                        TryFinalize(pre);
                    }
                    else
                    {
                        WritePlaceholder(box);
                        PluginContext.Log.LogInfo(
                            $"[MystiaAI] NightBubble: 评价气泡认领预生成（{pre.CharacterKey} 评价「{rating}」），AI 未就绪，已写占位符");
                    }
                    return;
                }
            }

            RegisterBubble(box, boxPtr, characterKey, characterId, ChatScene.Evaluation, text,
                state.Dish, rating, isSpecial);
            return;
        }

        // 普通型气泡：文本命中闲聊池才算客人闲聊
        if (idleHit)
        {
            RegisterBubble(box, boxPtr, characterKey, characterId, ChatScene.NightChat, text,
                null, null, isSpecial);
        }
        else
        {
            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightBubble: 普通气泡文本未命中闲聊池，保持原文 " +
                $"角色={characterKey} 文本「{Truncate(text)}」（校准样本）");
        }
    }

    /// <summary>取普通客人组内第一个可读客人的身份（本地化名 + Id，人设走 Default 兜底）；全部不可读返回 null。</summary>
    private static (string Name, int Id)? FindNormalGuestIdentity(NormalGuestsController controller)
    {
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<NormalGuest>? guests;
        try
        {
            guests = controller.NormalGuestsGroups;
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] NightBubble: 读取普通客人组失败: {ex.Message}");
            return null;
        }
        if (guests == null) return null;
        foreach (var g in guests)
        {
            if (g == null) continue;
            try
            {
                var name = g.Text?.Name;
                if (string.IsNullOrWhiteSpace(name)) name = $"普客#{g.Id}";
                return (name.Trim(), g.Id);
            }
            catch { /* 单个客人读取失败，试下一个 */ }
        }
        return null;
    }

    /// <summary>followTarget 反查控制器：向上走父链，与各控制器的 guestInstances transform 比对指针。</summary>
    private static GuestGroupController? FindControllerByFollowTarget(DialogBoxUI box)
    {
        Transform? follow;
        try
        {
            follow = box.m_WorldSpaceUITracker?.m_FollowTarget;
        }
        catch
        {
            return null;
        }
        if (UnityObjectGuard.IsDead(follow)) return null;

        // 收集 followTarget 及其全部祖先的指针（followTarget 可能是客人可视物的子节点）
        _followChain.Clear();
        try
        {
            var cur = follow;
            for (var i = 0; i < 10 && !UnityObjectGuard.IsDead(cur); i++)
            {
                _followChain.Add(cur!.Pointer);
                cur = cur.parent;
            }
        }
        catch { /* 链断在哪算哪 */ }
        if (_followChain.Count == 0) return null;

        foreach (var state in Controllers.Values)
        {
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<AStarInputGeneratorComponent>? instances;
            try
            {
                instances = state.Controller.guestInstances;
            }
            catch
            {
                continue;
            }
            if (instances == null) continue;
            foreach (var inst in instances)
            {
                try
                {
                    if (UnityObjectGuard.IsDead(inst)) continue;
                    var t = inst!.transform;
                    if (!UnityObjectGuard.IsDead(t) && _followChain.Contains(t.Pointer))
                        return state.Controller;
                }
                catch { /* 单实例失败跳过 */ }
            }
        }
        return null;
    }

    private static readonly List<IntPtr> _followChain = new();

    /// <summary>评价等级反推：当前 box.sprite 与 5 套皮肤的 box Sprite 引用比较。取不到返回空串（prompt 降级）。</summary>
    private static string RatingFromSkin(EvalulationBoxUI box)
    {
        try
        {
            var current = SafePtr(box.box?.sprite);
            if (current == IntPtr.Zero) return string.Empty;
            if (current == SafePtr(box.exBadSkin?.box)) return "极差";
            if (current == SafePtr(box.badSkin?.box)) return "差评";
            if (current == SafePtr(box.normalSkin?.box)) return "普通";
            if (current == SafePtr(box.goodSkin?.box)) return "好评";
            if (current == SafePtr(box.exGoodSkin?.box)) return "完美好评";
            PluginContext.Log.LogWarning("[MystiaAI] NightBubble: 评价气泡皮肤未匹配到任何已知等级（rating 用兜底）");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] NightBubble: 读取评价皮肤异常（rating 用兜底）: {ex.Message}");
        }
        return string.Empty;
    }

    /// <summary>稀客闲聊池成员性判定（SpecialConversation），池读取失败按不命中处理。</summary>
    private static bool IsIdleChatText(int guestId, string text)
    {
        Il2CppSystem.Collections.Generic.Dictionary<int, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<StructPtrString>>? pool;
        try
        {
            pool = NightSceneLanguage.SpecialConversation;
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] NightBubble: 读取稀客闲聊池失败（guestId={guestId}）: {ex.Message}");
            return false;
        }
        return PoolContains(pool, guestId, text);
    }

    /// <summary>普客闲聊池成员性判定（NormalConversation）：对组内每个普通客人 Id 都试一次。</summary>
    private static bool IsNormalIdleChatText(NormalGuestsController controller, string text)
    {
        Il2CppSystem.Collections.Generic.Dictionary<int, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<StructPtrString>>? pool;
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<NormalGuest>? guests;
        try
        {
            pool = NightSceneLanguage.NormalConversation;
            guests = controller.NormalGuestsGroups;
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] NightBubble: 读取普客闲聊池失败: {ex.Message}");
            return false;
        }
        if (guests == null) return false;
        foreach (var g in guests)
        {
            if (g == null) continue;
            int id;
            try
            {
                id = g.Id;
            }
            catch
            {
                continue;
            }
            if (PoolContains(pool, id, text)) return true;
        }
        return false;
    }

    /// <summary>闲聊池成员性判定（宽松匹配：全等/互含），池读取失败按不命中处理。</summary>
    private static bool PoolContains(
        Il2CppSystem.Collections.Generic.Dictionary<int, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<StructPtrString>>? pool,
        int guestId, string text)
    {
        if (pool == null) return false;
        try
        {
            if (!pool.TryGetValue(guestId, out var fragments) || fragments == null) return false;
            foreach (var fragment in fragments)
            {
                string? s;
                try
                {
                    s = fragment?.value?.Trim();
                }
                catch
                {
                    continue;
                }
                if (string.IsNullOrEmpty(s) || s.Length < 2) continue;
                if (text == s || text.Contains(s) || s.Contains(text)) return true;
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] NightBubble: 读取闲聊池失败（guestId={guestId}）: {ex.Message}");
        }
        return false;
    }

    // ---- 登记 + 生成 + watcher（模式复刻 NightChatPatch）----

    /// <summary>构建生成上下文并发起 AI 生成 + watcher；成功返回已入队 Pending，失败返回 null（保持原文）。</summary>
    private static PendingBubble? CreatePending(string characterKey, int characterId,
        ChatScene scene, string original, string? dish, string? rating, bool isSpecial)
    {
        try
        {
            // 学习 stringId → 中文名 别名（仅稀客；失败自动停用，不影响流程）
            if (isSpecial)
                SpecialGuestNames.LearnAlias(PluginContext.Personas, characterKey, characterId);

            var extra = new Dictionary<string, string>
            {
                ["characterKey"] = characterKey,
                ["location"] = "夜晚居酒屋营业中",
                ["news"] = NewspaperReader.GetTodayNewsSummary(),
                // 人设分类：普客走 NormalGuest 分类人设，稀客走角色专属/SpecialGuest 兜底
                ["personaCategory"] = isSpecial ? PersonaStore.CategorySpecialGuest : PersonaStore.CategoryNormalGuest,
            };
            if (!string.IsNullOrWhiteSpace(dish)) extra["dish"] = dish;
            if (!string.IsNullOrWhiteSpace(rating)) extra["rating"] = rating;

            var context = new GenerationContext
            {
                CharacterId = characterId,
                CharacterName = characterKey,
                Scene = scene,
                GameTime = DialogPannelPatch.GetGameTimeText(),
                Language = DialogPannelPatch.GetCurrentLanguage(),
                KizunaLevel = isSpecial ? GetKizunaLevel(characterKey) : 0,
                MaxLength = PluginContext.Settings.MaxLength,
                Extra = extra,
            };

            var pending = new PendingBubble
            {
                CharacterKey = characterKey,
                Scene = scene,
                Original = original,
                AiTask = DialogPannelPatch.StartGeneration(context),
            };
            Pending.Add(pending);
            StartWatcher(pending);

            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightBubble: 登记 {scene}（{characterKey} id={characterId} 羁绊={context.KizunaLevel}" +
                $"{(dish != null ? $" 菜品「{dish}」" : "")}{(rating != null ? $" 评价「{rating}」" : "")}），" +
                $"{(original.Length > 0 ? $"原文「{Truncate(original)}」，" : "")}AI 生成中");
            return pending;
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] NightBubble 登记异常（保持原文）: {ex}");
            return null;
        }
    }

    /// <summary>气泡已出现的登记路径（闲聊/未预生成的评价）：登记后立即写占位符，消除原文停留。</summary>
    private static void RegisterBubble(DialogBoxUI box, IntPtr boxPtr, string characterKey, int characterId,
        ChatScene scene, string original, string? dish, string? rating, bool isSpecial)
    {
        var pending = CreatePending(characterKey, characterId, scene, original, dish, rating, isSpecial);
        if (pending == null) return;
        pending.BoxPtr = boxPtr;
        pending.Box = box;
        WritePlaceholder(box);
    }

    /// <summary>
    /// 评价预生成：HasEvaluated 翻转当刻登记，气泡出现时 AI 往往已就绪 → 第一轮扫到直接改写。
    /// rating 此刻读不到（decomp 确认控制器没有存储本次 EvaluationResult 的字段，evaluationType 只是
    /// PostEvaluation 的闭包局部变量）→ 不带 rating 生成，prompt 降级；气泡出现时读到的 rating 仅用于日志。
    /// </summary>
    private static void RegisterPreEvaluation(GuestGroupController controller, ControllerState state)
    {
        try
        {
            var ptr = controller.Pointer;
            if (FindAwaitingEval(ptr) != null) return; // 同一控制器已有预生成任务

            string characterKey;
            int characterId;
            bool isSpecial;
            var special = controller.TryCast<SpecialGuestsController>();
            if (special?.SpecialGuest != null)
            {
                var guest = special.SpecialGuest;
                characterKey = guest.stringId;
                characterId = guest.Id;
                isSpecial = true;
            }
            else
            {
                // 普通客人同样享受预生成（受开关控制）
                if (!PluginContext.Settings.NormalGuestAiEnabled) return;
                var normal = controller.TryCast<NormalGuestsController>();
                if (normal == null) return;
                var identity = FindNormalGuestIdentity(normal);
                if (identity == null) return;
                characterKey = identity.Value.Name;
                characterId = identity.Value.Id;
                isSpecial = false;
            }

            var pending = CreatePending(characterKey, characterId, ChatScene.Evaluation,
                string.Empty, state.Dish, null, isSpecial);
            if (pending == null) return;
            pending.ControllerPtr = ptr;
            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightBubble: 评价预生成已登记（{characterKey}），等气泡出现绑定");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] NightBubble 预生成登记异常: {ex}");
        }
    }

    /// <summary>查某控制器未绑定的预生成评价任务。</summary>
    private static PendingBubble? FindAwaitingEval(IntPtr controllerPtr)
    {
        foreach (var p in Pending)
            if (!p.Resolved && p.Box == null && p.Scene == ChatScene.Evaluation && p.ControllerPtr == controllerPtr)
                return p;
        return null;
    }

    /// <summary>AI 未就绪时把气泡文本改为占位符（失败时 TryFinalize 会还原原文）。</summary>
    private static void WritePlaceholder(DialogBoxUI box)
    {
        try
        {
            var tmp = box.text;
            if (!UnityObjectGuard.IsDead(tmp)) tmp!.text = Placeholder;
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] NightBubble: 写占位符失败（保持原文）: {ex.Message}");
        }
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
                PluginContext.Log.LogError($"[MystiaAI] NightBubble watcher 异常: {ex}");
            }

            // 回主线程改写 UI（线程池里绝不能碰 Unity 对象）
            var captured = aiText;
            MainThreadDispatcher.Post(() =>
            {
                pending.AiDone = true;
                pending.ReadyText = captured;
                if (captured == null && pending.Box == null && !pending.Resolved)
                {
                    // 预生成失败且气泡尚未出现：没有绑定机会了，直接淘汰
                    pending.Resolved = true;
                    Pending.Remove(pending);
                    PluginContext.Log.LogWarning(
                        $"[MystiaAI] NightBubble: 预生成 {pending.Scene}（{pending.CharacterKey}）超时/失败，任务淘汰");
                    return;
                }
                TryFinalize(pending);
            });
        });
    }

    /// <summary>
    /// 主线程终态化：AI 已尘埃落定且气泡已绑定 → 成功则原地改写（原文或占位符都允许），
    /// 失败则把占位符还原成原文；预生成未绑定的留在队列里等气泡出现或窗口过期淘汰。
    /// </summary>
    private static void TryFinalize(PendingBubble pending)
    {
        try
        {
            if (pending.Resolved || !pending.AiDone) return;
            if (pending.Box == null) return; // 预生成待绑定
            pending.Resolved = true;
            Pending.Remove(pending);

            var aiText = pending.ReadyText;
            if (aiText == null)
            {
                PluginContext.Log.LogWarning(
                    $"[MystiaAI] NightBubble: {pending.Scene}（{pending.CharacterKey}）生成超时/失败，保持原文");
                RestoreOriginal(pending);
                return;
            }

            var box = pending.Box;
            if (UnityObjectGuard.IsDead(box)) return; // 气泡已淡出销毁
            if (box!.Pointer != pending.BoxPtr) return; // 包装复用防串
            var tmp = box.text;
            if (UnityObjectGuard.IsDead(tmp)) return;
            var current = tmp!.text;
            if (current != pending.Original && current != Placeholder) return; // 气泡已被复用/文本已变，防串

            tmp.text = aiText;
            RewrittenTexts[pending.BoxPtr] = aiText; // 登记已改写文本，下一轮扫描认出自己的 AI 文本不再误报
            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightBubble: {pending.Scene}（{pending.CharacterKey}）气泡原地改写为 AI 文本「{Truncate(aiText)}」");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] NightBubble.TryFinalize 异常: {ex}");
        }
    }

    /// <summary>生成失败回退：气泡正显示占位符时还原原文（原文本就是天然回退态）。</summary>
    private static void RestoreOriginal(PendingBubble pending)
    {
        try
        {
            var box = pending.Box;
            if (UnityObjectGuard.IsDead(box) || box!.Pointer != pending.BoxPtr) return;
            var tmp = box.text;
            if (UnityObjectGuard.IsDead(tmp)) return;
            if (tmp!.text == Placeholder && pending.Original.Length > 0)
                tmp.text = pending.Original;
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] NightBubble: 占位符还原原文失败: {ex.Message}");
        }
    }

    // ---- 辅助 ----

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
            PluginContext.Log.LogWarning($"[MystiaAI] NightBubble: 读取羁绊等级失败（{characterKey}，按 0 处理）: {ex.Message}");
            return 0;
        }
    }

    private static string DescribeController(GuestGroupController controller)
    {
        try
        {
            var guest = controller.TryCast<SpecialGuestsController>()?.SpecialGuest;
            if (guest != null) return $"稀客 {guest.stringId}";
        }
        catch { /* 忽略 */ }
        return "普通客人组";
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
