using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MystiaAI.Core;
using NightScene.GuestManagementUtility;
using UnityEngine;
using DialogBoxUI = NightScene.UI.GuestManagementUtility.DialogBoxUI;
using EvalulationBoxUI = NightScene.UI.GuestManagementUtility.EvalulationBoxUI;

namespace MystiaAI.Patches;

/// <summary>
/// 【临时诊断·第四轮】营业闲聊/评价语气泡侦察——只用已证明安全的锚点，零夜场景方法 patch。
///
/// 三轮崩溃（均 coreclr.dll+0x1d1fdd，native 回调已死 managed thunk）教训：
/// 逐个挂夜场景方法 = 打地鼠。本轮战略转向，全类只剩两个安全锚点：
/// 1. GuestGroupController.PostEvaluation postfix（基类+稀客，三轮实机证明安全）：
///    参数日志 + __instance 信息 dump（稀客身份/订单菜名，逐条独立 try-catch）+ 气泡反射 dump；
/// 2. EventSystem.Update postfix（白天链路主通道，一直稳定）上的帧节流气泡 watcher：
///    每 30 帧 FindObjectsOfType&lt;DialogBoxUI&gt;(含 inactive，子类 EvalulationBoxUI 一并捞)，
///    实例（指针+文本）有变化才打日志——纯观察，不 patch 任何夜场景方法。
/// 一次营业即可还原：气泡真实类型、创建时机、文本写入口、followTarget 关联。
/// 定位完成后本文件整体删除（含 Plugin.cs 里的 Install 一行）。
/// </summary>
internal static class NightDiagPatch
{
    private const string Tag = "[MystiaAI-DIAG]";

    public static void Install(Harmony harmony)
    {
        // 锚点 1：评价流程入口（×2 已安全）
        Hook(harmony, typeof(GuestGroupController), nameof(GuestGroupController.PostEvaluation),
            null, nameof(PostEvalHit));
        Hook(harmony, typeof(SpecialGuestsController), nameof(SpecialGuestsController.PostEvaluation),
            null, nameof(PostEvalHit));

        // 锚点 2：每帧必跑的引擎通道（DialogPannelPatch 同款，已安全）挂帧节流 watcher
        Hook(harmony, typeof(UnityEngine.EventSystems.EventSystem),
            nameof(UnityEngine.EventSystems.EventSystem.Update),
            p => p.Length == 0, nameof(FrameWatcher));

        PluginContext.Log.LogInfo($"{Tag} 第四轮诊断注册完毕（2 锚点 + 帧 watcher，零夜场景方法 patch）");
    }

    /// <summary>按参数形态选定目标重载并注册日志 postfix，回读自检。match 为 null 时取第一个候选。</summary>
    private static void Hook(Harmony harmony, Type type, string methodName,
        Func<ParameterInfo[], bool>? match, string handler)
    {
        try
        {
            var candidates = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name == methodName && !m.IsGenericMethodDefinition)
                .ToList();
            var target = match == null
                ? candidates.FirstOrDefault()
                : candidates.FirstOrDefault(m => match(m.GetParameters()));
            if (target == null)
            {
                PluginContext.Log.LogError($"{Tag} 自检失败: 无法解析 {type.FullName}.{methodName}");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(NightDiagPatch), handler));
            var ok = (Harmony.GetPatchInfo(target)?.Postfixes?.Count ?? 0) > 0;
            PluginContext.Log.LogInfo(
                ok ? $"{Tag} 自检通过: {Describe(target)}" : $"{Tag} 自检失败: {Describe(target)} 回读不到 postfix");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"{Tag} 自检失败: 注册 {type.FullName}.{methodName} 异常: {ex.Message}");
        }
    }

    // ---- 锚点 1：PostEvaluation（已证明安全）----

    private static int _postEvalHits;
    private static object? _pendingInstance;
    private static bool _insidePostfix;

    /// <summary>
    /// 只做计数 + 节流日志 + 记录实例，重活全部延迟到帧 watcher。
    /// 教训：在 postfix 里直接 PeekOrders/FindObjectsOfType 会重入触发 PostEvaluation 自身，
    /// 无限递归 → 0xC00000FD 栈溢出（443 次命中后崩溃的实锤）。
    /// </summary>
    private static void PostEvalHit(object __instance, MethodBase __originalMethod, object[] __args)
    {
        if (_insidePostfix) return; // 嵌套命中静默放行，掐断递归
        try
        {
            _insidePostfix = true;
            _postEvalHits++;
            _pendingInstance = __instance;
            if (_postEvalHits <= 3 || _postEvalHits % 100 == 0)
                PluginContext.Log.LogInfo($"{Tag} 命中 {Name(__originalMethod)}{FmtInstance(__instance)}{FmtArgs(__args)} (#{_postEvalHits})");
        }
        catch { /* 诊断日志绝不上抛 */ }
        finally { _insidePostfix = false; }
    }

    /// <summary>在帧 watcher 上下文里执行的评价上下文 dump（稀客身份/订单菜名/气泡实例），逐条独立 try-catch。</summary>
    private static void DumpPostEvalContext(object? instance)
    {
        // 稀客身份（基类 patch 命中普客时 TryCast 为 null，也是有效信息）
        try
        {
            if (instance is GuestGroupController ggc)
            {
                var spc = ggc.TryCast<SpecialGuestsController>();
                var guest = spc?.SpecialGuest;
                PluginContext.Log.LogInfo(guest != null
                    ? $"{Tag}   评价者: 稀客 {guest.stringId} (id={guest.Id})"
                    : $"{Tag}   评价者: 非稀客（{instance.GetType().Name}）");
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"{Tag}   评价者读取失败: {ex.Message}");
        }

        // 当前订单菜名（正式实现的 dish 取数验证）
        try
        {
            if (instance is GuestGroupController ggc2)
            {
                var dish = ggc2.PeekOrders()?.ServFood?.Text?.Name;
                PluginContext.Log.LogInfo($"{Tag}   订单栈顶菜名: {(string.IsNullOrWhiteSpace(dish) ? "<取不到>" : $"「{dish}」")}");
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"{Tag}   订单菜名读取失败: {ex.Message}");
        }

        DumpBubbleUi(typeof(EvalulationBoxUI));
        DumpBubbleUi(typeof(DialogBoxUI));
    }

    // ---- 锚点 2：帧节流气泡 watcher（纯观察，零 patch 风险）----

    private static int _frame;
    /// <summary>实例指针 → 上次记录的文本；每轮 sweep 重建（死实例自然淘汰）。</summary>
    private static Dictionary<IntPtr, string> _lastText = new();

    private static void FrameWatcher()
    {
        try
        {
            if (++_frame < 600 || _frame % 30 != 0) return; // 启动延迟 600 帧（约 10s，标题到达后）
            // PostEvaluation 延迟 dump：脱离原调用栈执行，杜绝重入递归
            if (_pendingInstance != null)
            {
                var inst = _pendingInstance;
                _pendingInstance = null;
                // 相位闸门：启动加载期游戏会补调 PostEvaluation，此时实例是半成品，
                // PeekOrders 等原生调用会触发 coreclr.dll+0x1d1fdd（签名 A）崩溃。
                if (NightChatPatch.IsNightWorkPhase())
                    DumpPostEvalContext(inst);
                else
                    PluginContext.Log.LogInfo($"{Tag} 非营业场景，评价上下文 dump 跳过");
            }
            WatchBubbles();
        }
        catch { /* 绝不上抛 */ }
    }

    private static void WatchBubbles()
    {
        var all = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(typeof(DialogBoxUI)), true);
        var current = new Dictionary<IntPtr, string>(all.Length);
        foreach (var obj in all)
        {
            try
            {
                var box = obj.TryCast<DialogBoxUI>();
                if (box == null || UnityObjectGuard.IsDead(box)) continue;
                var ptr = box.Pointer;

                string text;
                try { text = box.text == null ? "<无TMP>" : box.text.text ?? "<空>"; }
                catch { text = "<不可读>"; }
                current[ptr] = text;

                // 变化检测：新实例或文本变了才打日志
                if (_lastText.TryGetValue(ptr, out var prev) && prev == text) continue;

                var kind = box.TryCast<EvalulationBoxUI>() != null ? "EvalulationBoxUI" : "DialogBoxUI";
                var go = box.gameObject;
                string follow;
                try
                {
                    var target = box.m_WorldSpaceUITracker?.m_FollowTarget;
                    follow = target == null ? "<无>" : target.name;
                }
                catch { follow = "<不可读>"; }

                PluginContext.Log.LogInfo(
                    $"{Tag} 气泡{( _lastText.ContainsKey(ptr) ? "文本变化" : "新实例")}: 类型={kind} " +
                    $"路径={BuildPath(box.transform)} active={go.activeInHierarchy} " +
                    $"follow={follow} 文本=「{Trunc(text)}」");
            }
            catch (Exception ex)
            {
                PluginContext.Log.LogWarning($"{Tag} 单气泡观察失败: {ex.Message}");
            }
        }
        _lastText = current;
    }

    /// <summary>FindObjectsOfType（含 inactive）dump 某类气泡的全部实例：路径 / active / 当前文本。</summary>
    private static void DumpBubbleUi(Type uiType)
    {
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(uiType), true);
            PluginContext.Log.LogInfo($"{Tag} 反射 dump: {uiType.Name} 全场景共 {all.Length} 个实例");
            foreach (var obj in all)
            {
                try
                {
                    var box = obj.TryCast<DialogBoxUI>(); // EvalulationBoxUI 也是 DialogBoxUI 子类
                    if (box == null) continue;
                    var go = box.gameObject;
                    var tmp = box.text;
                    var content = tmp == null ? "<无 TMP>" : $"「{Trunc(tmp.text)}」";
                    PluginContext.Log.LogInfo(
                        $"{Tag}   实例 路径={BuildPath(box.transform)} active={go.activeInHierarchy} 文本={content}");
                }
                catch (Exception ex)
                {
                    PluginContext.Log.LogWarning($"{Tag}   单实例 dump 失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"{Tag} 反射 dump {uiType.Name} 失败: {ex.Message}");
        }
    }

    private static string BuildPath(Transform t)
    {
        try
        {
            var parts = new List<string>();
            var cur = t;
            while (cur != null)
            {
                parts.Add(cur.name ?? "?");
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
        catch
        {
            return "<路径读取失败>";
        }
    }

    // ---- 参数格式化 ----

    private static string Name(MethodBase m) => $"{m.DeclaringType?.Name}.{m.Name}";

    private static string FmtInstance(object instance)
    {
        if (instance == null) return " [static]";
        return $" [{instance.GetType().Name}]";
    }

    private static string FmtArgs(object[]? args)
    {
        if (args == null || args.Length == 0) return string.Empty;
        var parts = new List<string>(args.Length);
        foreach (var a in args)
            parts.Add(FmtArg(a));
        return $" 参数({string.Join(", ", parts)})";
    }

    private static string FmtArg(object? arg)
    {
        try
        {
            switch (arg)
            {
                case null: return "null";
                case string s: return $"「{Trunc(s)}」";
                case Transform tr: return tr == null ? "Transform(null)" : $"Transform({tr.name})";
                case UnityEngine.Object uo: return uo == null ? $"{arg.GetType().Name}(null)" : $"{arg.GetType().Name}({uo.name})";
                default: return Trunc(arg.ToString());
            }
        }
        catch
        {
            return "<不可读>";
        }
    }

    private static string Trunc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "<空>";
        return s.Length <= 20 ? s : s.Substring(0, 20) + "…";
    }

    private static string Describe(MethodBase method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(p =>
            $"{(p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "")}{p.ParameterType.FullName} {p.Name}"));
        return $"{method.DeclaringType?.FullName}.{method.Name}({parameters})";
    }
}
