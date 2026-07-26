using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Common.DialogUtility;
using GameData;
using GameData.Profile;
using HarmonyLib;
using MystiaAI.Core;
using MystiaAI.UI;

using Il2CppDict = Il2CppSystem.Collections.Generic.Dictionary<int, string>;
using Il2CppReadOnlyDict = Il2CppSystem.Collections.Generic.IReadOnlyDictionary<int, string>;
using DialogLineData = Il2CppSystem.ValueTuple<Common.DialogUtility.DialogMeta, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Common.DialogUtility.LoadedDialogActionData>>;

namespace MystiaAI.Patches;

/// <summary>
/// 对话面板的执行汇聚点（最终落点），方案 B（异步占位）的显示层。
/// 实测：白天闲聊不经过 UniversalGameManager.OpenDialogMenu 系列（那些 patch 保留，
/// 覆盖其他对话路径），而是直接经 ADP UI 打开 DialogPannel。
///
/// 注意：本类的 patch 不走特性，由 Plugin.Load 显式调用 Install 手动注册，
/// 并做「方法解析 + 注册结果」自检日志——第四轮实测证明仅靠 HarmonyPatch 特性
/// 会出现静默绑定失败（目标没打上且无任何报错），必须让失败在启动时可见。
///
/// 拦截点与异步流程：
/// 1. OnExecutingDialogLoopCore prefix：消费待替换表，建立「原文 → LineEntry(生成任务)」
///    映射挂到面板实例，并捕获主线程 SynchronizationContext。
/// 2. ExecuteDialog prefix（每句显示前必经，按原文匹配）三分支：
///    任务已成功 → 直接替换为 AI 文本；未完成 → 替换为占位符「……」并启动 watcher，
///    任务完成/超时后回主线程原地改写当前句；失败/取消 → 保持原文。
/// 3. UpdateMetaPresentation postfix（保留，其他对话路径可能经过）：任务已成功时替换。
/// 4. OnGUI postfix：主线程 drain 备选派发队列（SynchronizationContext 捕获失败时的兜底）。
/// </summary>
internal static class DialogPannelPatch
{
    /// <summary>AI 文本未就绪时先显示的占位符。</summary>
    private const string Placeholder = "……";

    /// <summary>当前在各面板上生效的替换（Core 处消费后存入；随面板实例回收）。</summary>
    private static readonly ConditionalWeakTable<DialogPannel, ActiveReplacement> Active = new();

    /// <summary>一句台词的替换状态（逐句懒生成：轮到播放才发起任务）。</summary>
    private sealed class LineEntry
    {
        public int DialogId;
        public string Original = string.Empty;

        /// <summary>生成任务（懒生成：ExecuteDialog 命中该句时才发起）。</summary>
        public Task<string> AiTask = null!;

        /// <summary>是否已发起过生成（同一句只发起一次）。</summary>
        public bool TaskStarted;

        /// <summary>实际显示的文本（AI 文本；生成失败则为原文）。供后续句的 transcript 用「实际内容」。</summary>
        public string? FinalText;

        public bool WatcherStarted;
        public bool Resolved; // 已有终态（直接替换/失败保持原文/原地改写完成）
        public long ShownAtTimestamp; // 占位符显示时刻，用于耗时统计
    }

    /// <summary>一句 Self（米斯蒂娅）台词的状态（PoC 自由输入）。</summary>
    private sealed class SelfLineEntry
    {
        public int DialogId;
        public string Original = string.Empty;
        public bool Handled; // 已处理过（弹窗或自动略过，每句只处理一次）

        /// <summary>所属「玩家回合」组号（连续相邻 Self 段归一组，从 1 开始）。</summary>
        public int GroupId;

        /// <summary>是否组首句（只有组首句弹输入框）。</summary>
        public bool IsGroupFirst;

        /// <summary>自动推进是否待执行（防重复触发）。</summary>
        public bool AutoAdvancePending;
    }

    private sealed class ActiveReplacement
    {
        public string PackageKey = string.Empty;
        public PendingReplacement Replacement = null!;

        /// <summary>全部段的原文存档：dialogId → 原文（面板打开时从 textFile 取出）。</summary>
        public Dictionary<int, string> Originals { get; } = new();

        /// <summary>原文 → 台词替换状态，供 ExecuteDialog 按内容匹配（下游拿不到 dialogId）。</summary>
        public Dictionary<string, LineEntry> LinesByOriginal { get; } = new();

        /// <summary>dialogId → NPC 台词状态（懒生成 transcript 按 dialogId 取实际显示内容用）。</summary>
        public Dictionary<int, LineEntry> NpcByDialogId { get; } = new();

        /// <summary>原文 → Self 台词状态（自由输入覆盖层 + 玩家回合分组）。</summary>
        public Dictionary<string, SelfLineEntry> SelfLines { get; } = new();

        /// <summary>玩家回合组的决策：groupId → 该组最终显示文本（确认=玩家输入，跳过=组首句原文）。</summary>
        public Dictionary<int, string> GroupDecisions { get; } = new();

        /// <summary>玩家自由输入记录：dialogId → 文本（组内被略过的句记为空串），正式版写回 transcript 用。</summary>
        public Dictionary<int, string> PlayerInputs { get; } = new();

        /// <summary>当前正在显示的 Self 句（自动推进前校验防误触发）。</summary>
        public SelfLineEntry? CurrentSelfEntry;

        /// <summary>当前正在显示的那句（用于任务完成时校验防串句）。</summary>
        public LineEntry? CurrentLine;
    }

    /// <summary>手动注册本类的全部 patch，并输出自检日志。任何一步失败都会 LogError。</summary>
    public static void Install(Harmony harmony)
    {
        // Load 早期建立主线程 Update 派发通道（AI 回调绝不在 OnGUI 事件内执行，防 native 闪退）
        MainThreadDispatcher.EnsureUpdateChannel();

        // 备选派发通道：patch EventSystem.Update（每帧必跑的引擎 interop 方法，不依赖 ClassInjector；
        // 与注入的 DispatcherBehaviour 共用同一队列，谁先 drain 到都一样，幂等）
        try
        {
            var esUpdate = AccessTools.Method(
                typeof(UnityEngine.EventSystems.EventSystem),
                nameof(UnityEngine.EventSystems.EventSystem.Update));
            if (esUpdate == null)
            {
                PluginContext.Log.LogError("[MystiaAI] 未找到 EventSystem.Update，备选派发通道不可用");
            }
            else
            {
                harmony.Patch(esUpdate,
                    postfix: new HarmonyMethod(typeof(DialogPannelPatch), nameof(EventSystemUpdate_Postfix)));
                MainThreadDispatcher.MarkEventSystemChannelReady();
                PluginContext.Log.LogInfo("[MystiaAI] 已 patch EventSystem.Update 作为主派发通道");
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] patch EventSystem.Update 失败: {ex}");
        }

        PatchMethod(
            harmony,
            targetName: nameof(DialogPannel.OnExecutingDialogLoopCore),
            match: parameters => parameters.Length >= 2
                                 && parameters[0].Name == "dialogPack"
                                 && parameters[1].Name == "textFile",
            prefix: nameof(OnExecutingDialogLoopCore_Prefix),
            postfix: null);

        PatchMethod(
            harmony,
            targetName: nameof(DialogPannel.UpdateMetaPresentation),
            match: parameters => parameters.Length >= 2
                                 && parameters[0].Name == "line"
                                 && parameters.Any(p => p.Name == "data"),
            prefix: null,
            postfix: nameof(UpdateMetaPresentation_Postfix));

        PatchMethod(
            harmony,
            targetName: nameof(DialogPannel.ExecuteDialog),
            match: parameters => parameters.Length >= 1
                                 && parameters[0].Name == "dialogContext"
                                 && parameters[0].ParameterType == typeof(string),
            prefix: nameof(ExecuteDialog_Prefix),
            postfix: null);

        PatchMethod(
            harmony,
            targetName: nameof(DialogPannel.OnGUI),
            match: parameters => parameters.Length == 0,
            prefix: null,
            postfix: nameof(OnGUI_Postfix));

        // 按键锁定（PoC 验证点 2）：覆盖层打开期间，拦截 DialogPannel 的全部
        // InputAction 回调——J 继续对话(Interact)、ToggleDialogUI、快进(Start/EndSkipTiming)、
        // InterruptDialog、Copy，prefix 直接 return false 跳过原方法
        foreach (var name in new[] { "Interact", "ToggleDialogUI", "Copy", "InterruptDialog", "StartSkipTiming", "EndSkipTiming" })
        {
            PatchMethod(
                harmony,
                targetName: name,
                match: parameters => parameters.Length == 1
                                     && parameters[0].ParameterType.FullName != null
                                     && parameters[0].ParameterType.FullName.Contains("CallbackContext"),
                prefix: nameof(GuardWhileOverlayOpen),
                postfix: null);
        }

        DumpAllPatchedMethods();
    }

    /// <summary>按名字列出全部候选重载并打进日志，按参数名选定目标后注册 patch，最后回读注册结果。</summary>
    private static void PatchMethod(Harmony harmony, string targetName,
        Func<ParameterInfo[], bool> match, string? prefix, string? postfix)
    {
        try
        {
            var candidates = typeof(DialogPannel)
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name == targetName)
                .ToList();

            foreach (var candidate in candidates)
                PluginContext.Log.LogInfo($"[MystiaAI] 自检: 发现候选方法 {Describe(candidate)}");

            var target = candidates.FirstOrDefault(m => match(m.GetParameters()));
            if (target == null)
            {
                PluginContext.Log.LogError($"[MystiaAI] 自检失败: 无法解析 DialogPannel.{targetName}，本拦截点不生效！");
                return;
            }
            PluginContext.Log.LogInfo($"[MystiaAI] 自检: 选定目标 {Describe(target)}");

            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(DialogPannelPatch), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(DialogPannelPatch), postfix));

            // 回读注册结果，确认 detour 确实挂上
            var info = Harmony.GetPatchInfo(target);
            var prefixCount = info?.Prefixes?.Count ?? 0;
            var postfixCount = info?.Postfixes?.Count ?? 0;
            if ((prefix != null && prefixCount == 0) || (postfix != null && postfixCount == 0))
            {
                PluginContext.Log.LogError(
                    $"[MystiaAI] 自检失败: DialogPannel.{targetName} 注册后回读不到 patch (prefix={prefixCount}, postfix={postfixCount})！");
                return;
            }
            PluginContext.Log.LogInfo(
                $"[MystiaAI] 自检通过: DialogPannel.{targetName} 已注册 (prefix={prefixCount}, postfix={postfixCount})");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] 自检失败: 注册 DialogPannel.{targetName} 时异常: {ex}");
        }
    }

    private static void DumpAllPatchedMethods()
    {
        try
        {
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(method);
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] 自检: 已 patch {Describe(method)} " +
                    $"(prefix={info?.Prefixes?.Count ?? 0}, postfix={info?.Postfixes?.Count ?? 0})");
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] 自检: 枚举已 patch 方法时异常: {ex}");
        }
    }

    private static string Describe(MethodBase method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(p =>
            $"{(p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "")}{p.ParameterType.FullName} {p.Name}"));
        return $"{method.DeclaringType?.FullName}.{method.Name}({parameters})";
    }

    // ---- 以下为 patch 本体（无 Harmony 特性，由 Install 手动注册）----

    private static void OnExecutingDialogLoopCore_Prefix(DialogPannel __instance, ref Il2CppReadOnlyDict textFile)
    {
        try
        {
            if (!PluginContext.Settings.Enabled) return;
            if (__instance == null) return;

            var package = __instance.OpenContext?.DialogPackageToPlay;
            if (package == null) return;

            // 诊断日志：每个经过面板的对话包都打一条，便于确认 patch 命中与匹配情况
            var packageKey = PendingReplacementStore.KeyOf(package);
            var hit = PendingReplacementStore.Contains(package);
            PluginContext.Log.LogInfo(
                $"[MystiaAI] DialogPannel.OnExecutingDialogLoopCore: 包 key={packageKey} " +
                $"textFile={(textFile == null ? "null" : "非空")} 命中待替换表={hit}");
            if (!hit) return;

            // 一次性消费：取出即从待替换表移除，防泄漏
            var replacement = PendingReplacementStore.Consume(package);
            if (replacement == null) return;

            // 登记为本面板的生效替换；面板是池化复用的，带包名校验防止串包
            var active = new ActiveReplacement { PackageKey = packageKey, Replacement = replacement };

            // 面板打开时才有全部原文（textFile）。这里只做数据准备，不发起任何生成任务：
            // NPC 段改为「逐句懒生成」——轮到它播放时（ExecuteDialog prefix）才发起任务，
            // transcript 用截至当前句的实际内容（玩家可能已改写 Self 句）。
            // textFile 层不再改写：实测显示完全不读 textFile（三轮验证），显示已全由
            // ExecuteDialog 控制，改写它纯属冗余。
            var originals = active.Originals;
            if (textFile != null)
            {
                foreach (var segment in replacement.Segments)
                {
                    if (textFile.TryGetValue(segment.DialogId, out var original) && !string.IsNullOrEmpty(original))
                        originals[segment.DialogId] = original;
                }
            }

            var npcCount = 0;
            var groupId = 0;
            SelfLineEntry? previousSelf = null;
            var groupSizes = new List<int>();
            foreach (var segment in replacement.Segments)
            {
                if (segment.IsSelf)
                {
                    // 米斯蒂娅的台词：AI 不碰；连续相邻的 Self 段归为同一个「玩家回合」组，
                    // 一组只弹一次输入框（组首句），组内后续句自动略过
                    if (originals.TryGetValue(segment.DialogId, out var selfOriginal))
                    {
                        SelfLineEntry entry;
                        if (previousSelf != null)
                        {
                            entry = new SelfLineEntry
                            {
                                DialogId = segment.DialogId,
                                Original = selfOriginal,
                                GroupId = previousSelf.GroupId,
                                IsGroupFirst = false,
                            };
                            groupSizes[groupSizes.Count - 1]++;
                        }
                        else
                        {
                            entry = new SelfLineEntry
                            {
                                DialogId = segment.DialogId,
                                Original = selfOriginal,
                                GroupId = ++groupId,
                                IsGroupFirst = true,
                            };
                            groupSizes.Add(1);
                        }
                        active.SelfLines[selfOriginal] = entry;
                        previousSelf = entry;
                    }
                    continue;
                }
                previousSelf = null; // Self 组被 NPC 段打断
                if (!originals.TryGetValue(segment.DialogId, out var original)) continue;

                // 懒生成：只建状态，不发起任务（AiTask 在 ExecuteDialog 命中时才赋值）
                var npcEntry = new LineEntry
                {
                    DialogId = segment.DialogId,
                    Original = original,
                };
                active.LinesByOriginal[original] = npcEntry;
                active.NpcByDialogId[segment.DialogId] = npcEntry;
                npcCount++;
            }

            Active.Remove(__instance);
            Active.Add(__instance, active);

            PluginContext.Log.LogInfo(
                $"[MystiaAI] 角色 {replacement.CharacterKey}: 共 {replacement.Segments.Count} 段，" +
                $"已为 {npcCount} 个 NPC 段建立懒生成状态（逐句播放时才发起任务）；" +
                $"Self 段分 {groupSizes.Count} 组（{(groupSizes.Count == 0 ? "无" : string.Join("+", groupSizes))} 段），每组只弹一次输入框");
        }
        catch (Exception ex)
        {
            // Patch 里绝不向游戏抛异常，只记日志
            PluginContext.Log.LogError($"[MystiaAI] DialogPannelPatch.OnExecutingDialogLoopCore 异常: {ex}");
        }
    }

    /// <summary>
    /// 逐句最终落点："Printing Dialog" 日志的出处，每句显示前必经。
    /// 三分支：任务已成功 → 直接替换；未完成 → 占位符 + 启动 watcher 完成后原地改写；
    /// 失败/取消 → 保持原文。
    /// </summary>
    private static void ExecuteDialog_Prefix(DialogPannel __instance, ref string dialogContext)
    {
        try
        {
            if (!PluginContext.Settings.Enabled) return;
            if (__instance == null) return;
            if (string.IsNullOrEmpty(dialogContext)) return;
            if (!Active.TryGetValue(__instance, out var active) || active == null) return;

            // 面板池化复用：确认当前播放的仍是登记替换时的那个包
            var package = __instance.OpenContext?.DialogPackageToPlay;
            if (package == null || PendingReplacementStore.KeyOf(package) != active.PackageKey) return;

            // 每句都更新「当前句」，使上一句的迟到 watcher 失效（玩家翻页/快进）
            if (!active.LinesByOriginal.TryGetValue(dialogContext, out var entry))
            {
                active.CurrentLine = null;
                HandleSelfLine(__instance, active, ref dialogContext);
                return;
            }
            active.CurrentLine = entry;
            if (entry.Resolved) return;

            // 逐句懒生成：轮到这句播放才发起任务（同一句只发起一次），
            // transcript 用截至当前句的实际内容（过去的 Self 句用玩家真实输入，
            // 过去的 NPC 句用实际显示的 AI 文本）
            if (!entry.TaskStarted)
            {
                entry.TaskStarted = true;
                var context = BuildLazyContext(active, entry);
                entry.AiTask = StartGeneration(context);
                context.Extra.TryGetValue("news", out var newsText);
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] DialogPannel: 包 {active.PackageKey} 段 dialogId={entry.DialogId} " +
                    $"发起懒生成（时间={context.GameTime} 报纸={(string.IsNullOrEmpty(newsText) ? "<无>" : newsText)}）");
            }

            var task = entry.AiTask;
            if (task.IsCompletedSuccessfully)
            {
                // 分支一：AI 文本已就绪，直接替换
                entry.Resolved = true;
                entry.FinalText = task.Result;
                var preview = Truncate(dialogContext);
                dialogContext = task.Result;
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] DialogPannel: 来源=ExecuteDialog 包 {active.PackageKey} 段 dialogId={entry.DialogId} " +
                    $"原文「{preview}」→ 已替换（AI 文本已就绪）");
            }
            else if (task.IsFaulted || task.IsCanceled)
            {
                // 分支三：生成失败/取消，保持原文
                entry.Resolved = true;
                entry.FinalText = entry.Original;
                PluginContext.Log.LogWarning(
                    $"[MystiaAI] DialogPannel: 包 {active.PackageKey} 段 dialogId={entry.DialogId} " +
                    $"生成失败（{task.Exception?.GetBaseException().Message ?? "已取消"}），保持原文");
            }
            else
            {
                // 分支二：未完成，先显示占位符，任务完成/超时后原地改写
                dialogContext = Placeholder;
                entry.ShownAtTimestamp = Stopwatch.GetTimestamp();
                if (!entry.WatcherStarted)
                {
                    entry.WatcherStarted = true;
                    StartWatcher(__instance, active, entry);
                }
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] DialogPannel: 包 {active.PackageKey} 段 dialogId={entry.DialogId} " +
                    $"AI 文本未就绪，显示占位符「{Placeholder}」");
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] DialogPannelPatch.ExecuteDialog 异常: {ex}");
        }
    }

    /// <summary>逐句兜底（保留）：任务已成功时按 dialogId 精确替换。</summary>
    private static void UpdateMetaPresentation_Postfix(DialogPannel __instance, ref string line, DialogLineData data)
    {
        try
        {
            if (!PluginContext.Settings.Enabled) return;
            if (__instance == null) return;
            if (!Active.TryGetValue(__instance, out var active) || active == null) return;

            // 面板池化复用：确认当前播放的仍是登记替换时的那个包
            var package = __instance.OpenContext?.DialogPackageToPlay;
            if (package == null || PendingReplacementStore.KeyOf(package) != active.PackageKey) return;

            var meta = data?.Item1;
            if (meta == null) return;

            // 按 dialogId 找当前句（Self 段不在表中，自然绕过）
            LineEntry? entry = null;
            foreach (var candidate in active.LinesByOriginal.Values)
            {
                if (candidate.DialogId == meta.dialogId) { entry = candidate; break; }
            }
            if (entry == null) return;
            if (!entry.TaskStarted) return; // 懒生成：任务尚未发起
            if (!entry.AiTask.IsCompletedSuccessfully) return; // 未就绪时由 ExecuteDialog 占位流程处理
            if (line == entry.AiTask.Result) return; // 已被其他层替换，幂等跳过

            var preview = Truncate(line);
            line = entry.AiTask.Result;
            PluginContext.Log.LogInfo(
                $"[MystiaAI] DialogPannel: 来源=逐句点(UpdateMetaPresentation) 包 {active.PackageKey} " +
                $"段 dialogId={meta.dialogId} 原文「{preview}」→ 已替换");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] DialogPannelPatch.UpdateMetaPresentation 异常: {ex}");
        }
    }

    /// <summary>
    /// 主派发通道：EventSystem.Update 每帧 postfix。
    /// 同时承担覆盖层轮询泵——原泵在 DialogPannel.OnGUI，对话结束/快进跳过时面板被隐藏后
    /// OnGUI 停跑，覆盖层的会话结束检测会失去执行机会而永久卡屏，故挪到与面板无关的每帧通道。
    /// </summary>
    private static void EventSystemUpdate_Postfix()
    {
        FreeInputOverlay.PollHotkeys();
        MainThreadDispatcher.Drain(); // 先 Poll 后 drain：Poll 入队的 Close 同帧即可执行
        NightBubblePatch.OnEventSystemFrame(); // 夜晚气泡帧泵（同链复用本 postfix，内部 30 帧节流 + 相位闸门）
    }

    /// <summary>主线程 drain 备选派发队列（仅 Update 通道未建立时兜底）。覆盖层轮询泵已挪到 EventSystem.Update。</summary>
    private static void OnGUI_Postfix()
    {
        // 点击崩溃排除法埋点：每次鼠标按下都会在 OnGUI 收到 MouseDown 事件，
        // 崩溃后看日志最后一条是「进入」还是「离开」即可定位是否死在本 postfix 内
        var evt = UnityEngine.Event.current;
        var isClick = evt != null && evt.type == UnityEngine.EventType.MouseDown;
        if (isClick)
            PluginContext.Log.LogInfo("[MystiaAI] OnGUI postfix: MouseDown 进入");
        MainThreadDispatcher.DrainFromOnGUI(); // Update 通道活着时是空操作（OnGUI 事件内不做原生 UI 调用）
        if (isClick)
            PluginContext.Log.LogInfo("[MystiaAI] OnGUI postfix: MouseDown 离开");
    }

    /// <summary>按键锁定：覆盖层打开期间跳过 DialogPannel 的全部 InputAction 回调。</summary>
    private static bool GuardWhileOverlayOpen(MethodBase __originalMethod)
    {
        try
        {
            var open = FreeInputOverlay.IsOpen;
            if (open)
            {
                // 埋点：点击触发 Interact（若游戏绑了鼠标）时会留下这条日志，排除法定位用
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] Guard: 覆盖层打开中，拦截 {(__originalMethod == null ? "<unknown>" : __originalMethod.Name)}");
                return false; // 跳过原方法
            }
            return true;
        }
        catch
        {
            return true; // 异常时放行，绝不卡死游戏
        }
    }

    /// <summary>
    /// Self（米斯蒂娅）台词处理：连续 Self 段为同一「玩家回合」组。
    /// 组首句 → 占位符 + 弹输入覆盖层（确认显示玩家输入 / 跳过显示原文，决策记入 GroupDecisions）；
    /// 组内后续句（组已有决策）→ 不弹窗，文本置空并自动推进对话。
    /// 覆盖层创建失败则回退显示原文。
    /// </summary>
    private static void HandleSelfLine(DialogPannel panel, ActiveReplacement active, ref string dialogContext)
    {
        try
        {
            if (FreeInputOverlay.IsOpen) return; // 已有覆盖层，不再叠加
            if (!active.SelfLines.TryGetValue(dialogContext, out var selfEntry)) return;
            active.CurrentSelfEntry = selfEntry;
            if (selfEntry.Handled) return;
            selfEntry.Handled = true;

            // 组内后续句：不弹窗、不停留，置空文本并自动推进到下一句
            if (!selfEntry.IsGroupFirst && active.GroupDecisions.ContainsKey(selfEntry.GroupId))
            {
                active.PlayerInputs[selfEntry.DialogId] = string.Empty; // 被组决策覆盖，记空串
                dialogContext = string.Empty;
                selfEntry.AutoAdvancePending = true;
                ScheduleAutoAdvance(panel, active, selfEntry);
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] DialogPannel: Self 段 dialogId={selfEntry.DialogId} 属玩家回合组 {selfEntry.GroupId}，" +
                    $"已随组首句覆盖，置空并排队自动推进");
                return;
            }

            // 组首句（或组尚无决策的兜底）：照常弹输入覆盖层（带 AI 建议按钮）
            var original = dialogContext;
            try
            {
                // 覆盖层打开期间对话框正文留白（空格而非「……」）：覆盖层面板已改为全透明，
                // 若显示占位符会透过面板与输入区文字重叠；关闭时 SetContent 会覆写本句。
                dialogContext = " ";
                FreeInputOverlay.Open(panel, result =>
                {
                    // 主线程回调（经 Update 通道延迟执行）
                    PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: onClosed 回调进入");
                    try
                    {
                        if (UnityObjectGuard.IsDead(panel)) return; // 面板已销毁或包装已被 GC 回收
                        var text = string.IsNullOrWhiteSpace(result) ? null : result;
                        var shown = text ?? selfEntry.Original;

                        // 记录组决策与 PlayerInputs（确认=玩家输入，跳过=原文；写回 transcript 用）
                        active.GroupDecisions[selfEntry.GroupId] = shown;
                        active.PlayerInputs[selfEntry.DialogId] = shown;

                        panel.SetContent(shown);
                        panel.SetContentMaxVisibleCharacters(99999);
                        PluginContext.Log.LogInfo(
                            $"[MystiaAI] DialogPannel: Self 组 {selfEntry.GroupId} 首句 dialogId={selfEntry.DialogId} " +
                            $"{(text != null ? $"玩家输入「{Truncate(text)}」" : "玩家跳过，显示原文")}已显示并记录");
                    }
                    catch (Exception ex)
                    {
                        PluginContext.Log.LogError($"[MystiaAI] Self 段写回异常: {ex}");
                    }
                }, BuildSuggestionProvider(active, selfEntry, out var lastNpcPending),
                    lastNpcPending ? "未进行对话" : "建议不可用");
            }
            catch (Exception ex)
            {
                // 覆盖层创建失败：回退原文，绝不影响游戏流程
                dialogContext = original;
                PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay 打开失败，回退原文: {ex}");
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] DialogPannelPatch.HandleSelfLine 异常: {ex}");
        }
    }

    /// <summary>
    /// 组装 AI 建议的 provider：context 带截至当前 Self 句的实际 transcript（Extra["transcript"]），
    /// npcLine = 最近一句 NPC 台词原文。
    /// 最近一句 NPC 台词 AI 还没生成出来时（lastNpcPending=true 传出）：不发起建议生成（返回 null），
    /// 调用方把建议按钮置为「未进行对话」；transcript 一律过滤未定型 NPC 句（原版绝不进上下文）。
    /// 注意：provider 会在线程池执行，而 context 组装涉及 IL2CPP 调用（时钟/报纸），
    /// 跨线程 interop 是 .NET Runtime 内部错误（event 1023）的典型成因——
    /// 因此全部数据在调用方（主线程）预取，provider 闭包只引用已取好的托管字符串。
    /// </summary>
    private static Func<CancellationToken, Task<IReadOnlyList<string>>>? BuildSuggestionProvider(
        ActiveReplacement active, SelfLineEntry selfEntry, out bool lastNpcPending)
    {
        lastNpcPending = false;
        GenerationContext context;
        string npcLine;
        try
        {
            // 判定用懒生成状态机的实际状态（FinalText），不看文本内容
            lastNpcPending = IsLastNpcLinePending(active, selfEntry.DialogId);
            if (lastNpcPending)
            {
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] DialogPannel: Self 段 dialogId={selfEntry.DialogId} 的上一句 NPC 台词 " +
                    "AI 文本尚未生成（占位符阶段被推进），不发起建议生成，按钮显示「未进行对话」");
                return null;
            }

            context = new GenerationContext
            {
                Scene = ChatScene.DayChat,
                CharacterName = active.Replacement.CharacterKey,
                GameTime = GetGameTimeText(),
                Language = GetCurrentLanguage(),
                MaxLength = PluginContext.Settings.MaxLength,
                Extra = new Dictionary<string, string>
                {
                    // 没有过去对话时 transcript 为空串；未定型 NPC 句过滤掉（快进连点可能多句 pending）
                    ["transcript"] = BuildActualTranscript(active, selfEntry.DialogId, skipUnsettledNpc: true),
                    ["characterKey"] = active.Replacement.CharacterKey,
                    ["location"] = "户外白天地图",
                    ["news"] = NewspaperReader.GetTodayNewsSummary(),
                },
            };
            npcLine = FindLastNpcOriginal(active, selfEntry.DialogId);
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] BuildSuggestionProvider 预取异常（返回空建议）: {ex}");
            return null;
        }

        context.Extra.TryGetValue("news", out var newsText);
        PluginContext.Log.LogInfo(
            $"[MystiaAI] DialogPannel: Self 段 dialogId={selfEntry.DialogId} 建议上下文已预取" +
            $"（时间={context.GameTime} 报纸={(string.IsNullOrEmpty(newsText) ? "<无>" : newsText)}）");
        return cancellationToken =>
            PluginContext.AiClient.GenerateReplyOptionsAsync(context, npcLine, 2, cancellationToken);
    }

    // ---- 组内后续 Self 句的自动推进 ----

    /// <summary>稍作延迟后回主线程自动推进（等 ExecuteDialog 状态机进入等待输入状态）。</summary>
    private static void ScheduleAutoAdvance(DialogPannel panel, ActiveReplacement active, SelfLineEntry entry)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120).ConfigureAwait(false);
            }
            catch { /* 忽略，照常派发 */ }
            MainThreadDispatcher.Post(() => AutoAdvance(panel, active, entry));
        });
    }

    /// <summary>
    /// 自动推进：调用 DialogPannel.Interact（即「继续对话 J」的 InputAction 回调）。
    /// 执行前校验面板存活、包 key 未变、当前 Self 句仍是登记的那句（复用 FinalizeLine 的校验模式）；
    /// 快进模式下游戏会自行推进，不重复触发（防双跳）。
    /// </summary>
    private static void AutoAdvance(DialogPannel panel, ActiveReplacement active, SelfLineEntry entry)
    {
        try
        {
            if (UnityObjectGuard.IsDead(panel)) return; // 面板已销毁或包装已被 GC 回收（切场景/关对局）
            if (!Active.TryGetValue(panel, out var current) || !ReferenceEquals(current, active)) return;
            var package = panel.OpenContext?.DialogPackageToPlay;
            if (package == null || PendingReplacementStore.KeyOf(package) != active.PackageKey) return;
            if (!entry.AutoAdvancePending || !ReferenceEquals(active.CurrentSelfEntry, entry))
            {
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] DialogPannel: Self 段 dialogId={entry.DialogId} 的自动推进已过期（玩家已翻页/对话已结束），放弃");
                return;
            }
            entry.AutoAdvancePending = false;

            try
            {
                if (panel.fastForwardMode)
                {
                    PluginContext.Log.LogInfo(
                        $"[MystiaAI] DialogPannel: Self 段 dialogId={entry.DialogId} 快进模式中，由游戏自行推进");
                    return;
                }
            }
            catch { /* 字段读取失败则照常推进 */ }

            panel.Interact(new UnityEngine.InputSystem.InputAction.CallbackContext());
            PluginContext.Log.LogInfo(
                $"[MystiaAI] DialogPannel: Self 段 dialogId={entry.DialogId} 自动推进成功（调用 Interact）");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] DialogPannelPatch.AutoAdvance 异常: {ex}");
        }
    }

    // ---- 异步 watcher：任务完成/超时后回主线程原地改写当前句 ----

    private static void StartWatcher(DialogPannel panel, ActiveReplacement active, LineEntry entry)
    {
        var timeoutSeconds = PluginContext.Settings.TimeoutSeconds;
        _ = Task.Run(async () =>
        {
            string? aiText = null;
            try
            {
                var timeout = Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1f, timeoutSeconds)));
                var finished = await Task.WhenAny(entry.AiTask, timeout).ConfigureAwait(false);
                if (finished == entry.AiTask && entry.AiTask.IsCompletedSuccessfully)
                    aiText = entry.AiTask.Result;
            }
            catch (Exception ex)
            {
                PluginContext.Log.LogError($"[MystiaAI] DialogPannel watcher 异常: {ex}");
            }

            // 回主线程改写 UI（线程池里绝不能碰 Unity 对象）
            var captured = aiText;
            MainThreadDispatcher.Post(() => FinalizeLine(panel, active, entry, captured));
        });
    }

    /// <summary>任务尘埃落定后在主线程原地改写：成功 → AI 文本；超时/失败 → 回退原文。</summary>
    private static void FinalizeLine(DialogPannel panel, ActiveReplacement active, LineEntry entry, string? aiText)
    {
        try
        {
            // 面板已被销毁（玩家关了对局/切场景）或 Il2Cpp 包装已被 GC 回收
            if (UnityObjectGuard.IsDead(panel)) return;
            // 面板复用后已是另一条替换 / 另一个包
            if (!Active.TryGetValue(panel, out var current) || !ReferenceEquals(current, active)) return;
            var package = panel.OpenContext?.DialogPackageToPlay;
            if (package == null || PendingReplacementStore.KeyOf(package) != active.PackageKey) return;
            // 玩家已翻页/快进，或该句已有终态
            if (entry.Resolved || !ReferenceEquals(active.CurrentLine, entry)) return;

            entry.Resolved = true;
            var elapsedMs = (Stopwatch.GetTimestamp() - entry.ShownAtTimestamp) * 1000 / Stopwatch.Frequency;

            if (aiText != null)
            {
                entry.FinalText = aiText;
                panel.SetContent(aiText);
                panel.SetContentMaxVisibleCharacters(99999); // 打字机全量可见
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] DialogPannel: 包 {active.PackageKey} 段 dialogId={entry.DialogId} " +
                    $"AI 文本到位，占位符原地替换（耗时 {elapsedMs}ms）");
            }
            else
            {
                entry.FinalText = entry.Original;
                panel.SetContent(entry.Original);
                panel.SetContentMaxVisibleCharacters(99999);
                PluginContext.Log.LogWarning(
                    $"[MystiaAI] DialogPannel: 包 {active.PackageKey} 段 dialogId={entry.DialogId} " +
                    $"生成超时/失败，占位符回退原文（耗时 {elapsedMs}ms）");
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] DialogPannelPatch.FinalizeLine 异常: {ex}");
        }
    }

    /// <summary>发起生成任务并兜底：同步异常 / 返回 null 统一转为失败任务，交给显示层走失败逻辑。
    /// internal：NightChatPatch（营业气泡）复用同一入口。</summary>
    internal static Task<string> StartGeneration(GenerationContext context)
    {
        try
        {
            var task = PluginContext.AiClient.GenerateAsync(context);
            return task ?? Task.FromException<string>(new InvalidOperationException("AI 客户端返回了 null 任务"));
        }
        catch (Exception ex)
        {
            return Task.FromException<string>(ex);
        }
    }

    /// <summary>
    /// 懒生成的 GenerationContext：transcript = 截至 currentDialogId（不含）的实际对话内容——
    /// 过去的 Self 句用玩家真实输入（跳过记原文、被组略过的空句省略），
    /// 过去的 NPC 句用实际显示文本（AI 文本；失败为原文），未来的句不包含。
    /// </summary>
    private static GenerationContext BuildLazyContext(ActiveReplacement active, LineEntry current)
    {
        return new GenerationContext
        {
            Scene = ChatScene.DayChat,
            CharacterName = active.Replacement.CharacterKey,
            GameTime = GetGameTimeText(),
            Language = GetCurrentLanguage(),
            MaxLength = PluginContext.Settings.MaxLength,
            // 与 PromptBuilder 的契约：给了这两个键就走连贯改写 prompt
            Extra = new Dictionary<string, string>
            {
                ["transcript"] = BuildActualTranscript(active, current.DialogId),
                ["targetOriginal"] = current.Original,
                ["characterKey"] = active.Replacement.CharacterKey,
                ["location"] = "户外白天地图",
                // 文文新闻当日剪报（流行情报等），未解锁/无数据为空串，prompt 侧判空
                ["news"] = NewspaperReader.GetTodayNewsSummary(),
            },
        };
    }

    /// <summary>
    /// 截至 beforeDialogId（不含）的实际对话 transcript，逐行「说话人：台词」。
    /// skipUnsettledNpc=true 时（建议生成路径）跳过 AI 还没生成出来的 NPC 句——
    /// 那句实际显示的是占位符，喂原版进上下文会和已 AI 化的前文对不上。
    /// </summary>
    private static string BuildActualTranscript(ActiveReplacement active, int beforeDialogId,
        bool skipUnsettledNpc = false)
    {
        var protagonist = ProtagonistName(GetCurrentLanguage());
        var lines = new List<string>();
        foreach (var segment in active.Replacement.Segments)
        {
            if (segment.DialogId == beforeDialogId) break;
            if (!active.Originals.TryGetValue(segment.DialogId, out var original)) continue;

            if (segment.IsSelf)
            {
                var text = original;
                if (active.PlayerInputs.TryGetValue(segment.DialogId, out var input))
                {
                    if (string.IsNullOrEmpty(input))
                        continue; // 玩家回合组内被略过的句，实际未发言
                    text = input;
                }
                lines.Add($"{protagonist}：{text}");
            }
            else
            {
                var text = original;
                if (active.NpcByDialogId.TryGetValue(segment.DialogId, out var npc))
                {
                    if (npc.FinalText == null)
                    {
                        // 未定型（生成中/占位符阶段被快进，FinalizeLine 翻页早退永不落 FinalText）
                        if (skipUnsettledNpc) continue;
                    }
                    else
                    {
                        text = npc.FinalText; // 实际显示的 AI 文本（失败时已落为原文）
                    }
                }
                lines.Add($"{active.Replacement.CharacterKey}：{text}");
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// beforeDialogId 之前最近一句 NPC 台词是否还未定型（AI 生成中/占位符阶段被推进）。
    /// 判定用懒生成状态机的 FinalText（已替换/失败回退都会落值），不看文本内容。
    /// </summary>
    private static bool IsLastNpcLinePending(ActiveReplacement active, int beforeDialogId)
    {
        var lastId = -1;
        foreach (var segment in active.Replacement.Segments)
        {
            if (segment.DialogId == beforeDialogId) break;
            if (!segment.IsSelf && active.Originals.ContainsKey(segment.DialogId))
                lastId = segment.DialogId;
        }
        if (lastId < 0) return false; // 前面没有 NPC 句（开场即玩家回合）
        return active.NpcByDialogId.TryGetValue(lastId, out var entry) && entry.FinalText == null;
    }

    /// <summary>beforeDialogId 之前最近一句 NPC 台词原文（AI 建议的 npcLine 参数）。</summary>
    private static string FindLastNpcOriginal(ActiveReplacement active, int beforeDialogId)
    {
        var last = string.Empty;
        foreach (var segment in active.Replacement.Segments)
        {
            if (segment.DialogId == beforeDialogId) break;
            if (!segment.IsSelf && active.Originals.TryGetValue(segment.DialogId, out var original))
                last = original;
        }
        return last;
    }

    /// <summary>主角名按游戏语言映射（transcript 里 Self 行的说话人标签）。</summary>
    private static string ProtagonistName(string language)
        => language is "Chinese" or "CNT" ? "米斯蒂娅" : "Mystia";

    /// <summary>
    /// 真实游戏时间文本（prompt 的时间锚点）。internal：NightChatPatch（营业气泡）复用。
    /// 白天（GamePhase.Day 系）：读 DayScene.UI.UIManager.Instance.currentTime——
    /// MonoSingleton 单例左上角时钟的 TMP 文本（如 "10:30"），拼成「白天 10:30」；
    /// 时钟读取失败降级为「白天」并打 Warning。其余阶段按 RunTimeScheduler.CurrentGamePhase
    /// 给粗粒度文本（准备中/营业中/结算中）。出处：decomp/DayScene_UI_UIManager.cs
    /// （UIManager : MonoSingleton&lt;UIManager&gt;，currentTime 字段）、
    /// decomp/GameData_RunTime_Common_RunTimeScheduler.cs（GamePhase 枚举）。
    /// </summary>
    internal static string GetGameTimeText()
    {
        try
        {
            var phase = GameData.RunTime.Common.RunTimeScheduler.CurrentGamePhase;
            switch (phase)
            {
                case GameData.RunTime.Common.RunTimeScheduler.GamePhase.Day:
                case GameData.RunTime.Common.RunTimeScheduler.GamePhase.DayTimeEnd:
                case GameData.RunTime.Common.RunTimeScheduler.GamePhase.DayToPreperation:
                    try
                    {
                        var clock = DayScene.UI.UIManager.Instance?.currentTime?.text;
                        if (!string.IsNullOrEmpty(clock)) return $"白天 {clock}";
                    }
                    catch (Exception ex)
                    {
                        PluginContext.Log.LogWarning($"[MystiaAI] 读取白天时钟失败，降级为「白天」: {ex.Message}");
                    }
                    return "白天";
                case GameData.RunTime.Common.RunTimeScheduler.GamePhase.Preperation:
                case GameData.RunTime.Common.RunTimeScheduler.GamePhase.PreperationToWork:
                case GameData.RunTime.Common.RunTimeScheduler.GamePhase.BeforeWorkStart:
                    return "夜晚营业前准备中";
                case GameData.RunTime.Common.RunTimeScheduler.GamePhase.Work:
                case GameData.RunTime.Common.RunTimeScheduler.GamePhase.BeforeChallengeStart:
                case GameData.RunTime.Common.RunTimeScheduler.GamePhase.Challenge:
                    return "夜晚营业中";
                case GameData.RunTime.Common.RunTimeScheduler.GamePhase.WorkEnd:
                case GameData.RunTime.Common.RunTimeScheduler.GamePhase.WorkToResult:
                case GameData.RunTime.Common.RunTimeScheduler.GamePhase.Result:
                case GameData.RunTime.Common.RunTimeScheduler.GamePhase.ResultToDay:
                    return "营业结束结算中";
                default:
                    return phase.ToString();
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] 获取游戏时间失败（降级为空串）: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>当前游戏语言枚举名（Chinese/CNT/English/…）。internal：NightChatPatch（营业气泡）复用。</summary>
    internal static string GetCurrentLanguage()
    {
        try
        {
            return MultiLanguageTextMesh.CurrentLanguage.ToString();
        }
        catch
        {
            return "Chinese";
        }
    }

    /// <summary>取原文前 10 字供日志对照。</summary>
    private static string Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "<空>";
        return s.Length <= 10 ? s : s.Substring(0, 10) + "…";
    }
}
