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
    }

    private static readonly Dictionary<string, Entry> Entries = new();

    /// <summary>每个包 key 钉一份「我们自己的」结束回调（进程生命周期内不释放）。</summary>
    private static readonly Dictionary<string, Il2CppSystem.Action> PinnedFinish = new();
    private static readonly Dictionary<string, System.Action> PinnedFinishManaged = new();

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
        };
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

    /// <summary>一轮播完（游戏的结束回调链触发，主线程）：检查停止条件，快照记录，延时重开。</summary>
    public static void OnRoundFinished(string key)
    {
        try
        {
            if (!PluginContext.Settings.Enabled) return;
            if (!Entries.TryGetValue(key, out var entry)) return;

            // 防抖：结束回调可能二次触发（重开间隔短、撞上未关完的池化面板），1 秒内只认一次
            var now = System.DateTime.UtcNow;
            if ((now - entry.LastFinishUtc).TotalMilliseconds < 1000)
            {
                PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 1 秒内的重复结束回调，忽略");
                return;
            }
            entry.LastFinishUtc = now;

            var snapshot = DialogPannelPatch.SnapshotRound(key);
            if (snapshot == null) return; // 非 AI 对话（未经过我们的面板替换流程）
            if (snapshot.Value.ExitRequested)
            {
                PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 玩家已选择结束对话，不再续聊");
                TryRecordMemory(key, snapshot.Value.Transcript);
                return;
            }
            if (!snapshot.Value.HadInput)
            {
                PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 本轮无玩家输入（独白包/挂机），不续聊");
                // 独白/挂机无交互内容，不记记忆
                return;
            }
            if (entry.Rounds >= MaxRounds)
            {
                PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 已达轮数安全上限 {MaxRounds}，停止续聊");
                TryRecordMemory(key, snapshot.Value.Transcript);
                return;
            }
            if (!IsDayPhase())
            {
                TryRecordMemory(key, snapshot.Value.Transcript); // 离开白天场景，对话被截断也记录
                return;
            }
            if (OpenDialogMenuPatch.RecentlyOpened(TimeSpan.FromSeconds(2)))
            {
                PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 结束后有其他对话接棒（如羁绊事件），不续聊");
                TryRecordMemory(key, snapshot.Value.Transcript);
                return;
            }

            entry.CarriedTranscript = Cap(snapshot.Value.Transcript);
            entry.SkipLeadingSelfNextRound = snapshot.Value.EndedWithPlayerSpeech;
            entry.Rounds++;
            var round = entry.Rounds;
            // 防闪回：点「闲聊」时菜单只是隐藏未出栈，第一轮对话框关闭时栈会把它还原出来
            // （续聊重开前的几百毫秒里闪现一次）。这里先把它真正关掉，栈里就没有可还原的了
            CloseLingeringChatMenu();
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
            UniversalGameManager.OpenDialogMenu(
                entry.Package,
                GetOwnFinishCallback(key),
                null,
                // HideVisual：打开新一轮对话时隐藏下层残留面板（点「闲聊」时被 ClosePanel
                // 但未出栈的选项菜单，会在上一轮对话框关闭时被面板栈自动还原视觉，
                // 与续聊对话重叠）；对话全部结束后菜单视觉自然恢复
                AdpUIPanelManager.PanelVisualMode.HideVisual);
            PluginContext.Log.LogInfo($"[MystiaAI] 续聊: 包 {key} 第 {round} 轮已重开");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] DialogContinuation.Reopen 异常（{key}）: {ex}");
        }
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

    /// <summary>真正关闭残留的聊后选项菜单面板（含隐藏中的实例），防止面板栈在对话间隙还原其视觉造成闪回。</summary>    /// <summary>
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

    /// <summary>真正关闭残留的聊后选项菜单面板（含隐藏中的实例），防止面板栈在对话间隙还原其视觉造成闪回。</summary>
    private static void CloseLingeringChatMenu()
    {
        try
        {
            foreach (var panel in UnityEngine.Object.FindObjectsOfType<DayScene.UI.DaySceneChatSelectionPannel>(true))
            {
                if (UnityObjectGuard.IsDead(panel)) continue;
                panel.ClosePanel();
                PluginContext.Log.LogInfo("[MystiaAI] 续聊: 已关闭残留的选项菜单面板（防闪回）");
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] 续聊: 关闭残留菜单面板失败（不影响续聊）: {ex.Message}");
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
