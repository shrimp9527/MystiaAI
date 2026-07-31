using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using MystiaAI.Config;

namespace MystiaAI.Core;

/// <summary>
/// 全局服务定位。集中持有配置、日志、AI 客户端，
/// Patch 层通过它访问一切服务，便于替换实现与单元测试。
/// </summary>
public static class PluginContext
{
    private static Settings _settings = null!;

    /// <summary>配置（每次访问按文件修改时间热重载，节流 2 秒，网页工具保存后即时生效）。</summary>
    public static Settings Settings
    {
        get
        {
            _settings.ReloadIfChanged();
            return _settings;
        }
    }

    public static IAiClient AiClient { get; private set; } = null!;
    public static PersonaStore Personas { get; private set; } = null!;
    public static ManualLogSource Log { get; private set; } = null!;

    /// <summary>当前是否处于「假 AI」开发模式（ApiKey 为空时自动成立，也可手动置回 true 强制假 AI）。</summary>
    public static bool UseFakeAi { get; set; } = true;

    public static void Initialize(ConfigFile config, ManualLogSource log)
    {
        Log = log;
        _settings = Settings.LoadOrCreate(config, log);
        Personas = new PersonaStore(log);

        // ApiKey 为空说明用户还没配置真实供应商，退回假 AI 保证替换通道可联调
        UseFakeAi = string.IsNullOrWhiteSpace(Settings.ApiKey);
        if (UseFakeAi)
        {
            AiClient = new FakeAiClient();
            Log.LogInfo("[MystiaAI] 未配置 ApiKey，使用假 AI（测试文本）。");
            return;
        }

        var (baseUrl, model) = ResolveEndpoint(Settings);
        AiClient = new OpenAiCompatibleClient(
            baseUrl, Settings.ApiKey, model, Settings.TimeoutSeconds, Personas);
        Log.LogInfo($"[MystiaAI] AI 供应商：{Settings.Provider}，BaseUrl={baseUrl}，Model={model}");
    }

    /// <summary>
    /// 决定实际使用的 BaseUrl/Model：用户没改过的项（仍是全局默认值）按 Provider 套用预设，
    /// 改过的项尊重用户配置；Custom 一律用配置原值。
    /// </summary>
    private static (string BaseUrl, string Model) ResolveEndpoint(Settings settings)
    {
        var baseUrl = settings.BaseUrl;
        var model = settings.Model;

        if (!string.Equals(settings.Provider, "Custom", StringComparison.OrdinalIgnoreCase)
            && ProviderPresets.All.TryGetValue(settings.Provider, out var preset))
        {
            if (baseUrl == Settings.DefaultBaseUrl)
                baseUrl = preset.BaseUrl;
            if (model == Settings.DefaultModel)
                model = preset.DefaultModel;
        }

        return (baseUrl, model);
    }
}

/// <summary>
/// 各供应商的 OpenAI 兼容接入点预设（Provider 名 → BaseUrl / 默认模型）。
/// 网页配置 GUI 复用此表填充下拉选项，新增供应商只需在此加一行。
/// </summary>
public static class ProviderPresets
{
    public static readonly IReadOnlyDictionary<string, (string BaseUrl, string DefaultModel)> All =
        new Dictionary<string, (string BaseUrl, string DefaultModel)>(StringComparer.OrdinalIgnoreCase)
        {
            ["DeepSeek"] = ("https://api.deepseek.com/v1", "deepseek-v4-flash"),
            ["OpenAI"] = ("https://api.openai.com/v1", "gpt-4o-mini"),
            ["GLM"] = ("https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
            ["Moonshot"] = ("https://api.moonshot.cn/v1", "moonshot-v1-8k"),
            ["Claude"] = ("https://api.anthropic.com/v1", "claude-sonnet-4-5"),
            // Custom：无预设，用配置文件原值（本地 Ollama 等）
        };
}
