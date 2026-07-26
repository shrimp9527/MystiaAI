using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Common.CharacterUtility;
using GameData.CoreLanguage.Collections;
using GameData.RunTime.Common;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MystiaAI.Core;
using NightScene.GuestManagementUtility;
using UnityEngine;
using DialogBoxUI = NightScene.UI.GuestManagementUtility.DialogBoxUI;
using EvalulationBoxUI = NightScene.UI.GuestManagementUtility.EvalulationBoxUI;

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
/// 每 30 帧：相位闸门 → 刷新控制器状态（HasEvaluated 翻转检测 + 提前缓存菜品）
/// → FindObjectsOfType&lt;DialogBoxUI&gt;(true) 扫气泡 → 逐个甄别：
///   评价气泡（EvalulationBoxUI）：followTarget 反查稀客 + HasEvaluated 翻转窗口内
///     → 等级由 box.sprite 与 5 套皮肤 Sprite 引用比较反推 → 登记 Evaluation 生成；
///     窗口外的一律不动（符卡 bark 同型气泡，诊断日志验证 bark 不翻转 HasEvaluated）；
///   闲聊气泡（普通 DialogBoxUI）：followTarget 反查稀客 + 文本命中 SpecialConversation 池
///     → 登记 NightChat 生成（未命中的文本打诊断日志供校准匹配规则）。
/// AI 文本到位 → 主线程终态化：气泡活着且文本仍是原文 → tmp.text 原地改写。
/// </summary>
internal static class NightBubblePatch
{
    private const int FrameInterval = 30;        // 扫描间隔（帧）
    private const int StartupWarmupFrames = 600; // 启动预热（约 10s，读档半成品期绝不碰场景）
    private const int EvalWindowFrames = 600;    // HasEvaluated 翻转后评价气泡认领窗口（约 20s）

    /// <summary>一条待改写的营业气泡：原文已先行显示，AI 文本到位后原地替换。</summary>
    private sealed class PendingBubble
    {
        public string CharacterKey = string.Empty; // 稀客 stringId（如 "Rumia"）
        public ChatScene Scene;                    // NightChat / Evaluation
        public string Original = string.Empty;     // 先行显示的原文（改写前的防串校验基线）
        public Task<string> AiTask = Task.FromResult(string.Empty);
        public IntPtr BoxPtr;                      // 气泡 native 指针（去重 + 存活校验）
        public DialogBoxUI? Box;
        public bool Resolved;
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
            if (!PluginContext.Settings.Enabled.Value) return;
            var f = ++_frame;
            if (f < StartupWarmupFrames || f % FrameInterval != 0) return;
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

    private static void Tick()
    {
        if (!IsBusinessPhase())
        {
            // 连续离开营业相位约 10s 后清状态（跨天/切场景后旧指针全部作废）
            if (++_nonBusinessTicks >= 10 && (Controllers.Count > 0 || SeenBubbles.Count > 0))
            {
                Controllers.Clear();
                SeenBubbles.Clear();
                PluginContext.Log.LogInfo("[MystiaAI] NightBubble: 离开营业相位，状态已清空");
            }
            return;
        }
        _nonBusinessTicks = 0;

        var manager = GuestsManager.Instance;
        if (UnityObjectGuard.IsDead(manager)) return;

        RefreshControllers(manager!);
        SweepBubbles();
    }

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

        var special = controller.TryCast<SpecialGuestsController>();
        var guest = special?.SpecialGuest;
        if (guest == null)
        {
            // 普通客人气泡：闲聊/评价都不碰
            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightBubble: 气泡属普通客人，跳过 文本「{Truncate(text)}」");
            return;
        }

        var ptr = controller.Pointer;
        Controllers.TryGetValue(ptr, out var state);

        if (evalBox != null)
        {
            // 评价型气泡：只有 HasEvaluated 翻转窗口内的才算吃饭评价（bark 不动）
            if (state == null || state.EvalFlippedFrame < 0 || _frame - state.EvalFlippedFrame > EvalWindowFrames)
            {
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] NightBubble: 评价型气泡不在评价翻转窗口内（疑似符卡 bark/残留），" +
                    $"保持原文 稀客={guest.stringId} 文本「{Truncate(text)}」");
                return;
            }
            var rating = RatingFromSkin(evalBox);
            RegisterBubble(box, boxPtr, guest.stringId, guest.Id, ChatScene.Evaluation, text,
                state.Dish, rating);
            return;
        }

        // 普通型气泡：文本命中闲聊池才算稀客闲聊
        if (IsIdleChatText(guest.Id, text))
        {
            RegisterBubble(box, boxPtr, guest.stringId, guest.Id, ChatScene.NightChat, text, null, null);
        }
        else
        {
            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightBubble: 稀客普通气泡文本未命中闲聊池，保持原文 " +
                $"稀客={guest.stringId} 文本「{Truncate(text)}」（校准样本）");
        }
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

    /// <summary>闲聊池成员性判定（宽松匹配：全等/互含），池读取失败按不命中处理。</summary>
    private static bool IsIdleChatText(int guestId, string text)
    {
        try
        {
            var pool = NightSceneLanguage.SpecialConversation;
            if (pool == null || !pool.TryGetValue(guestId, out var fragments) || fragments == null) return false;
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

    /// <summary>主线程预取上下文并发起 AI 生成；dish/rating 仅评价场景非空。</summary>
    private static void RegisterBubble(DialogBoxUI box, IntPtr boxPtr, string characterKey, int characterId,
        ChatScene scene, string original, string? dish, string? rating)
    {
        try
        {
            var extra = new Dictionary<string, string>
            {
                ["characterKey"] = characterKey,
                ["location"] = "夜晚居酒屋营业中",
                ["news"] = NewspaperReader.GetTodayNewsSummary(),
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
                KizunaLevel = GetKizunaLevel(characterKey),
                MaxLength = PluginContext.Settings.MaxLength.Value,
                Extra = extra,
            };

            var pending = new PendingBubble
            {
                CharacterKey = characterKey,
                Scene = scene,
                Original = original,
                BoxPtr = boxPtr,
                Box = box,
                AiTask = DialogPannelPatch.StartGeneration(context),
            };
            Pending.Add(pending);
            StartWatcher(pending);

            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightBubble: 登记 {scene}（{characterKey} id={characterId} 羁绊={context.KizunaLevel}" +
                $"{(dish != null ? $" 菜品「{dish}」" : "")}{(rating != null ? $" 评价「{rating}」" : "")}），" +
                $"原文「{Truncate(original)}」，AI 生成中");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] NightBubble 登记异常（保持原文）: {ex}");
        }
    }

    /// <summary>异步等待生成结果（复刻 DialogPannelPatch.StartWatcher 模式），尘埃落定回主线程终态化。</summary>
    private static void StartWatcher(PendingBubble pending)
    {
        var timeoutSeconds = PluginContext.Settings.TimeoutSeconds.Value;
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
                    $"[MystiaAI] NightBubble: {pending.Scene}（{pending.CharacterKey}）生成超时/失败，保持原文");
                return;
            }

            var box = pending.Box;
            if (UnityObjectGuard.IsDead(box)) return; // 气泡已淡出销毁
            if (box!.Pointer != pending.BoxPtr) return; // 包装复用防串
            var tmp = box.text;
            if (UnityObjectGuard.IsDead(tmp)) return;
            if (tmp!.text != pending.Original) return; // 气泡已被复用/文本已变，防串

            tmp.text = aiText;
            PluginContext.Log.LogInfo(
                $"[MystiaAI] NightBubble: {pending.Scene}（{pending.CharacterKey}）气泡原地改写为 AI 文本「{Truncate(aiText)}」");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] NightBubble.FinalizeBubble 异常: {ex}");
        }
    }

    // ---- 辅助 ----

    private static int GetKizunaLevel(string characterKey)
    {
        try
        {
            RunTimeAlbum.GetCharacterKizuna(characterKey, out _, out var level);
            return level;
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
