using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace MystiaAI.Config;

/// <summary>
/// 全部可调参数的单一入口。持久化后端是独立 JSON：文档目录/MystiaAI/settings.json——
/// 网页配置工具与用户手动编辑的都是这同一份文件，不再使用 BepInEx cfg。
/// 首次启动时若存在旧 cfg（cc.mystia.ai.cfg）则一次性迁移其值后弃用（cfg 文件不删除）。
/// 访问前按文件修改时间热重载（节流 2 秒），网页保存后游戏内即时生效。
/// </summary>
public sealed class Settings
{
    /// <summary>BaseUrl 全局默认值。PluginContext 用它判断用户是否改过该配置（未改则按 Provider 套预设）。</summary>
    public const string DefaultBaseUrl = "https://api.deepseek.com/v1";

    /// <summary>Model 全局默认值，用途同 <see cref="DefaultBaseUrl"/>。与 DeepSeek 当前支持的模型名保持一致。</summary>
    public const string DefaultModel = "deepseek-v4-flash";

    /// <summary>
    /// 配置文件夹（文档目录下的 MystiaAI 文件夹）。
    /// 不能放游戏根目录：游戏装在 Program Files 时 Chrome 禁止网页工具访问该位置。
    /// </summary>
    public static readonly string StoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MystiaAI");

    public static readonly string StoreFile = Path.Combine(StoreDir, "settings.json");

    private const int FileVersion = 1;

    /// <summary>热重载节流间隔（夜晚气泡轮询会高频读配置，不能每次都 stat 文件）。</summary>
    private static readonly TimeSpan ReloadThrottle = TimeSpan.FromSeconds(2);

    // ---- 可配置项（与 settings.json 字段一一对应，新增配置两边同步加）----
    public bool Enabled { get; set; } = true;
    public bool NormalGuestAiEnabled { get; set; } = true;
    public string Provider { get; set; } = "DeepSeek";
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string Model { get; set; } = DefaultModel;
    public int MaxLength { get; set; } = 50;
    public bool Streaming { get; set; } = false;
    public float TimeoutSeconds { get; set; } = 10f;

    /// <summary>报纸话题注入概率（百分比 0-100）：AI 闲聊时把当日《文文新闻》塞进提示词的概率，0=完全不提，100=每次都提。</summary>
    public int NewsFrequency { get; set; } = 30;

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    private readonly ManualLogSource? _log;
    private DateTime _lastWriteUtc;
    private DateTime _nextCheckUtc = DateTime.MinValue;

    private Settings(ManualLogSource? log)
    {
        _log = log;
    }

    /// <summary>加载（或创建）配置文件；legacyCfg 存在且 settings.json 不存在时做一次性迁移。</summary>
    public static Settings LoadOrCreate(ConfigFile legacyCfg, ManualLogSource? log)
    {
        var s = new Settings(log);
        try
        {
            ConfigMigration.EnsureMigrated(log);
            if (!File.Exists(StoreFile))
            {
                s.ImportLegacyCfg(legacyCfg);
                s.Save();
                log?.LogInfo($"[MystiaAI] 已创建独立配置文件：{StoreFile}（旧 BepInEx cfg 已迁移并弃用）");
                s._lastWriteUtc = File.GetLastWriteTimeUtc(StoreFile);
                return s;
            }
            s.ReadFrom(File.ReadAllText(StoreFile));
            s._lastWriteUtc = File.GetLastWriteTimeUtc(StoreFile);
        }
        catch (Exception ex)
        {
            log?.LogWarning($"[MystiaAI] 配置文件读取失败，本次使用默认值：{ex.Message}");
        }
        return s;
    }

    /// <summary>文件被外部（网页工具/用户）修改后热重载；节流 2 秒，失败保持当前值只记日志。</summary>
    public void ReloadIfChanged()
    {
        var now = DateTime.UtcNow;
        if (now < _nextCheckUtc) return;
        _nextCheckUtc = now + ReloadThrottle;
        try
        {
            if (!File.Exists(StoreFile)) return;
            var writeUtc = File.GetLastWriteTimeUtc(StoreFile);
            if (writeUtc == _lastWriteUtc) return;
            ReadFrom(File.ReadAllText(StoreFile));
            _lastWriteUtc = writeUtc;
            _log?.LogInfo("[MystiaAI] 检测到配置文件变更，已热重载 settings.json");
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[MystiaAI] 配置文件热重载失败（保持当前值）: {ex.Message}");
        }
    }

    /// <summary>把当前值写回 settings.json（迁移/创建时用；游戏运行中不写配置，避免与网页工具互相覆盖）。</summary>
    public void Save()
    {
        Directory.CreateDirectory(StoreDir);
        File.WriteAllText(StoreFile, JsonSerializer.Serialize(ToDto(), JsonOptions));
        if (File.Exists(StoreFile))
            _lastWriteUtc = File.GetLastWriteTimeUtc(StoreFile);
    }

    // ---- 序列化：snake_case 字段 + _meta 标识（网页工具靠 _meta 识别文件）----

    private FileDto ToDto() => new FileDto
    {
        Meta = new Dictionary<string, string>
        {
            ["tool"] = "MystiaAI",
            ["kind"] = "settings",
            ["version"] = FileVersion.ToString(),
        },
        Enabled = Enabled,
        NormalGuestAiEnabled = NormalGuestAiEnabled,
        Provider = Provider,
        ApiKey = ApiKey,
        BaseUrl = BaseUrl,
        Model = Model,
        MaxLength = MaxLength,
        Streaming = Streaming,
        TimeoutSeconds = TimeoutSeconds,
        NewsFrequency = NewsFrequency,
    };

    private void ReadFrom(string json)
    {
        var dto = JsonSerializer.Deserialize<FileDto>(json);
        if (dto == null) throw new FormatException("settings.json 不是有效的 JSON");
        if (dto.Enabled.HasValue) Enabled = dto.Enabled.Value;
        if (dto.NormalGuestAiEnabled.HasValue) NormalGuestAiEnabled = dto.NormalGuestAiEnabled.Value;
        if (dto.Provider != null) Provider = dto.Provider;
        if (dto.ApiKey != null) ApiKey = dto.ApiKey;
        if (dto.BaseUrl != null) BaseUrl = dto.BaseUrl;
        if (dto.Model != null) Model = dto.Model;
        if (dto.MaxLength.HasValue) MaxLength = dto.MaxLength.Value;
        if (dto.Streaming.HasValue) Streaming = dto.Streaming.Value;
        if (dto.TimeoutSeconds.HasValue) TimeoutSeconds = dto.TimeoutSeconds.Value;
        if (dto.NewsFrequency.HasValue) NewsFrequency = Math.Clamp(dto.NewsFrequency.Value, 0, 100);
    }

    /// <summary>从旧 BepInEx cfg 读取同名字段（只读不写；文件不存在时 Bind 会给默认值，正好当出厂值）。</summary>
    private void ImportLegacyCfg(ConfigFile cfg)
    {
        Enabled = cfg.Bind("General", "Enabled", true).Value;
        NormalGuestAiEnabled = cfg.Bind("General", "NormalGuestAiEnabled", true).Value;
        Provider = cfg.Bind("AI", "Provider", "DeepSeek").Value;
        ApiKey = cfg.Bind("AI", "ApiKey", "").Value;
        BaseUrl = cfg.Bind("AI", "BaseUrl", DefaultBaseUrl).Value;
        Model = cfg.Bind("AI", "Model", DefaultModel).Value;
        MaxLength = cfg.Bind("AI", "MaxLength", 50).Value;
        Streaming = cfg.Bind("AI", "Streaming", false).Value;
        TimeoutSeconds = cfg.Bind("AI", "TimeoutSeconds", 10f).Value;
        NewsFrequency = cfg.Bind("AI", "NewsFrequency", 30).Value;
        // WebPort 已废弃（网页改为纯静态页，不再有本地服务），不迁移
    }

    private sealed class FileDto
    {
        [JsonPropertyName("_meta")] public Dictionary<string, string>? Meta { get; set; }
        [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
        [JsonPropertyName("normalGuestAiEnabled")] public bool? NormalGuestAiEnabled { get; set; }
        [JsonPropertyName("provider")] public string? Provider { get; set; }
        [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }
        [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("maxLength")] public int? MaxLength { get; set; }
        [JsonPropertyName("streaming")] public bool? Streaming { get; set; }
        [JsonPropertyName("timeoutSeconds")] public float? TimeoutSeconds { get; set; }
        [JsonPropertyName("newsFrequency")] public int? NewsFrequency { get; set; }
    }
}

/// <summary>
/// 配置目录迁移：旧位置是 游戏根目录/MystiaAI，但游戏装在 Program Files 时
/// Chrome 的目录选择器禁止访问该位置（网页工具无法读写），故整体迁到文档目录。
/// 旧文件一次性复制到新位置（不覆盖新文件、不删除旧文件），之后只用新位置。
/// </summary>
internal static class ConfigMigration
{
    private static readonly string LegacyStoreDir = Path.Combine(Paths.GameRootPath, "MystiaAI");
    private static bool _done;

    public static void EnsureMigrated(ManualLogSource? log = null)
    {
        if (_done) return;
        _done = true;
        try
        {
            if (!Directory.Exists(LegacyStoreDir)) return;
            foreach (var name in new[] { "settings.json", "personas.json", "aliases.json" })
            {
                var src = Path.Combine(LegacyStoreDir, name);
                var dst = Path.Combine(Settings.StoreDir, name);
                if (!File.Exists(src) || File.Exists(dst)) continue;
                Directory.CreateDirectory(Settings.StoreDir);
                File.Copy(src, dst);
                log?.LogInfo($"[MystiaAI] 配置已迁移到文档目录：{name}");
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning($"[MystiaAI] 配置目录迁移失败（使用文档目录默认值）: {ex.Message}");
        }
    }
}
