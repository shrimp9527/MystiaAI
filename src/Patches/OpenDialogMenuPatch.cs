using System.Collections.Generic;
using System.Threading.Tasks;
using Common.UI;
using DEYU.AdpUISystem.Managers;
using GameData.Profile;
using HarmonyLib;
using MystiaAI.Core;

using Il2CppReplaceDict = Il2CppSystem.Collections.Generic.Dictionary<int, string>;

namespace MystiaAI.Patches;

/// <summary>
/// 旧路径兜底：拦截 Common.UI.UniversalGameManager.OpenDialogMenu，
/// 对有待替换记录的 DialogPackage 注入 overrideReplaceTextCallback（按 dialogId 换文本）。
/// 实测白天闲聊不经过本路径（走 ADP 直开 DialogPannel，由 DialogPannelPatch 处理），
/// 本补丁仅为其他经过 OpenDialogMenu 的对话路径保留；day-chat 登记的 PendingReplacement.Texts
/// 恒为空表，因此即便命中也是无副作用的 no-op。剧情对话包不进 PendingReplacementStore，不受影响。
/// 注意：本补丁用特性式注册（Plugin.Load 的 PatchAll 收编），与 DialogPannelPatch 的手动 Install 不同。
/// </summary>
[HarmonyPatch(typeof(UniversalGameManager), nameof(UniversalGameManager.OpenDialogMenu),
    typeof(DialogPackage), typeof(Il2CppSystem.Action),
    typeof(Il2CppSystem.Action<Il2CppReplaceDict>), typeof(AdpUIPanelManager.PanelVisualMode))]
internal static class OpenDialogMenuPatch
{
    /// <summary>
    /// 钉住注入游戏的回调：Il2CppSystem.Action 交给 native 侧持有后经 reverse P/Invoke 调用，
    /// managed 包装被 GC 后游戏再调就是 .NET Runtime 内部错误（三个 dmp 同址 coreclr+0x1d1fdd
    /// 已证实这类死 thunk 崩溃）。按包 key 各钉一份（含中间 System.Action），进程生命周期内不释放。
    /// </summary>
    private static readonly Dictionary<string, Il2CppSystem.Action<Il2CppReplaceDict>> PinnedCallbacks = new();
    private static readonly Dictionary<string, System.Action<Il2CppReplaceDict>> PinnedManaged = new();

    /// <summary>自动续聊：包装后的结束回调钉住表（按包 key）。</summary>
    private static readonly Dictionary<string, Il2CppSystem.Action> PinnedFinishWrap = new();
    private static readonly Dictionary<string, System.Action> PinnedFinishWrapManaged = new();

    /// <summary>任何 OpenDialogMenu 调用的最近时刻（含未登记的剧情/事件包），续聊冲突检测用。</summary>
    internal static System.DateTime LastOpenDialogMenuUtc = System.DateTime.MinValue;

    /// <summary>最近 window 内是否有对话被打开（如羁绊事件接棒），有则续聊让路。</summary>
    internal static bool RecentlyOpened(System.TimeSpan window)
        => System.DateTime.UtcNow - LastOpenDialogMenuUtc < window;

    private static bool Prefix(
        DialogPackage dialogPackage,
        ref Il2CppSystem.Action onFinishCallback,
        ref Il2CppSystem.Action<Il2CppReplaceDict>? overrideReplaceTextCallback,
        AdpUIPanelManager.PanelVisualMode previousPanelVisualMode)
    {
        LastOpenDialogMenuUtc = System.DateTime.UtcNow; // 无条件记录，含剧情/事件包（续聊冲突检测）
        try
        {
            if (!PluginContext.Settings.Enabled) return true;
            if (dialogPackage == null) return true;
            if (!PendingReplacementStore.Contains(dialogPackage)) return true;

            var key = PendingReplacementStore.KeyOf(dialogPackage);

            // 自动续聊：包装结束回调，播完一轮后由 DialogContinuation 评估是否重开；
            // 续聊重开时传入的是我们自己钉的回调，不重复包装
            if (DialogContinuation.Has(key) && !DialogContinuation.IsOwnFinishCallback(key, onFinishCallback))
            {
                var capturedKey = key;
                var original = onFinishCallback;
                System.Action managed = () =>
                {
                    DialogContinuation.BeginFinish(capturedKey); // 菜单抑制窗口：原回调执行期间有效
                    try
                    {
                        original?.Invoke();
                    }
                    catch (System.Exception ex)
                    {
                        PluginContext.Log.LogError($"[MystiaAI] OpenDialogMenu: 原结束回调异常（{capturedKey}）: {ex}");
                    }
                    finally
                    {
                        DialogContinuation.EndFinish();
                    }
                    DialogContinuation.OnRoundFinished(capturedKey);
                };
                var wrapped = (Il2CppSystem.Action)managed;
                PinnedFinishWrapManaged[capturedKey] = managed;
                PinnedFinishWrap[capturedKey] = wrapped;
                onFinishCallback = wrapped;

                // 首轮即 Manual 模式打开：整段对话期间面板不关，轮间零闪烁（失败回退原流程）
                if (DialogContinuation.TryOpenManualFirst(key, dialogPackage, onFinishCallback))
                {
                    PluginContext.Log.LogInfo($"[MystiaAI] OpenDialogMenu: 包 {key} 已改为 Manual 模式打开（续聊零闪烁）");
                    return false; // 原 OpenDialogMenu 不再执行
                }
            }

            if (overrideReplaceTextCallback != null) return true; // 游戏/其他 mod 已提供回调，不覆盖
            if (!PendingReplacementStore.TryMarkInjected(dialogPackage)) return true; // 防多层级重复链式叠加
            if (!PinnedCallbacks.TryGetValue(key, out var callback))
            {
                var capturedKey = key;
                System.Action<Il2CppReplaceDict> managed = dict => Inject(dict, capturedKey);
                callback = (Il2CppSystem.Action<Il2CppReplaceDict>)managed;
                PinnedManaged[key] = managed;     // 中间委托一并钉住
                PinnedCallbacks[key] = callback;  // IL2CPP 包装钉住，防 GC 后 native 回调死 thunk
            }
            overrideReplaceTextCallback = callback;
            PluginContext.Log.LogInfo($"[MystiaAI] OpenDialogMenu: 包 {key} 命中待替换记录，已注入 overrideReplaceTextCallback（已钉住）");
            return true;
        }
        catch (System.Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] OpenDialogMenuPatch.Prefix 异常: {ex}");
            return true;
        }
    }

    /// <summary>
    /// 回调本体（游戏主线程执行）：把已完成任务的 AI 文本按 dialogId 写入替换表。
    /// 只读不消费（Consume 归 DialogPannelPatch），任务未完成/失败的段保持原文。
    /// </summary>
    private static void Inject(Il2CppReplaceDict? dict, string packageKey)
    {
        // 崩溃定位埋点：native 经 reverse P/Invoke 调进来，若日志停在这条之后无「退出」，
        // 说明回调体内部是凶手
        PluginContext.Log.LogInfo($"[MystiaAI] OpenDialogMenu: overrideReplaceTextCallback 进入（包 {packageKey}）");
        try
        {
            if (dict == null) return;
            var replacement = PendingReplacementStore.Peek(packageKey);
            if (replacement?.Texts == null || replacement.Texts.Count == 0) return;

            var injected = 0;
            foreach (var kv in replacement.Texts)
            {
                Task<string> task = kv.Value;
                if (task == null || !task.IsCompletedSuccessfully) continue;
                dict[kv.Key] = task.Result;
                injected++;
            }
            if (injected > 0)
                PluginContext.Log.LogInfo($"[MystiaAI] OpenDialogMenu: 包 {packageKey} 回调写入 {injected} 段替换文本");
        }
        catch (System.Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] OpenDialogMenuPatch.Inject 异常: {ex}");
        }
        PluginContext.Log.LogInfo($"[MystiaAI] OpenDialogMenu: overrideReplaceTextCallback 退出（包 {packageKey}）");
    }
}
