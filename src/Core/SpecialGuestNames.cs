using System;
using GameData.CoreLanguage.Collections;
using Il2CppSystem.Collections.Generic;

namespace MystiaAI.Core;

/// <summary>
/// 稀客 id → 本地化名查询（DataBaseLanguage.GetAllSpecialGuestsNames 的懒加载缓存），
/// 并在查到后把 stringId → 中文名 写进 PersonaStore 的别名表。
/// 仅在主线程、稀客对象已就绪的 Patch 场景调用；任何失败都只记日志并永久停用，绝不影响游戏。
/// </summary>
public static class SpecialGuestNames
{
    private static Dictionary<int, string>? _cache;
    private static bool _disabled;

    /// <summary>稀客出现时调用：按 id 查本地化名，学习 stringId → 中文名 别名。</summary>
    public static void LearnAlias(PersonaStore? store, string? stringId, int id)
    {
        if (_disabled || store == null || string.IsNullOrWhiteSpace(stringId)) return;
        try
        {
            _cache ??= DataBaseLanguage.GetAllSpecialGuestsNames();
            if (_cache != null && _cache.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name))
                store.LearnAlias(stringId, name.Trim());
        }
        catch (Exception ex)
        {
            _disabled = true; // 数据未就绪/调用失败：停用学习，避免每次出现都刷异常
            PluginContext.Log.LogWarning($"[MystiaAI] 稀客名称查询失败，别名学习已停用: {ex.Message}");
        }
    }
}
