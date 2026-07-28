using System;
using System.Collections.Generic;
using GameData.Core.Collections.CharacterUtility;
using GameData.CoreLanguage.Collections;

namespace MystiaAI.Core;

/// <summary>
/// 稀客 stringId → 本地化名对照表的一次性预建。
/// 数据源：DataBaseCharacter.GetAllSpecialGuests()/GetAllMappedGuests()（全量稀客与 DLC 映射变体，
/// 带 id 与 stringId）+ DataBaseLanguage.GetAllSpecialGuestsNames()（id → 当前语言名）。
/// 首次有机会（白天/夜晚对话触发）时一把建好整张表，免去逐条实时学习；
/// 按当前游戏语言生成，数据未就绪时下次调用重试，任何失败只记日志，绝不影响游戏。
/// </summary>
public static class SpecialGuestNames
{
    private static bool _built;
    private static bool _failureLogged;

    /// <summary>首次调用时预建全量别名表；成功后不再执行。数据未就绪则下次对话时重试。</summary>
    public static void EnsurePrebuilt(PersonaStore? store)
    {
        if (_built || store == null) return;
        try
        {
            var names = DataBaseLanguage.GetAllSpecialGuestsNames();
            if (names == null || names.Count == 0) return; // 启动早期数据未就绪是常态，静默等下次

            var map = new Dictionary<string, string>();
            var identities = new List<(int Id, string Label, string Name)>();
            foreach (var guest in DataBaseCharacter.GetAllSpecialGuests())
            {
                try
                {
                    if (guest == null) continue;
                    var sid = guest.stringId;
                    if (!string.IsNullOrWhiteSpace(sid)
                        && names.TryGetValue(guest.Id, out var name)
                        && !string.IsNullOrWhiteSpace(name))
                    {
                        map[sid.Trim()] = name.Trim();
                        identities.Add((guest.Id, sid.Trim(), name.Trim()));
                    }
                }
                catch { /* 单个稀客读取失败跳过，不影响整表 */ }
            }
            foreach (var mapped in DataBaseCharacter.GetAllMappedGuests())
            {
                try
                {
                    if (mapped == null) continue;
                    var sid = mapped.stringId;
                    if (string.IsNullOrWhiteSpace(sid) || map.ContainsKey(sid.Trim())) continue;
                    // 映射变体（如 DLC1_Marisa）在名字表里没有自己的条目，回退到源稀客的名字
                    if (names.TryGetValue(mapped.sourceGuestID, out var mappedName)
                        && !string.IsNullOrWhiteSpace(mappedName))
                    {
                        map[sid.Trim()] = mappedName.Trim();
                    }
                }
                catch { /* 同上 */ }
            }
            if (map.Count == 0) return; // 视为未就绪，下次重试

            store.LearnAliases(map);
            // 顺带把 label/id 回填进 personas.json 角色条目（只补缺，供网页工具展示与直接匹配）
            store.BackfillIdentities(identities);
            _built = true;
            PluginContext.Log.LogInfo($"[MystiaAI] 稀客别名表已一次性预建（{map.Count} 条，按当前语言）");
        }
        catch (Exception ex)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                PluginContext.Log.LogWarning($"[MystiaAI] 稀客别名表预建失败（将在下次对话时重试）: {ex.Message}");
            }
        }
    }
}
