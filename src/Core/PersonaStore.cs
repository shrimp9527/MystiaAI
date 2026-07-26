using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;

namespace MystiaAI.Core;

/// <summary>
/// NPC 人设存储。持久化在 BepInEx 配置目录下的 MystiaAI/personas.json，
/// 格式为 { "Rumia": "露米娅，食人妖怪……", ... }，key 是角色内部名
/// （characterKey，如 Rumia / Wriggle / Cirno），"Default" 为通用路人模板。
/// 加载后常驻内存（线程安全）；运行中不监视文件变更，
/// 后续网页配置 GUI 修改文件后调用 <see cref="Reload"/> 触发热重载。
/// </summary>
public sealed class PersonaStore
{
    /// <summary>查不到对应角色时回退的通用模板 key。</summary>
    public const string DefaultKey = "Default";

    private static readonly string StoreDir = Path.Combine(Paths.ConfigPath, "MystiaAI");
    private static readonly string StoreFile = Path.Combine(StoreDir, "personas.json");

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    private readonly ManualLogSource? _log;
    private readonly object _gate = new object();
    private Dictionary<string, string>? _personas;

    public PersonaStore(ManualLogSource? log = null)
    {
        _log = log;
    }

    /// <summary>取角色人设文本；查不到该角色（或 key 为空）时返回 Default 模板。</summary>
    public string GetPersona(string? characterKey)
    {
        var map = EnsureLoaded();
        if (!string.IsNullOrWhiteSpace(characterKey)
            && map.TryGetValue(characterKey, out var persona)
            && !string.IsNullOrWhiteSpace(persona))
        {
            return persona;
        }
        return map[DefaultKey];
    }

    /// <summary>丢弃缓存，下次访问时重新读盘。供网页 GUI 保存人设后触发热重载。</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _personas = null;
        }
    }

    private Dictionary<string, string> EnsureLoaded()
    {
        lock (_gate)
        {
            if (_personas == null)
                _personas = LoadOrCreate();
            return _personas;
        }
    }

    private Dictionary<string, string> LoadOrCreate()
    {
        try
        {
            if (!File.Exists(StoreFile))
            {
                var defaults = DefaultPersonas();
                Directory.CreateDirectory(StoreDir);
                File.WriteAllText(StoreFile, JsonSerializer.Serialize(defaults, JsonOptions));
                _log?.LogInfo($"[MystiaAI] 已写入默认人设文件：{StoreFile}");
                return defaults;
            }

            var json = File.ReadAllText(StoreFile);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (map == null || map.Count == 0 || !map.ContainsKey(DefaultKey))
            {
                // 不覆盖用户文件，仅在内存中回退默认人设
                _log?.LogWarning($"[MystiaAI] 人设文件为空或缺少 \"{DefaultKey}\"，本次使用内置默认人设：{StoreFile}");
                return DefaultPersonas();
            }
            return map;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[MystiaAI] 人设文件读取失败，本次使用内置默认人设：{ex.Message}");
            return DefaultPersonas();
        }
    }

    private static Dictionary<string, string> DefaultPersonas()
    {
        return new Dictionary<string, string>
        {
            ["Rumia"] = "露米娅，食人妖怪（虽然是妖怪却没什么威严）。天真烂漫、粗线条、记性不太好，偶尔会问一些奇怪的问题。惊讶时偶尔会说「是这样吗——？」，偶尔也会问能不能咬一口尝尝。",
            ["Wriggle"] = "莉格露·奈特巴格，萤火虫妖怪，自称虫子们的领袖。元气爱逞强，非常在意别人觉得虫子恶心，一被夸奖就立刻得意忘形。",
            ["Cirno"] = "琪露诺，冰之妖精，自称「幻想乡最强」。自信满满但其实有点笨（尤其算术），爱恶作剧、爱向别人挑战。得意时偶尔会自称「本小姐是最强的！」。",
            ["Suika"] = "伊吹萃香，鬼族少女，整天醉醺醺地抱着酒葫芦。豪爽直率、喜欢热闹和宴会，说话带着醉意，偶尔会把话题聊到酒上。",
            [DefaultKey] = "幻想乡的普通居民，米斯蒂娅居酒屋的常客。性格随和友善，说话随意家常。",
        };
    }
}
