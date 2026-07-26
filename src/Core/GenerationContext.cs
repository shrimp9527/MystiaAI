using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MystiaAI.Core;

/// <summary>AI 文本生成的上下文。后续扩展新场景时在此追加字段。</summary>
public sealed class GenerationContext
{
    /// <summary>说话 NPC 的角色 ID（游戏内部 SpecialGuest/NPC ID）。</summary>
    public int CharacterId { get; init; }

    /// <summary>说话 NPC 的显示名（按当前游戏语言）。</summary>
    public string CharacterName { get; init; } = string.Empty;

    /// <summary>当前闲聊场景。</summary>
    public ChatScene Scene { get; init; }

    /// <summary>游戏内时间描述（如 "白天"、"夜晚营业中"）。</summary>
    public string GameTime { get; init; } = string.Empty;

    /// <summary>与该角色的羁绊等级，未知为 0。</summary>
    public int KizunaLevel { get; init; }

    /// <summary>游戏当前语言（生成文本需使用该语言）。</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>生成文本的最大字符数。</summary>
    public int MaxLength { get; init; }

    /// <summary>附加信息：评价场景下的菜品、闲聊场景下玩家选择的回复等。</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>();
}

public enum ChatScene
{
    /// <summary>白天地图上的 NPC 闲聊。</summary>
    DayChat,
    /// <summary>夜晚营业中稀客的闲聊。</summary>
    NightChat,
    /// <summary>上菜后稀客的评价语。</summary>
    Evaluation,
}
