using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;
using BepInEx.Logging;
using MystiaAI.Config;

namespace MystiaAI.Core;

/// <summary>
/// NPC 人设存储（v2）。持久化在 文档目录/MystiaAI/personas.json（与 settings.json 同目录，
/// 不在游戏根目录——游戏装在 Program Files 时 Chrome 禁止网页工具访问），结构：
/// {
///   "_meta": { "tool": "MystiaAI", "kind": "personas", "version": "2" },
///   "categories": { "Default": "…", "DayNpc": "…", "NormalGuest": "…", "SpecialGuest": "…" },
///   "characters": { "露米娅": { "displayName": "露米娅", "category": "SpecialGuest", "persona": "…" } }
/// }
/// 角色条目以【中文名】为 key（与普客运行时 key 一致，也便于网页工具编辑）。
/// 稀客/白天 NPC 运行时拿到的是内部英文名（stringId，如 "Chen"），由 aliases.json
/// （stringId → 中文名）在查找时换算；别名表在首次对话时按游戏数据一次性预建补全。
/// 生效优先级：角色专属（精确 key → 别名换算）→ 分类 → Default → 内置默认。
/// 人设为空或仍是占位符「【待填写】」视为未配置，继续向上回退。
/// 内置默认（116 条全量人设）内嵌于 DLL 资源 personas.default.json；
/// 用户的 personas.json 缺条目时启动自动补齐并写回（不覆盖用户已有内容）。
/// 按文件修改时间热重载（节流 2 秒），网页工具保存后游戏内即时生效。
/// </summary>
public sealed class PersonaStore
{
    /// <summary>查不到对应角色时回退的通用模板 key。</summary>
    public const string DefaultKey = "Default";

    /// <summary>分类 key：白天路边居民。</summary>
    public const string CategoryDayNpc = "DayNpc";

    /// <summary>分类 key：营业时间普通客人（闲聊+评价）。</summary>
    public const string CategoryNormalGuest = "NormalGuest";

    /// <summary>分类 key：稀客（兜底，稀客一般都应有角色专属人设）。</summary>
    public const string CategorySpecialGuest = "SpecialGuest";

    /// <summary>未配置占位符：用户后续在网页工具里逐一替换为正式人设。</summary>
    public const string Placeholder = "【待填写】";

    private static readonly string StoreDir = Settings.StoreDir;
    private static readonly string StoreFile = Path.Combine(StoreDir, "personas.json");
    private static readonly string AliasStoreFile = Path.Combine(StoreDir, "aliases.json");

    /// <summary>v1 旧文件位置（BepInEx 配置目录），仅用于一次性迁移。</summary>
    private static readonly string LegacyFile = Path.Combine(Paths.ConfigPath, "MystiaAI", "personas.json");

    private const int FileVersion = 2;

    private static readonly TimeSpan ReloadThrottle = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 预置别名（stringId → 中文名）。早期版本内置的四个英文 key 角色 + 实测确认过的 Chen，
    /// 让老用户和常见角色在第一晚之前也能命中专属人设；其余别名运行时自动学习。
    /// </summary>
    private static readonly Dictionary<string, string> SeedAliases = new Dictionary<string, string>
    {
        ["Rumia"] = "露米娅",
        ["Wriggle"] = "莉格露",
        ["Cirno"] = "琪露诺",
        ["Suika"] = "伊吹萃香",
        ["Chen"] = "橙",
    };

    private readonly ManualLogSource? _log;
    private readonly object _gate = new object();
    private PersonaFile? _file;
    private DateTime _lastWriteUtc;
    private DateTime _nextCheckUtc = DateTime.MinValue;
    private Dictionary<string, string>? _aliases;

    public PersonaStore(ManualLogSource? log = null)
    {
        _log = log;
    }

    /// <summary>取角色人设文本（不带分类，按 Default 兜底）。</summary>
    public string GetPersona(string? characterKey) => GetPersona(characterKey, null);

    /// <summary>
    /// 取角色人设文本：角色专属（精确 key → 别名换算）→ 指定分类 → Default → 内置默认。
    /// 人设为空或是占位符视为未配置，继续向上回退。
    /// </summary>
    public string GetPersona(string? characterKey, string? category)
    {
        ReloadIfChanged();
        lock (_gate)
        {
            var file = EnsureLoaded();
            if (!string.IsNullOrWhiteSpace(characterKey))
            {
                if (file.Characters.TryGetValue(characterKey, out var entry) && IsUsable(entry.Persona))
                    return entry.Persona;
                // label（stringId）直接匹配：条目上配置的 stringId 与运行时 key 一致即命中
                foreach (var (_, e) in file.Characters)
                {
                    if (!string.IsNullOrEmpty(e.Label) && e.Label == characterKey && IsUsable(e.Persona))
                        return e.Persona;
                }
                // stringId → 中文名 别名换算（稀客/白天 NPC 路径）
                var cn = ResolveAlias(characterKey);
                if (cn != null
                    && file.Characters.TryGetValue(cn, out var aliasEntry)
                    && IsUsable(aliasEntry.Persona))
                {
                    return aliasEntry.Persona;
                }
            }
            if (!string.IsNullOrWhiteSpace(category)
                && file.Categories.TryGetValue(category, out var catPersona)
                && IsUsable(catPersona))
            {
                return catPersona;
            }
            if (file.Categories.TryGetValue(DefaultKey, out var def) && IsUsable(def))
                return def;
            return BuiltinDefaults().Categories[DefaultKey];
        }
    }

    /// <summary>人设是否可用（非空且不是占位符）。</summary>
    public static bool IsUsable(string? persona)
        => !string.IsNullOrWhiteSpace(persona) && !persona.TrimStart().StartsWith(Placeholder);

    /// <summary>
    /// 取羁绊等级语气提示词（{bondTone} 变量）：角色专属（精确 key → label 匹配 → 别名换算）
    /// → 全局兜底 → 空串。level 不在 1~5（普客/未收录为 0）直接返回空串，不回退分类。
    /// </summary>
    public string GetBondTone(string? characterKey, int level)
    {
        if (level < 1 || level > 5) return string.Empty;
        var lv = level.ToString();
        ReloadIfChanged();
        lock (_gate)
        {
            var file = EnsureLoaded();
            file.BondGlobal ??= new Dictionary<string, string>();

            PersonaEntry? entry = null;
            if (!string.IsNullOrWhiteSpace(characterKey))
            {
                if (file.Characters.TryGetValue(characterKey, out var e1)) entry = e1;
                else
                {
                    foreach (var (_, e) in file.Characters)
                    {
                        if (!string.IsNullOrEmpty(e.Label) && e.Label == characterKey) { entry = e; break; }
                    }
                    if (entry == null)
                    {
                        var cn = ResolveAlias(characterKey);
                        if (cn != null && file.Characters.TryGetValue(cn, out var e2)) entry = e2;
                    }
                }
            }

            if (entry?.BondPrompts != null
                && entry.BondPrompts.TryGetValue(lv, out var tone)
                && !string.IsNullOrWhiteSpace(tone))
            {
                return tone.Trim();
            }
            if (file.BondGlobal.TryGetValue(lv, out var globalTone) && !string.IsNullOrWhiteSpace(globalTone))
                return globalTone.Trim();
            return string.Empty;
        }
    }

    /* ================= 别名表（stringId → 中文名） ================= */

    /// <summary>
    /// 批量并入别名（SpecialGuestNames 一次性预建用）：只补缺、不覆盖已有条目
    /// （含用户手改与预置别名），有新增才写盘一次。
    /// </summary>
    public void LearnAliases(IReadOnlyDictionary<string, string> map)
    {
        if (map == null || map.Count == 0) return;
        lock (_gate)
        {
            var aliases = EnsureAliasesLoaded();
            var added = 0;
            foreach (var (k, v) in map)
            {
                if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(v)) continue;
                var key = k.Trim();
                var value = v.Trim();
                if (key == value || aliases.ContainsKey(key)) continue;
                aliases[key] = value;
                added++;
            }
            if (added == 0) return;
            try
            {
                Directory.CreateDirectory(StoreDir);
                File.WriteAllText(AliasStoreFile, JsonSerializer.Serialize(aliases, JsonOptions));
                _log?.LogInfo($"[MystiaAI] 别名表批量并入 {added} 条（总计 {aliases.Count} 条）");
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[MystiaAI] 别名写入失败（仅内存生效）: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 用游戏数据回填角色条目的 label（stringId）与 id：按「条目 key == 当前语言名」匹配，
    /// 只补缺不覆盖（用户手改的不动）；有改动写回一次。由 SpecialGuestNames 预建别名表后调用。
    /// </summary>
    public void BackfillIdentities(IReadOnlyList<(int Id, string Label, string Name)> identities)
    {
        if (identities == null || identities.Count == 0) return;
        lock (_gate)
        {
            var file = EnsureLoaded();
            var changed = 0;
            foreach (var (id, label, name) in identities)
            {
                if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(name)) continue;
                if (!file.Characters.TryGetValue(name.Trim(), out var entry)) continue;
                if (string.IsNullOrEmpty(entry.Label)) { entry.Label = label.Trim(); changed++; }
                if (!entry.Id.HasValue) { entry.Id = id; changed++; }
            }
            if (changed == 0) return;
            try
            {
                file.Dirty = false;
                File.WriteAllText(StoreFile, JsonSerializer.Serialize(file, JsonOptions));
                _lastWriteUtc = File.GetLastWriteTimeUtc(StoreFile);
                _log?.LogInfo($"[MystiaAI] personas.json 已回填 label/id（{changed} 处）");
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[MystiaAI] personas.json label/id 回填写盘失败（仅内存生效）: {ex.Message}");
            }
        }
    }

    /// <summary>把运行时 key（可能是 stringId）换算成中文名；查不到返回 null。</summary>
    public string? ResolveAlias(string key)
    {
        var aliases = EnsureAliasesLoaded();
        return aliases.TryGetValue(key, out var cn) ? cn : null;
    }

    private Dictionary<string, string> EnsureAliasesLoaded()
    {
        if (_aliases != null) return _aliases;
        ConfigMigration.EnsureMigrated(_log);
        var aliases = new Dictionary<string, string>();
        try
        {
            if (File.Exists(AliasStoreFile))
            {
                aliases = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(AliasStoreFile))
                          ?? new Dictionary<string, string>();
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[MystiaAI] 别名表读取失败（从空表开始）: {ex.Message}");
        }
        // 预置别名只补缺，不覆盖已学习/用户修改的
        var dirty = !File.Exists(AliasStoreFile);
        foreach (var (k, v) in SeedAliases)
        {
            if (!aliases.ContainsKey(k))
            {
                aliases[k] = v;
                dirty = true;
            }
        }
        _aliases = aliases;
        if (dirty)
        {
            try
            {
                Directory.CreateDirectory(StoreDir);
                File.WriteAllText(AliasStoreFile, JsonSerializer.Serialize(aliases, JsonOptions));
            }
            catch { /* 别名表写不进去就只用内存副本 */ }
        }
        return _aliases;
    }

    /* ================= 加载 / 热重载 ================= */

    /// <summary>文件被外部（网页工具/用户）修改后热重载；节流 2 秒。</summary>
    public void ReloadIfChanged()
    {
        var now = DateTime.UtcNow;
        if (now < _nextCheckUtc) return;
        _nextCheckUtc = now + ReloadThrottle;
        lock (_gate)
        {
            try
            {
                if (!File.Exists(StoreFile)) return;
                // 首次加载交给 EnsureLoaded（它会做迁移/补齐并写回），这里只处理运行中的外部修改
                if (_file == null) return;
                var writeUtc = File.GetLastWriteTimeUtc(StoreFile);
                if (writeUtc == _lastWriteUtc) return;
                var read = ReadFile(File.ReadAllText(StoreFile));
                _file = read;
                _lastWriteUtc = writeUtc;
                if (read.Dirty)
                {
                    // 外部文件缺条目（旧版文件/手动删过）→ 补齐后写回，让网页工具看到完整结构
                    read.Dirty = false;
                    File.WriteAllText(StoreFile, JsonSerializer.Serialize(read, JsonOptions));
                    _lastWriteUtc = File.GetLastWriteTimeUtc(StoreFile);
                    _log?.LogInfo("[MystiaAI] personas.json 已自动补齐内置条目并写回");
                }
                _log?.LogInfo("[MystiaAI] 检测到人设文件变更，已热重载 personas.json");
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[MystiaAI] 人设文件热重载失败（保持当前值）: {ex.Message}");
            }
        }
    }

    /// <summary>丢弃缓存，下次访问时重新读盘（保留给显式刷新场景）。</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _file = null;
        }
    }

    private PersonaFile EnsureLoaded()
    {
        if (_file != null) return _file;
        try
        {
            ConfigMigration.EnsureMigrated(_log);
            if (!File.Exists(StoreFile))
            {
                // 一次性迁移 v1 旧文件（扁平字典），没有则写内置默认
                _file = TryMigrateLegacy() ?? BuiltinDefaults();
                Directory.CreateDirectory(StoreDir);
                File.WriteAllText(StoreFile, JsonSerializer.Serialize(_file, JsonOptions));
                _log?.LogInfo($"[MystiaAI] 已写入人设文件：{StoreFile}");
                _lastWriteUtc = File.GetLastWriteTimeUtc(StoreFile);
                return _file;
            }
            var upgraded = ReadFile(File.ReadAllText(StoreFile));
            _file = upgraded;
            _lastWriteUtc = File.GetLastWriteTimeUtc(StoreFile);
            if (upgraded.Dirty)
            {
                // 迁移/补齐过内容 → 写回，让网页工具能看到完整结构
                upgraded.Dirty = false;
                File.WriteAllText(StoreFile, JsonSerializer.Serialize(upgraded, JsonOptions));
                _lastWriteUtc = File.GetLastWriteTimeUtc(StoreFile);
                _log?.LogInfo("[MystiaAI] personas.json 已自动升级/补齐内置条目并写回");
            }
            return _file;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[MystiaAI] 人设文件读取失败，本次使用内置默认人设：{ex.Message}");
            _file = BuiltinDefaults();
            return _file;
        }
    }

    /// <summary>
    /// 解析 personas.json；识别 v1（扁平字典）并自动升级结构；
    /// 旧英文 key（Rumia 等）按预置别名迁移为中文名 key（保留用户改过的文案）；
    /// 缺的分类/角色条目补内置值（不覆盖用户已有的），有改动时标记 Dirty 让调用方写回。
    /// </summary>
    private PersonaFile ReadFile(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("personas.json 根节点不是对象");

        PersonaFile file;
        if (!root.TryGetProperty("_meta", out _))
        {
            // v1 扁平字典：{ "Rumia": "…", "Default": "…" } → v2
            _log?.LogInfo("[MystiaAI] 检测到 v1 扁平人设文件，已升级为 v2 结构");
            var flat = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>();
            file = BuiltinDefaults();
            file.Characters.Clear(); // v1 自带条目走下方迁移，避免被内置值抢先占位
            foreach (var (key, persona) in flat)
            {
                if (key == DefaultKey)
                    file.Categories[DefaultKey] = persona;
                else
                    file.Characters[key] = new PersonaEntry
                    {
                        DisplayName = key,
                        Category = CategorySpecialGuest,
                        Persona = persona,
                    };
            }
            file.Dirty = true;
        }
        else
        {
            file = JsonSerializer.Deserialize<PersonaFile>(json)
                   ?? throw new FormatException("personas.json 不是有效的 v2 结构");
            file.Categories ??= new Dictionary<string, string>();
            file.Characters ??= new Dictionary<string, PersonaEntry>();
            file.BondGlobal ??= new Dictionary<string, string>();
        }

        // 旧英文 key → 中文名 key 迁移（预置别名覆盖的角色）
        foreach (var (engKey, cnName) in SeedAliases)
        {
            if (!file.Characters.TryGetValue(engKey, out var legacy)) continue;
            if (!file.Characters.ContainsKey(cnName))
            {
                legacy.DisplayName = cnName;
                file.Characters[cnName] = legacy;
                _log?.LogInfo($"[MystiaAI] 人设条目已迁移：{engKey} → {cnName}");
            }
            file.Characters.Remove(engKey);
            file.Dirty = true;
        }

        // 补缺的分类与内置角色条目（不覆盖用户已有内容）
        var builtin = BuiltinDefaults();
        foreach (var (key, persona) in builtin.Categories)
        {
            if (!file.Categories.ContainsKey(key))
            {
                file.Categories[key] = persona;
                file.Dirty = true;
            }
        }
        var added = 0;
        foreach (var (key, entry) in builtin.Characters)
        {
            if (!file.Characters.ContainsKey(key))
            {
                file.Characters[key] = entry;
                added++;
            }
        }
        if (added > 0)
        {
            file.Dirty = true;
            _log?.LogInfo($"[MystiaAI] 已补齐 {added} 条内置角色人设");
        }
        return file;
    }

    /// <summary>迁移 BepInEx 配置目录下的 v1 旧文件；不存在或失败返回 null。</summary>
    private PersonaFile? TryMigrateLegacy()
    {
        try
        {
            if (!File.Exists(LegacyFile)) return null;
            var migrated = ReadFile(File.ReadAllText(LegacyFile));
            _log?.LogInfo($"[MystiaAI] 已从旧位置迁移人设文件：{LegacyFile} → {StoreFile}（旧文件不删除）");
            return migrated;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[MystiaAI] 旧人设文件迁移失败（使用内置默认）: {ex.Message}");
            return null;
        }
    }

    /* ================= 内置默认（嵌入资源） ================= */

    private static string? _builtinJson;

    /// <summary>内置默认 JSON：DLL 嵌入资源（116 条全量人设）；资源缺失时退化为最小内置集。</summary>
    private static string BuiltinJson()
    {
        if (_builtinJson != null) return _builtinJson;
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("MystiaAI.src.Assets.personas.default.json");
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                _builtinJson = reader.ReadToEnd();
                return _builtinJson;
            }
        }
        catch { /* 落到最小内置集 */ }
        _builtinJson = MinimalBuiltinJson;
        return _builtinJson;
    }

    /// <summary>内置默认：每次反序列化出新对象（调用方会往里合并用户数据）。</summary>
    private static PersonaFile BuiltinDefaults()
    {
        var file = JsonSerializer.Deserialize<PersonaFile>(BuiltinJson());
        if (file == null) throw new FormatException("内置 personas.default.json 解析失败");
        file.Categories ??= new Dictionary<string, string>();
        file.Characters ??= new Dictionary<string, PersonaEntry>();
        file.BondGlobal ??= new Dictionary<string, string>();
        return file;
    }

    /// <summary>嵌入资源缺失时的兜底（仅分类兜底文案，保证 mod 永远可用）。</summary>
    private const string MinimalBuiltinJson = """
        {
          "_meta": { "tool": "MystiaAI", "kind": "personas", "version": "2" },
          "categories": {
            "Default": "幻想乡的普通居民，米斯蒂娅的夜雀食堂常客。性格随和友善，说话随意家常。",
            "DayNpc": "幻想乡乡道上的普通居民，白天在外闲逛。性格随和，说话家常，会聊聊天气和日常琐事。",
            "NormalGuest": "光顾米斯蒂娅的夜雀食堂的普通客人，结束一天的生活后来小酌放松。随和健谈，会对菜品和环境随口评价几句。",
            "SpecialGuest": "幻想乡的知名角色，米斯蒂娅的夜雀食堂稀客。言行符合其原作性格。"
          },
          "characters": {}
        }
        """;

    private sealed class PersonaFile
    {
        [JsonPropertyName("_meta")] public Dictionary<string, string>? Meta { get; set; }
        [JsonPropertyName("categories")] public Dictionary<string, string> Categories { get; set; } = new();
        [JsonPropertyName("characters")] public Dictionary<string, PersonaEntry> Characters { get; set; } = new();

        /// <summary>全局兜底羁绊提示词（key 为 "1"~"5"）：角色无专属提示词时回退到此。</summary>
        [JsonPropertyName("bondGlobal")] public Dictionary<string, string> BondGlobal { get; set; } = new();

        /// <summary>加载过程中发生过迁移/补齐（不序列化，仅内存标记，提示调用方写回）。</summary>
        [JsonIgnore] public bool Dirty { get; set; }
    }

    private sealed class PersonaEntry
    {
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
        [JsonPropertyName("category")] public string Category { get; set; } = CategorySpecialGuest;
        [JsonPropertyName("persona")] public string Persona { get; set; } = string.Empty;

        /// <summary>内部名（stringId，如 "Wriggle"）。运行时 key 为 stringId 时直接按此匹配，无需走别名表。</summary>
        [JsonPropertyName("label")] public string? Label { get; set; }

        /// <summary>游戏内数字 id（稀客 id）。仅作展示/参考，查找不使用。</summary>
        [JsonPropertyName("id")] public int? Id { get; set; }

        /// <summary>羁绊 1~5 级语气提示词（key 为 "1"~"5"）。可选，空/缺失=无专属提示词。</summary>
        [JsonPropertyName("bondPrompts")] public Dictionary<string, string>? BondPrompts { get; set; }
    }
}
