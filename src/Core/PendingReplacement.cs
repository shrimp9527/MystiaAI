using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GameData.Profile;

namespace MystiaAI.Core;

/// <summary>一段对话的元数据（登记时确定，无需等面板打开）。</summary>
public sealed class DialogSegment
{
    /// <summary>DialogMeta.dialogId。</summary>
    public int DialogId { get; init; }

    /// <summary>说话人是否为主角米斯蒂娅（SpeakerIdentity.Identity.Self）。Self 段一律保持原文。</summary>
    public bool IsSelf { get; init; }
}

/// <summary>
/// 一条「待替换」记录：某个 DialogPackage 的对话元数据。
/// 由取数点（如 DayChatPatch）登记——此时只有元数据（各段 dialogId 与说话人），
/// 不发起生成任务：连贯改写需要整段原文，而原文（textFile）要等面板打开才有。
/// 生成任务由显示点（DialogPannelPatch.OnExecutingDialogLoopCore）为每个非 Self 段发起。
/// </summary>
public sealed class PendingReplacement
{
    /// <summary>角色 string label（如 "Wriggle"），仅用于日志与调试，也用作 transcript 里 NPC 行的说话人标签。</summary>
    public string CharacterKey { get; init; } = string.Empty;

    /// <summary>按 dialogMeta 顺序的各段元数据。</summary>
    public IReadOnlyList<DialogSegment> Segments { get; init; } = new List<DialogSegment>();

    /// <summary>
    /// 同步语义的替换任务表（兼容保留）：仅 OpenDialogMenuPatch 的旧路径消费。
    /// 白天闲聊的方案 B 异步流程不使用此表（生成任务挂在 DialogPannelPatch 的面板实例上），保持为空即可。
    /// </summary>
    public IReadOnlyDictionary<int, Task<string>> Texts { get; init; } = new Dictionary<int, Task<string>>();

    /// <summary>
    /// 原文记录表：key = DialogMeta.dialogId，value = 游戏原文（生成替换时的原文）。
    /// 供下游「按原文匹配」的替换点使用。无原文记录时为空字典。
    /// </summary>
    public IReadOnlyDictionary<int, string> OriginalTexts { get; init; } = new Dictionary<int, string>();
}

/// <summary>
/// 全局待替换表。匹配键为 DialogPackage 的包名（如 "Yokai_Elder_1-B"）：
/// 实测发现游戏在显示阶段未必使用取数时的同一个 native 实例
/// （可能按包名重新取包），而包名稳定且唯一；包名取不到时退化为 native 指针。
/// 条目在回调执行时一次性消费移除，防止泄漏。
/// </summary>
public static class PendingReplacementStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PendingReplacement> Pending = new();

    /// <summary>已注入回调的包，防止同一包在多个层级（OpenDialogMenu / OpenContext 构造）重复链式叠加。</summary>
    private static readonly HashSet<string> Injected = new();

    /// <summary>取匹配键：包名优先，native 指针兜底。供 Patch 层诊断日志使用。</summary>
    public static string KeyOf(DialogPackage package)
    {
        if (package == null) return "<null>";
        string? name = null;
        try { name = package.name; } catch { /* 已销毁对象访问 name 可能抛异常 */ }
        return string.IsNullOrEmpty(name) ? "#ptr:" + package.Pointer.ToString("X") : name!;
    }

    public static void Register(DialogPackage package, PendingReplacement replacement)
    {
        if (package == null || replacement == null) return;
        var key = KeyOf(package);
        lock (Gate)
        {
            Pending[key] = replacement;
            Injected.Remove(key); // 新一轮生成，重置注入标记
        }
    }

    /// <summary>只查不取，供显示点判断是否需要注入回调。</summary>
    public static bool Contains(DialogPackage package)
    {
        if (package == null) return false;
        lock (Gate)
            return Pending.ContainsKey(KeyOf(package));
    }

    /// <summary>按匹配键只读查看（不消费），供 OpenDialogMenuPatch 注入的回调读取任务表。</summary>
    public static PendingReplacement? Peek(string key)
    {
        lock (Gate)
            return Pending.TryGetValue(key, out var replacement) ? replacement : null;
    }

    /// <summary>标记该包已注入回调；返回 false 表示此前已注入过（不要重复链式叠加）。</summary>
    public static bool TryMarkInjected(DialogPackage package)
    {
        if (package == null) return false;
        lock (Gate)
            return Injected.Add(KeyOf(package));
    }

    /// <summary>取出并移除（一次性消费）。无记录时返回 null。</summary>
    public static PendingReplacement? Consume(DialogPackage package)
    {
        if (package == null) return null;
        var key = KeyOf(package);
        lock (Gate)
        {
            Injected.Remove(key);
            if (Pending.TryGetValue(key, out var replacement))
            {
                Pending.Remove(key);
                return replacement;
            }
            return null;
        }
    }
}
