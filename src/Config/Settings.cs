using BepInEx.Configuration;

namespace MystiaAI.Config;

/// <summary>
/// 全部可调参数的单一入口。BepInEx 配置文件是持久化后端；
/// 后续网页配置 GUI 读写同一份 Settings，不引入第二套配置。
/// </summary>
public sealed class Settings
{
    /// <summary>BaseUrl 全局默认值。PluginContext 用它判断用户是否改过该配置（未改则按 Provider 套预设）。</summary>
    public const string DefaultBaseUrl = "https://api.deepseek.com/v1";

    /// <summary>Model 全局默认值，用途同 <see cref="DefaultBaseUrl"/>。与 DeepSeek 当前支持的模型名保持一致。</summary>
    public const string DefaultModel = "deepseek-v4-flash";

    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<string> Provider { get; }
    public ConfigEntry<string> ApiKey { get; }
    public ConfigEntry<string> BaseUrl { get; }
    public ConfigEntry<string> Model { get; }
    public ConfigEntry<int> MaxLength { get; }
    public ConfigEntry<bool> Streaming { get; }
    public ConfigEntry<int> WebPort { get; }
    public ConfigEntry<float> TimeoutSeconds { get; }

    public Settings(ConfigFile config)
    {
        Enabled = config.Bind("General", "Enabled", true, "总开关：是否启用 AI 文本替换");
        Provider = config.Bind("AI", "Provider", "DeepSeek", "AI 供应商：OpenAI / DeepSeek / GLM / Moonshot / Claude / Custom（本地）");
        ApiKey = config.Bind("AI", "ApiKey", "", "API Key");
        BaseUrl = config.Bind("AI", "BaseUrl", DefaultBaseUrl, "API 地址（本地模型填 Ollama 等地址）");
        Model = config.Bind("AI", "Model", DefaultModel, "模型名");
        MaxLength = config.Bind("AI", "MaxLength", 50, "生成文本最大字符数");
        Streaming = config.Bind("AI", "Streaming", false, "是否开启流式显示（边生成边显示）");
        TimeoutSeconds = config.Bind("AI", "TimeoutSeconds", 10f, "生成超时时间（秒），超时回退原文");
        WebPort = config.Bind("Web", "Port", 8520, "网页配置界面的本地端口");
    }
}
