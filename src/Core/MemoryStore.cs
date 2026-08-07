using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx.Logging;
using MystiaAI.Config;

namespace MystiaAI.Core;

/// <summary>
/// 一条长期记忆：对话结束时从 transcript 截取的片段 + 当时的时间/场景。
/// </summary>
public sealed class MemoryEntry
{
    /// <summary>记忆文本（transcript 尾部实际说的内容，非 AI 总结）。</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>游戏内时间描述（如 "白天 10:30"），空串=未知。</summary>
    [JsonPropertyName("gameTime")]
    public string GameTime { get; set; } = string.Empty;

    /// <summary>场景名（ChatScene 枚举名：DayChat / NightChat / Evaluation）。</summary>
    [JsonPropertyName("scene")]
    public string Scene { get; set; } = string.Empty;

    /// <summary>写入时的真实时间（UTC），用于排序与调试。</summary>
    [JsonPropertyName("savedAtUtc")]
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 长期记忆存储：把每次对话结束时的 transcript 尾部存到 文档/MystiaAI/memories.json，
/// 下次同一角色对话时注入 prompt（见 PromptBuilder 的记忆段），让 NPC 记得过去聊过什么。
///
/// 设计取舍：
/// - 记忆文本 = 原文片段（transcript 尾部），不调 AI 摘要——零 API 成本、零延迟、无失败路径；
/// - 每角色保留最近 N 条（Settings.MemoryMaxPerCharacter），超出丢最旧；
/// - 与最新一条文本相同则跳过（防续聊多轮/重复结束回调重复记录）；
/// - 线程安全：全静态锁；文件写入用临时文件 + 替换（避免写一半损坏）。
/// </summary>
public static class MemoryStore
{
    /// <summary>memories.json 的完整路径（文档目录，与 settings.json 同位置）。</summary>
    public static readonly string StoreFile = Path.Combine(Settings.StoreDir, "memories.json");

    /// <summary>单条记忆文本的字符上限（transcript 尾部截取用）。</summary>
    private const int EntryTextLimit = 300;

    /// <summary>transcript 尾部最多取几行。</summary>
    private const int EntryLineLimit = 4;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    private static ManualLogSource? _log;
    private static Dictionary<string, List<MemoryEntry>> _characters = new();
    private static bool _loaded;

    /// <summary>初始化（PluginContext.Initialize 调用）。失败不抛；文件损坏时备份后重建，功能不静默失效。</summary>
    public static void Initialize(ManualLogSource? log)
    {
        _log = log;
        try
        {
            lock (Gate)
            {
                _characters = LoadLocked();
                _loaded = true;
            }
            log?.LogInfo($"[MystiaAI] 长期记忆已加载（{CountEntries()} 条，{_characters.Count} 个角色）");
        }
        catch (Exception ex)
        {
            // 损坏的 memories.json（断电写坏/手工改错格式）不能永久静默禁用记忆：
            // 备份坏文件为 memories.json.bak，以空表重建，下次记录时写出全新文件
            try
            {
                if (File.Exists(StoreFile))
                {
                    File.Copy(StoreFile, StoreFile + ".bak", overwrite: true);
                    File.Delete(StoreFile);
                }
                lock (Gate)
                {
                    _characters = new Dictionary<string, List<MemoryEntry>>();
                    _loaded = true;
                }
                log?.LogWarning(
                    $"[MystiaAI] 长期记忆文件损坏，已备份为 memories.json.bak 并以空表重建（原错误: {ex.Message}）");
            }
            catch (Exception ex2)
            {
                log?.LogWarning($"[MystiaAI] 长期记忆加载失败且备份失败（本次会话记忆不可用）: {ex.Message}；{ex2.Message}");
            }
        }
    }

    /// <summary>
    /// 记录一条记忆：从 transcript 尾部截取文本（最多 4 行 / 300 字），
    /// 与最新一条相同则跳过（去重）。配置关闭或文本为空时静默忽略。
    /// </summary>
    public static void Record(string characterKey, string transcript, string gameTime, string scene)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(characterKey)) return;
            if (!PluginContext.Settings.MemoryEnabled) return;

            var text = Tail(transcript);
            if (string.IsNullOrWhiteSpace(text)) return;

            lock (Gate)
            {
                if (!_loaded) return;
                if (!_characters.TryGetValue(characterKey, out var list))
                {
                    list = new List<MemoryEntry>();
                    _characters[characterKey] = list;
                }

                // 去重：与最新一条文本相同则跳过（续聊多轮结束时 transcript 尾部往往不变）
                if (list.Count > 0 && string.Equals(list[list.Count - 1].Text, text, StringComparison.Ordinal))
                {
                    _log?.LogInfo($"[MystiaAI] 记忆: 角色 {characterKey} 与上一条相同，跳过去重");
                    return;
                }

                list.Add(new MemoryEntry
                {
                    Text = text,
                    GameTime = gameTime ?? string.Empty,
                    Scene = scene ?? string.Empty,
                    SavedAtUtc = DateTime.UtcNow,
                });

                var max = Math.Clamp(PluginContext.Settings.MemoryMaxPerCharacter, 1, 200);
                while (list.Count > max)
                    list.RemoveAt(0); // 丢最旧

                try
                {
                    SaveLocked();
                    _log?.LogInfo($"[MystiaAI] 记忆: 角色 {characterKey} 已记录（现有 {list.Count} 条）");
                }
                catch (Exception ex)
                {
                    _log?.LogWarning($"[MystiaAI] 记忆写入失败（内存中保留，重启后丢失）: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[MystiaAI] MemoryStore.Record 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 取某角色最近 count 条记忆的格式化文本（供 prompt 注入）：
    /// 每行 "- 白天 10:30 [白天闲聊]：内容"。无记忆返回空串。
    /// </summary>
    public static string GetRecentText(string characterKey, int count)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(characterKey)) return string.Empty;
            if (!PluginContext.Settings.MemoryEnabled) return string.Empty;
            if (count <= 0) return string.Empty;

            lock (Gate)
            {
                if (!_loaded) return string.Empty;
                if (!_characters.TryGetValue(characterKey, out var list) || list.Count == 0)
                    return string.Empty;

                var lines = new List<string>();
                var start = Math.Max(0, list.Count - count);
                for (var i = start; i < list.Count; i++)
                {
                    var e = list[i];
                    if (string.IsNullOrWhiteSpace(e.Text)) continue;
                    var scene = SceneText(e.Scene);
                    var when = string.IsNullOrWhiteSpace(e.GameTime) ? "" : e.GameTime + " ";
                    lines.Add($"- {when}[{scene}]：{e.Text}");
                }
                return string.Join("\n", lines);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[MystiaAI] MemoryStore.GetRecentText 异常: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>统计总条数（仅日志用）。</summary>
    private static int CountEntries()
    {
        var n = 0;
        foreach (var list in _characters.Values) n += list.Count;
        return n;
    }

    /// <summary>场景枚举名 → 中文描述（prompt 用）。</summary>
    private static string SceneText(string scene)
    {
        switch (scene)
        {
            case "DayChat": return "白天闲聊";
            case "NightChat": return "夜晚闲聊";
            case "Evaluation": return "用餐评价";
            default: return string.IsNullOrWhiteSpace(scene) ? "对话" : scene;
        }
    }

    /// <summary>transcript 尾部截取：最多 EntryLineLimit 行、EntryTextLimit 字（从末尾向前收集）。</summary>
    private static string Tail(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return string.Empty;
        var trimmed = transcript.TrimEnd();
        var lines = trimmed.Split('\n');
        var parts = new List<string>();
        var len = 0;
        for (var i = lines.Length - 1; i >= 0 && parts.Count < EntryLineLimit; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            parts.Insert(0, line); // 保持原顺序
            len += line.Length;
            if (len > EntryTextLimit) break;
        }
        var text = string.Join("\n", parts);
        if (text.Length > EntryTextLimit)
            text = text.Substring(text.Length - EntryTextLimit); // 超限截尾部（保留最新内容）
        // 防孤立代理项：截取起点若落在 surrogate pair 中间（低代理项），丢弃它——
        // 孤立代理项会让 System.Text.Json 序列化抛异常，导致之后每次保存都静默失败
        if (text.Length > 0 && char.IsLowSurrogate(text[0]))
            text = text.Substring(1);
        return text;
    }

    // ---- 文件 IO（锁内调用）----

    private static Dictionary<string, List<MemoryEntry>> LoadLocked()
    {
        if (!File.Exists(StoreFile)) return new Dictionary<string, List<MemoryEntry>>();
        var json = File.ReadAllText(StoreFile);
        var dto = JsonSerializer.Deserialize<FileDto>(json);
        if (dto?.Characters == null) return new Dictionary<string, List<MemoryEntry>>();
        var result = new Dictionary<string, List<MemoryEntry>>();
        foreach (var kv in dto.Characters)
        {
            var list = kv.Value ?? new List<MemoryEntry>();
            // 防御：清掉空文本条目
            list.RemoveAll(e => e == null || string.IsNullOrWhiteSpace(e.Text));
            if (list.Count > 0) result[kv.Key] = list;
        }
        return result;
    }

    private static void SaveLocked()
    {
        Directory.CreateDirectory(Settings.StoreDir);
        var dto = new FileDto
        {
            Meta = new Dictionary<string, string>
            {
                ["tool"] = "MystiaAI",
                ["kind"] = "memories",
                ["version"] = "1",
            },
            Characters = _characters,
        };
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        // 原子写：临时文件 + 替换，避免写一半损坏
        var tmp = StoreFile + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, StoreFile, overwrite: true);
    }

    private sealed class FileDto
    {
        [JsonPropertyName("_meta")] public Dictionary<string, string>? Meta { get; set; }
        [JsonPropertyName("characters")] public Dictionary<string, List<MemoryEntry>>? Characters { get; set; }
    }
}
