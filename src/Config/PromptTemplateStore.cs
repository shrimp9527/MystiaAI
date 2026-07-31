using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BepInEx.Logging;

namespace MystiaAI.Config;

/// <summary>
/// 提示词模板存储（文档/MystiaAI/prompts.json）。
/// system：NPC 台词生成的 system 模板；systemReplyOptions：玩家回复选项生成的 system 模板。
/// 变量为英文花括号（如 {characterName}），生成时替换；未知变量原样保留并记一次 Warning。
/// 文件不存在/读取失败时用内置默认模板（即历史上硬编码的文案），热重载节流 2 秒。
/// </summary>
public sealed class PromptTemplateStore
{
    public static readonly string StoreFile = Path.Combine(Settings.StoreDir, "prompts.json");

    /// <summary>内置默认：NPC 台词 system 模板（与旧版硬编码文案一致 + {bondTone} 挂点）。</summary>
    public const string DefaultSystem =
        "你正在扮演《东方夜雀食堂》中的角色 {characterName}。{persona}。{bondTone}" +
        "扮演规则：" +
        "1.贴合原作风格，不OOC，不使用网络流行语；" +
        "2.台词贴合角色性格，使用短句和口语；" +
        "3.人设资料中包含该角色的档案与原版点餐/评价对话范本，" +
        "参考优先级：角色档案＞原版对话范本，尽量复刻范本的句式与口吻；" +
        "4.仅输出角色台词本身，不含旁白、引号与额外说明。" +
        "以该角色的口吻说一句话。要求：使用{language}；不超过{maxLength}字。";

    /// <summary>内置默认：玩家回复选项 system 模板（与旧版硬编码文案一致）。</summary>
    public const string DefaultSystemReplyOptions =
        "你正在扮演米斯蒂娅·萝蕾拉——《东方夜雀食堂》的主角，" +
        "开朗勤劳的夜雀妖怪，经营移动居酒屋的老板娘，歌声动听、待人热情。" +
        "你要产出的是给玩家挑选的米斯蒂娅台词选项，不是在扮演 {characterName}。";

    // ---- 内置默认：五条生成路径的 user 模板（与旧版硬编码输出一致 + {memories} 长期记忆挂点）----

    public const string DefaultUserDayChat =
        "{situationLine}\n{news}{memories}{transcript}\n" +
        "以上是你与对方的对话，最后一句是对方刚刚对你说的话。请直接承接这句话，" +
        "以你的口吻回一句话。（参考：原本的剧本里这句你说的是" +
        "「{targetOriginal}」，仅作语气参考，不必沿用其内容，可以完全不同。）" +
        "只输出这一句台词本身。";

    public const string DefaultUserDaySingle =
        "{situationLine}和她随口闲聊一句。\n{news}{memories}{playerReply}";

    public const string DefaultUserNightChat =
        "{situationLine}随口和老板娘闲聊一句。\n{news}{memories}{playerReply}";

    public const string DefaultUserEvaluation =
        "{situationLine}你吃的是「{dish}」{dishDesc}{dishIngredients}，禁止增加未提及的食材，评价等级为「{rating}」。" +
        "{ratingTone}说出一句符合该评价的感想。{memories}";

    public const string DefaultUserReplyOptions =
        "{mystiaSituationLine}\n{news}{memories}{transcript}\n" +
        "以上是米斯蒂娅与对方的完整对话。玩家（扮演米斯蒂娅）现在要回应对方最后这句话：" +
        "「{npcLine}」。请给出 {optionCount} 个简短的回应选项。" +
        "要求：以米斯蒂娅（开朗勤劳的居酒屋老板娘夜雀）的口吻；风格各异（比如一热情一吐槽）；" +
        "每条不超过{maxLength}字；每个选项一行，不要编号，不要引号，不要解释；使用{language}。";

    private static readonly TimeSpan ReloadThrottle = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文不转义，便于手工编辑
    };

    private static readonly Regex PlaceholderPattern = new(@"\{([A-Za-z][A-Za-z0-9]*)\}", RegexOptions.Compiled);

    private readonly ManualLogSource? _log;
    private readonly HashSet<string> _warnedUnknown = new();
    private FileDto _dto = new();
    private DateTime _lastWriteUtc;
    private DateTime _nextCheckUtc = DateTime.MinValue;

    public PromptTemplateStore(ManualLogSource? log = null)
    {
        _log = log;
        EnsureLoaded();
    }

    /// <summary>NPC 台词 system 模板渲染。vars：characterName/persona/language/maxLength/bondTone。</summary>
    public string RenderSystem(IReadOnlyDictionary<string, string> vars)
        => Render(Current().System, vars);

    /// <summary>玩家回复选项 system 模板渲染。vars 同上（当前默认文案只用 characterName）。</summary>
    public string RenderReplyOptionsSystem(IReadOnlyDictionary<string, string> vars)
        => Render(Current().SystemReplyOptions, vars);

    /// <summary>user 模板渲染（五条路径共用）。kind：UserDayChat/UserDaySingle/UserNightChat/UserEvaluation/UserReplyOptions。</summary>
    public string RenderUser(UserKind kind, IReadOnlyDictionary<string, string> vars)
        => Render(Current().For(kind), vars);

    /// <summary>五条 user 生成路径。</summary>
    public enum UserKind
    {
        DayChat,
        DaySingle,
        NightChat,
        Evaluation,
        ReplyOptions,
    }

    private string Render(string template, IReadOnlyDictionary<string, string> vars)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        return PlaceholderPattern.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (vars.TryGetValue(key, out var value)) return value;
            if (_warnedUnknown.Add(key))
                _log?.LogWarning($"[MystiaAI] 提示词模板含未知变量 {{{key}}}，已原样保留");
            return m.Value;
        });
    }

    /// <summary>按文件修改时间热重载（节流 2 秒）。</summary>
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
            _log?.LogInfo("[MystiaAI] 检测到提示词模板变更，已热重载 prompts.json");
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[MystiaAI] prompts.json 热重载失败（保持当前值）: {ex.Message}");
        }
    }

    private FileDto Current() => _dto;

    private void EnsureLoaded()
    {
        try
        {
            if (!File.Exists(StoreFile))
            {
                Directory.CreateDirectory(Settings.StoreDir);
                File.WriteAllText(StoreFile, JsonSerializer.Serialize(_dto, JsonOptions));
                _lastWriteUtc = File.GetLastWriteTimeUtc(StoreFile);
                _log?.LogInfo($"[MystiaAI] 已创建提示词模板文件：{StoreFile}");
                return;
            }
            ReadFrom(File.ReadAllText(StoreFile));
            _lastWriteUtc = File.GetLastWriteTimeUtc(StoreFile);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[MystiaAI] prompts.json 读取失败，使用内置默认模板：{ex.Message}");
        }
    }

    private void ReadFrom(string json)
    {
        var dto = JsonSerializer.Deserialize<FileDto>(json);
        if (dto == null) throw new FormatException("prompts.json 不是有效的 JSON");
        if (dto.System != null) _dto.System = dto.System;
        if (dto.SystemReplyOptions != null) _dto.SystemReplyOptions = dto.SystemReplyOptions;
        if (dto.UserDayChat != null) _dto.UserDayChat = dto.UserDayChat;
        if (dto.UserDaySingle != null) _dto.UserDaySingle = dto.UserDaySingle;
        if (dto.UserNightChat != null) _dto.UserNightChat = dto.UserNightChat;
        if (dto.UserEvaluation != null) _dto.UserEvaluation = dto.UserEvaluation;
        if (dto.UserReplyOptions != null) _dto.UserReplyOptions = dto.UserReplyOptions;
    }

    private sealed class FileDto
    {
        [JsonPropertyName("_meta")] public Dictionary<string, string>? Meta { get; set; }
            = new() { ["tool"] = "MystiaAI", ["kind"] = "prompts", ["version"] = "1" };

        [JsonPropertyName("system")] public string? System { get; set; } = DefaultSystem;

        [JsonPropertyName("systemReplyOptions")] public string? SystemReplyOptions { get; set; }
            = DefaultSystemReplyOptions;

        [JsonPropertyName("userDayChat")] public string? UserDayChat { get; set; } = DefaultUserDayChat;
        [JsonPropertyName("userDaySingle")] public string? UserDaySingle { get; set; } = DefaultUserDaySingle;
        [JsonPropertyName("userNightChat")] public string? UserNightChat { get; set; } = DefaultUserNightChat;
        [JsonPropertyName("userEvaluation")] public string? UserEvaluation { get; set; } = DefaultUserEvaluation;
        [JsonPropertyName("userReplyOptions")] public string? UserReplyOptions { get; set; } = DefaultUserReplyOptions;

        public string For(UserKind kind) => kind switch
        {
            UserKind.DayChat => UserDayChat ?? DefaultUserDayChat,
            UserKind.DaySingle => UserDaySingle ?? DefaultUserDaySingle,
            UserKind.NightChat => UserNightChat ?? DefaultUserNightChat,
            UserKind.Evaluation => UserEvaluation ?? DefaultUserEvaluation,
            UserKind.ReplyOptions => UserReplyOptions ?? DefaultUserReplyOptions,
            _ => string.Empty,
        };
    }
}
