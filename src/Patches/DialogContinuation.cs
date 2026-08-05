using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Common.UI;
using DEYU.AdpUISystem.Managers;
using GameData.Profile;
using GameData.RunTime.Common;
using MystiaAI.Core;

namespace MystiaAI.Patches;

/// <summary>
/// 自动续聊：一段 AI 对话包播完后无缝重开同一包进入下一轮，并把前几轮的完整
/// 对话记录带给 AI（对玩家来说本质是一轮对话，只是游戏底层按固定句数分包播放）。
/// 停止条件：玩家点「结束对话」（ExitRequested）、本轮无玩家输入（NPC 独白包/玩家挂机）、
/// 配置关闭、离开白天场景、达到轮数安全上限。
/// 实现要点：结束回调交 IL2CPP 持有必须钉住（同 OpenDialogMenuPatch 的防 GC 死 thunk 模式）；
/// 重开直接调 UniversalGameManager.OpenDialogMenu，不走 SceneManager.Chat，
/// 避免重复触发首次聊天羁绊/事件检查等副作用。
/// </summary>
internal static class DialogContinuation
{
    /// <summary>跨轮携带 transcript 的字符上限：超出时按行丢弃最旧内容，控制 token 消耗。</summary>
    private const int CarriedTranscriptLimit = 6000;

    /// <summary>单次对话的续聊轮数安全上限（正常游玩远达不到，防意外死循环）。</summary>
    private const int MaxRounds = 30;

    /// <summary>播完到重开的间隔（毫秒）：等面板关闭流程走完（m_ProtectionLock 在结束回调
    /// 触发前已释放）。注意：间隔短于关板淡出会提高结束回调二次触发概率（已由 1 秒防抖兜底），
    /// 若出现续聊静默失效应回调到 200ms。</summary>
    private const int ReopenDelayMs = 10;

    private sealed class Entry
    {
        public DialogPackage Package = null!;
        public string CharacterKey = string.Empty;
        public IReadOnlyList<DialogSegment> Segments = new List<DialogSegment>();
        public string CarriedTranscript = string.Empty;
        public int Rounds;

        /// <summary>上一轮以玩家说话收尾：下一轮开头玩家回合组静默略过，NPC 直接承接回应。</summary>
        public bool SkipLeadingSelfNextRound;

        /// <summary>上次处理结束回调的时刻（防抖：重开撞上未关完的面板会导致结束回调二次触发）。</summary>
        public System.DateTime LastFinishUtc = System.DateTime.MinValue;

        /// <summary>退出已处理（结束回调链会多次触发，退出路径必须幂等：记忆/关板/恢复输入只做一次）。</summary>
        public bool ExitHandled;

        /// <summary>Manual 模式回调捕获的「开始播首句」动作（loadFinish 时调用放行首轮）。</summary>
        public Il2CppSystem.Action? StartPlay;

        /// <summary>Manual 模式回调捕获的「退出关板」动作；非 null 表示当前是 Manual 常驻面板（续聊同面板重播用）。</summary>
        public Il2CppSystem.Action? ExitAction;

        /// <summary>游戏自己的结束回调（恢复 NPC/重开聊后菜单），整段对话真正结束时由 CloseIfManual 调用。</summary>
        public Il2CppSystem.Action? GameFinishCallback;
    }

    /// <summary>Manual 模式四个回调的钉住包（managed 中间委托 + IL2CPP 包装一并钉住，防 GC 死 thunk）。</summary>
    private sealed class ManualSuite
    {
        public System.Action? MContinue;
        public System.Action<Il2CppSystem.Action>? MPlayFirst;
        public System.Action<Il2CppSystem.Action>? MCanExit;
        public System.Action? MLoadFinish;
        public Il2CppSystem.Action? Continue;
        public Il2CppSystem.Action<Il2CppSystem.Action>? PlayFirst;
        public Il2CppSystem.Action<Il2CppSystem.Action>? CanExit;
        public Il2CppSystem.Action? LoadFinish;
    }

    private static readonly Dictionary<string, Entry> Entries = new();

    /// <summary>每个包 key 钉一份「我们自己的」结束回调（进程生命周期内不释放）。</summary>
    private static readonly Dictionary<string, Il2CppSystem.Action> PinnedFinish = new();
    private static readonly Dictionary<string, System.Action> PinnedFinishManaged = new();

    /// <summary>Manual 模式回调钉住表（按包 key）。</summary>
    private static readonly Dictionary<string, ManualSuite> PinnedManual = new();

    /// <summary>DayChatPatch 登记：新一轮对话开始（重置跨轮记录与轮数）；续聊重开不经过这里。</summary>
    public static void Register(DialogPackage package, PendingReplacement replacement)
    {
        if (package == null || replacement == null) return;
        var key = PendingReplacementStore.KeyOf(package);
        Entries[key] = new Entry
        {
            Package = package,
            CharacterKey = replacement.CharacterKey,
            Segments = replacement.Segments,
        }; // ExitAction/StartPlay/GameFinishCallback 均为新实例初始 null，自动复位
    }

    /// <summary>Manual 直开重入保护（防御：若游戏内部实现又走 OpenDialogMenu，避免递归重定向）。</summary>
    private static bool _manualOpening;

    /// <summary>
    /// 首轮即用 Manual 模式打开（OpenDialogMenuPatch 重定向调用）：整段对话期间面板不关，
    /// 轮间零闪烁。wrappedFinish = 已包装的游戏结束回调（真正散场时调用）。
    /// </summary>
    public static bool TryOpenManualFirst(string key, DialogPackage package, Il2CppSystem.Action wrappedFinish)
    {
        if (_manualOpening) return false;
        try
        {
            if (!PluginContext.Settings.Enabled) return false;
            if (!Entries.TryGetValue(key, out var entry)) return false;
            entry.GameFinishCallback = wrappedFinish;
            entry.ExitAction = null;
            entry.StartPlay = null;
            _manualOpening = true;
            try
            {
                OpenManualRound(key, entry);
            }
            finally
            {
                _manualOpening = false;
            }
            return true;
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] Manual 直开失败（{key}，回退普通打开）: {ex}");
            return false;
        }
    }

    /// <summary>该包是否已登记续聊（OpenDialogMenuPatch 据此决定是否包装结束回调）。</summary>
    public static bool Has(string key) => Entries.ContainsKey(key);

    /// <summary>正在执行结束回调的包 key（OpenDialogMenuPatch 包装器设置）：
    /// 聊后菜单就藏在这个回调里，抑制判断只在这个窗口内生效，杜绝跨包/历史误抑制。</summary>
    private static string? _finishingKey;

    public static void BeginFinish(string key) => _finishingKey = key;
    public static void EndFinish() => _finishingKey = null;

    /// <summary>
    /// 聊后菜单（邀请/关于食材…）抑制判断：仅在「正在收尾的对话包」属于该角色且满足
    /// 续聊条件时返回 true——菜单不打开，由我们 600ms 后直接重开下一轮，避免菜单与对话互相覆盖。
    /// 对话真正结束时（玩家点了「结束对话」/本轮无输入）返回 false，菜单照常打开。
    /// </summary>
    public static bool ShouldSuppressAfterChatMenu(string characterLabel)
    {
        try
        {
            if (!PluginContext.Settings.Enabled) return false;
            if (_finishingKey == null) return false; // 不在结束回调窗口内（如首次开菜单），照常
            if (!Entries.TryGetValue(_finishingKey, out var entry)) return false;
            if (entry.CharacterKey != characterLabel) return false;
            if (entry.Rounds >= MaxRounds || !IsDayPhase()) return false;
            var snapshot = DialogPannelPatch.SnapshotRound(_finishingKey);
            if (snapshot == null) return false;
            return snapshot.Value.HadInput && !snapshot.Value.ExitRequested;
        }
        catch
        {
            return false; // 异常放行，菜单照常打开
        }
    }

    /// <summary>判断结束回调是不是我们钉的那份（避免重开时被重复包装）。</summary>
    public static bool IsOwnFinishCallback(string key, Il2CppSystem.Action? action)
    {
        return action != null
               && PinnedFinish.TryGetValue(key, out var own)
               && ReferenceEquals(own, action);
    }

    /// <summary>取（并钉住）我们的结束回调：续聊重开时作为 onFinishCallback 传入。</summary>
    public static Il2CppSystem.Action GetOwnFinishCallback(string key)
    {
        if (!PinnedFinish.TryGetValue(key, out var action))
        {
            System.Action managed = () => OnRoundFinished(key);
            action = (Il2CppSystem.Action)managed;
            PinnedFinishManaged[key] = managed; // 中间委托一并钉住
            PinnedFinish[key] = action;
        }
        return action;
    }

    /// <summary>一轮播完（游戏的结束回调链触发，主线程）：检查停止条件，快照记录，延时重开。
    /// force = 玩家显式终止（「结束对话」按钮的直达驱动）：跳过防抖——1 秒防抖只针对同一轮
    /// 结束回调的重复触发，绝不能吞掉退出意图（吞了 GameFinishCallback 不会执行，玩家输入永久锁死）。</summary>
    public static void OnRoundFinished(string key, bool force = false)
    {
        try
        {
            if (!PluginContext.Settings.Enabled) return;
            if (!Entries.TryGetValue(key, out var entry)) return;

            var snapshot = DialogPannelPatch.SnapshotRound(key);
            var isExit = force || snapshot?.ExitRequested == true;

            // 防抖：结束回调可能二次触发（重开间隔短、撞上未关完的池化面板），1 秒内只认一次。
            // 退出意图（玩家点「结束对话」）不受此窗口限制。
            if (!isExit)
            {
                var now = System.DateTime.UtcNow;
                if ((now - entry.LastFinishUtc).TotalMilliseconds < 1000)
                {
                    PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 1 秒内的重复结束回调，忽略");
                    return;
                }
                entry.LastFinishUtc = now;
            }

            if (snapshot == null)
            {
                if (isExit) CloseIfManual(key, entry); // 快照缺失但玩家明确要退：尽力关板恢复输入
                return; // 非 AI 对话（未经过我们的面板替换流程）
            }
            if (snapshot.Value.ExitRequested)
            {
                if (entry.ExitHandled) return; // 退出已处理（结束回调链多次触发），幂等返回
                entry.ExitHandled = true;
                PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 玩家已选择结束对话，不再续聊");
                TryRecordMemory(key, snapshot.Value.Transcript);
                CloseIfManual(key, entry);
                return;
            }
            if (!snapshot.Value.HadInput)
            {
                PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 本轮无玩家输入（独白包/挂机），不续聊");
                // 独白/挂机无交互内容，不记记忆
                CloseIfManual(key, entry);
                return;
            }
            if (entry.Rounds >= MaxRounds)
            {
                PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 已达轮数安全上限 {MaxRounds}，停止续聊");
                TryRecordMemory(key, snapshot.Value.Transcript);
                CloseIfManual(key, entry);
                return;
            }
            if (!IsDayPhase())
            {
                TryRecordMemory(key, snapshot.Value.Transcript); // 离开白天场景，对话被截断也记录
                CloseIfManual(key, entry);
                return;
            }
            if (OpenDialogMenuPatch.RecentlyOpened(TimeSpan.FromSeconds(2)))
            {
                PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 结束后有其他对话接棒（如羁绊事件），不续聊");
                TryRecordMemory(key, snapshot.Value.Transcript);
                CloseIfManual(key, entry);
                return;
            }

            entry.CarriedTranscript = Cap(snapshot.Value.Transcript);
            entry.SkipLeadingSelfNextRound = snapshot.Value.EndedWithPlayerSpeech;
            entry.Rounds++;
            var round = entry.Rounds;
            // 注意：不得在此关闭残留的聊后菜单——Manual 模式下对话框面板始终开着，
            // 关它下面的菜单违反面板栈顺序（ClosePanel 抛 InvalidOperationException 并损坏栈记账，
            // 后续关对话框会 NRE 卡死）。菜单被 HideVisual 压着不会闪回，散场时随栈自然恢复。
            PluginContext.Log.LogInfo(
                $"[MystiaAI] 续聊: 包 {key} 第 {round} 轮已排队（携带记录 {entry.CarriedTranscript.Length} 字）");
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(ReopenDelayMs).ConfigureAwait(false); }
                catch { /* 忽略，照常派发 */ }
                MainThreadDispatcher.Post(() => Reopen(key, round));
            });
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] DialogContinuation.OnRoundFinished 异常（{key}）: {ex}");
        }
    }

    /// <summary>重开同一对话包（主线程）：重登记（带跨轮记录）后直接调 OpenDialogMenu。</summary>
    private static void Reopen(string key, int round)
    {
        try
        {
            if (!PluginContext.Settings.Enabled) return;
            if (!Entries.TryGetValue(key, out var entry)) return;
            if (entry.Rounds != round) return; // 期间玩家开始了新对话，放弃这次续聊
            if (!IsDayPhase())
            {
                PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 已离开白天场景，取消重开");
                return;
            }
            if (OpenDialogMenuPatch.RecentlyOpened(TimeSpan.FromMilliseconds(ReopenDelayMs - 50)))
            {
                PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 等待期间有其他对话打开，取消重开");
                return;
            }
            if (UnityObjectGuard.IsDead(entry.Package))
            {
                PluginContext.Log.LogWarning($"[MystiaAI] 续聊: 包 {key} 对话包已销毁，取消重开");
                return;
            }

            PendingReplacementStore.Register(entry.Package, new PendingReplacement
            {
                CharacterKey = entry.CharacterKey,
                Segments = entry.Segments,
                CarriedTranscript = entry.CarriedTranscript,
                SkipLeadingSelf = entry.SkipLeadingSelfNextRound,
            });

            if (entry.ExitAction != null)
            {
                // Manual 常驻面板：不关板，同面板直接重播（零闪烁）
                if (DialogPannelPatch.TryReplayInPlace(key))
                {
                    PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 第 {round} 轮已同面板重播");
                    return;
                }
                PluginContext.Log.LogWarning($"[MystiaAI] 续聊: 包 {key} 同面板重播失败，回退为 Manual 新开面板");
                CloseIfManual(key, entry);
            }

            // 首轮续聊（或重播失败回退）：Manual 模式打开——播完不关板，后续轮次同面板重播
            OpenManualRound(key, entry);
            PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 第 {round} 轮已重开（Manual 模式）");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] DialogContinuation.Reopen 异常（{key}）: {ex}");
        }
    }

    /// <summary>Manual 模式打开新一轮：面板播完不关板，回调驱动续播/退出。</summary>
    private static void OpenManualRound(string key, Entry entry)
    {
        var suite = GetManualSuite(key);
        UniversalGameManager.OpenManualDialogMenu(
            entry.Package,
            suite.Continue!,
            suite.PlayFirst!,
            suite.CanExit!,
            suite.LoadFinish!,
            false,
            AdpUIPanelManager.PanelVisualMode.HideVisual);
    }

    // ---- 重播协程完成监听（Manual 模式的关键补件）----

    /// <summary>
    /// Manual 面板播完回调只对开板时的首个循环触发一次（DialogPannel.cs:918-920），
    /// 我们重播的协程结束没有任何通知——靠协程对象假死来侦测（每帧轮询）。
    /// </summary>
    private sealed class ReplayWatch
    {
        public string Key = string.Empty;
        public UnityEngine.Coroutine? Coroutine;
        public Common.DialogUtility.DialogPannel? Panel;
    }

    private static readonly List<ReplayWatch> _watchers = new();

    /// <summary>登记一个重播协程的完成监听（TryReplayInPlace 调用）。</summary>
    public static void WatchReplayCompletion(string key, UnityEngine.Coroutine? coroutine,
        Common.DialogUtility.DialogPannel? panel)
    {
        if (coroutine == null) return;
        lock (_watchers)
        {
            _watchers.Add(new ReplayWatch { Key = key, Coroutine = coroutine, Panel = panel });
        }
    }

    /// <summary>协程死活探测：Coroutine 不是 UnityEngine.Object，无 fake-null；包装被回收/原生销毁时访问 Pointer 抛异常即视为结束。</summary>
    private static bool IsCoroutineDead(UnityEngine.Coroutine? co)
    {
        if (co == null) return true;
        try
        {
            _ = co.Pointer;
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>每帧泵（EventSystem.Update 链）：重播协程对象假死 = 该轮播完 → 走正常的一轮结束流程。</summary>
    public static void PollReplayWatchers()
    {
        if (_watchers.Count == 0) return;
        List<string>? finished = null;
        lock (_watchers)
        {
            for (var i = _watchers.Count - 1; i >= 0; i--)
            {
                var w = _watchers[i];
                if (IsCoroutineDead(w.Coroutine) || UnityObjectGuard.IsDead(w.Panel))
                {
                    (finished ??= new List<string>()).Add(w.Key);
                    _watchers.RemoveAt(i);
                }
            }
        }
        if (finished == null) return;
        foreach (var key in finished)
        {
            PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 重播轮播完（协程结束侦测）");
            OnRoundFinished(key);
        }
    }

    /// <summary>Manual 面板的真正关板（停止续聊时）：调用捕获的退出动作，并补调游戏的结束回调（恢复 NPC/重开聊后菜单）。</summary>
    private static void CloseIfManual(string key, Entry entry)
    {
        if (entry.ExitAction == null) return; // 非 Manual 轮，游戏自己关板
        try
        {
            entry.ExitAction.Invoke();
            PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} Manual 面板已退出关板");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] Manual 面板退出失败（{key}）: {ex.Message}");
        }
        entry.ExitAction = null;
        entry.StartPlay = null;
        if (entry.GameFinishCallback != null)
        {
            try
            {
                entry.GameFinishCallback.Invoke();
            }
            catch (Exception ex)
            {
                PluginContext.Log.LogWarning($"[MystiaAI] 游戏结束回调异常（{key}）: {ex.Message}");
            }
            entry.GameFinishCallback = null;
        }
    }

    /// <summary>取（并钉住）Manual 模式四个回调。</summary>
    private static ManualSuite GetManualSuite(string key)
    {
        if (PinnedManual.TryGetValue(key, out var suite) && suite.Continue != null)
            return suite;

        suite = new ManualSuite();
        suite.MContinue = () => OnRoundFinished(key);
        suite.MPlayFirst = a =>
        {
            if (Entries.TryGetValue(key, out var e)) e.StartPlay = a;
        };
        suite.MCanExit = a =>
        {
            if (Entries.TryGetValue(key, out var e)) e.ExitAction = a;
        };
        suite.MLoadFinish = () =>
        {
            if (!Entries.TryGetValue(key, out var e) || e.StartPlay == null) return;
            try { e.StartPlay.Invoke(); }
            catch (Exception ex) { PluginContext.Log.LogWarning($"[MystiaAI] Manual 首句放行失败（{key}）: {ex.Message}"); }
        };
        suite.Continue = (Il2CppSystem.Action)suite.MContinue;
        suite.PlayFirst = (Il2CppSystem.Action<Il2CppSystem.Action>)suite.MPlayFirst;
        suite.CanExit = (Il2CppSystem.Action<Il2CppSystem.Action>)suite.MCanExit;
        suite.LoadFinish = (Il2CppSystem.Action)suite.MLoadFinish;
        PinnedManual[key] = suite;
        return suite;
    }

    /// <summary>白天时段才允许续聊（与 GetGameTimeText 的白天分支同口径）。</summary>
    private static bool IsDayPhase()
    {
        try
        {
            var phase = RunTimeScheduler.CurrentGamePhase;
            return phase is RunTimeScheduler.GamePhase.Day
                or RunTimeScheduler.GamePhase.DayTimeEnd
                or RunTimeScheduler.GamePhase.DayToPreperation;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 对话真正结束时记录长期记忆（MemoryStore.Record 内部会截取 transcript 尾部、
    /// 去重、按配置上限裁剪）。失败只记日志，不影响对话流程。
    /// </summary>
    private static void TryRecordMemory(string key, string transcript)
    {
        try
        {
            if (!Entries.TryGetValue(key, out var entry)) return;
            if (string.IsNullOrWhiteSpace(entry.CharacterKey)) return;
            MemoryStore.Record(entry.CharacterKey, transcript,
                DialogPannelPatch.GetGameTimeText(), ChatScene.DayChat.ToString());
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] 记忆记录失败（不影响对话）: {ex.Message}");
        }
    }

    /// <summary>transcript 超限时按行丢弃最旧内容。</summary>
    private static string Cap(string transcript)    {
        if (string.IsNullOrEmpty(transcript) || transcript.Length <= CarriedTranscriptLimit)
            return transcript ?? string.Empty;
        var cut = transcript.Length - CarriedTranscriptLimit;
        var nl = transcript.IndexOf('\n', cut);
        return nl >= 0 && nl + 1 < transcript.Length ? transcript.Substring(nl + 1) : transcript.Substring(cut);
    }
}
