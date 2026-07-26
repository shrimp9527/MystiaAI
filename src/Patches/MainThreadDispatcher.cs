using System;
using System.Collections.Concurrent;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using MystiaAI.Core;
using UnityEngine;

namespace MystiaAI.Patches;

/// <summary>
/// 主线程派发器：AI 生成任务的 continuation 跑在线程池，改写 Unity UI 必须回到主线程。
///
/// 架构（第 4 轮闪退攻坚定稿）：纯托管队列 + 每帧 drain。
/// 派发通道（共用同一 ConcurrentQueue，幂等）：
/// 1. EventSystem.Update 的 Harmony postfix（主通道，实测工作）；
/// 2. ClassInjector 注入的 DispatcherBehaviour.Update（备选，实测未触发，仅作健康观察）。
/// 兜底：两通道都失效时 DialogPannel.OnGUI postfix 才 drain（OnGUI 事件内做原生 UI 调用
/// 有 native 崩溃风险，平时绝不启用）。
///
/// 为什么彻底移除 SynchronizationContext 通道：
/// UnitySynchronizationContext 是 native Unity 对象，ctx.Post(SendOrPostCallback) 会把
/// managed 委托交给 native 侧持有并稍后经 reverse P/Invoke 回调——崩溃转储（三个 dmp 同址
/// coreclr.dll+0x1d1fdd，.NET Runtime event 1023 内部错误）正是「native 调用已死 managed
/// thunk」的公共崩溃点。本类不再向 native 移交任何 managed 委托：
/// 队列与 drain 全程在 coreclr 内，drain 入口是 Harmony patch（由 Harmony 自己钉住）。
/// </summary>
internal static class MainThreadDispatcher
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static volatile bool _eventSystemChannelReady;
    private static volatile bool _updateChannelReady;
    private static bool _injectorRegistered;
    private static int _drainFrames;
    private static bool _injectionDeadWarned;

    /// <summary>EventSystem.Update 派发通道已 patch（DialogPannelPatch.Install 成功后标记）。</summary>
    public static void MarkEventSystemChannelReady()
    {
        _eventSystemChannelReady = true;
    }

    /// <summary>
    /// 建立注入 MonoBehaviour 的 Update drain 备选通道。在插件 Load 早期调用
    /// （DialogPannelPatch.Install），幂等。实测其 Update 不触发，仅作健康观察保留。
    /// </summary>
    public static void EnsureUpdateChannel()
    {
        if (_updateChannelReady) return;
        try
        {
            if (!_injectorRegistered)
            {
                ClassInjector.RegisterTypeInIl2Cpp<DispatcherBehaviour>();
                _injectorRegistered = true;
            }
            var go = new GameObject("MystiaAI_Dispatcher", Il2CppType.Of<DispatcherBehaviour>());
            UnityEngine.Object.DontDestroyOnLoad(go);
            _updateChannelReady = true;
            PluginContext.Log.LogInfo(
                "[MystiaAI] MainThreadDispatcher: 注入 MonoBehaviour 备选通道已建立（DontDestroyOnLoad）");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError(
                $"[MystiaAI] MainThreadDispatcher.EnsureUpdateChannel 失败（不影响主通道）: {ex}");
        }
    }

    /// <summary>把动作派发到主线程执行。任何线程可调；全程不离开 coreclr（纯托管队列）。</summary>
    public static void Post(Action action)
    {
        try
        {
            Queue.Enqueue(action);
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] MainThreadDispatcher.Post 异常: {ex}");
        }
    }

    /// <summary>每帧 drain（EventSystem.Update postfix / DispatcherBehaviour.Update 调用，主线程）。</summary>
    public static void Drain()
    {
        DrainQueue();

        // 注入通道健康检查：drain 跑了 300 帧而注入的 Update 从未触发，判定注入通道失效
        if (!_injectionDeadWarned && ++_drainFrames >= 300 && !DispatcherBehaviour.Fired)
        {
            _injectionDeadWarned = true;
            PluginContext.Log.LogWarning(
                "[MystiaAI] MainThreadDispatcher: 注入的 DispatcherBehaviour.Update 300 帧未触发，" +
                "注入通道判定失效，由 EventSystem.Update 主通道承担全部派发");
        }
    }

    /// <summary>
    /// OnGUI drain：仅当两条 Update 通道都失效时的兜底。
    /// IMGUI 事件（含鼠标点击）处理内部做原生 UI 调用有 native 崩溃风险，通道活着就绝不在此执行。
    /// </summary>
    public static void DrainFromOnGUI()
    {
        if (_eventSystemChannelReady || _updateChannelReady) return;
        DrainQueue();
    }

    private static void DrainQueue()
    {
        try
        {
            while (Queue.TryDequeue(out var action))
            {
                // 崩溃定位埋点：若下次闪退日志最后一条是「开始」而无「结束」，凶手就是这个 action
                PluginContext.Log.LogInfo("[MystiaAI] Dispatcher: 派发动作开始");
                Safe(action);
                PluginContext.Log.LogInfo("[MystiaAI] Dispatcher: 派发动作结束");
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] MainThreadDispatcher.DrainQueue 异常: {ex}");
        }
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] MainThreadDispatcher 派发动作异常: {ex}");
        }
    }
}

/// <summary>
/// Unity 对象存活判断：destroyed 的 Unity 对象走 fake-null（== null 为 true），
/// 被 GC 回收的 Il2Cpp 包装走 Pointer == IntPtr.Zero（或访问时抛 ObjectCollectedException），
/// 两种都判死。所有跨帧/跨线程持有的 Unity 引用使用前必查。
/// </summary>
internal static class UnityObjectGuard
{
    public static bool IsDead(UnityEngine.Object? obj)
    {
        if (obj == null) return true; // 真 null 或 Unity fake-null（已 Destroy）
        try
        {
            return obj.Pointer == IntPtr.Zero; // Il2Cpp 包装已被 GC 回收
        }
        catch
        {
            return true; // 访问 Pointer 抛异常（ObjectCollectedException 等）同样视为已死
        }
    }
}

/// <summary>
/// 注入 IL2CPP 的极简 MonoBehaviour：每帧 Update drain 派发队列。
/// 注册由 MainThreadDispatcher.EnsureUpdateChannel 完成（幂等）。
/// 实测 Update 从未触发（仅作健康观察保留），主通道是 EventSystem.Update postfix。
/// </summary>
internal sealed class DispatcherBehaviour : MonoBehaviour
{
    /// <summary>Update 是否触发过（注入通道健康检查用）。</summary>
    internal static bool Fired;

    private static bool _firstUpdateLogged;

    public DispatcherBehaviour(IntPtr pointer) : base(pointer) { }

    public DispatcherBehaviour()
        : base(ClassInjector.DerivedConstructorPointer<DispatcherBehaviour>())
    {
        ClassInjector.DerivedConstructorBody(this);
    }

    private void Update()
    {
        try
        {
            Fired = true;
            if (!_firstUpdateLogged)
            {
                _firstUpdateLogged = true;
                PluginContext.Log.LogInfo("[MystiaAI] DispatcherBehaviour: Update 首次触发，注入通道工作正常");
            }
            MainThreadDispatcher.Drain();
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] DispatcherBehaviour.Update 异常: {ex}");
        }
    }
}
