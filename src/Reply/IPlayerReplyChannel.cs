using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MystiaAI.Reply;

/// <summary>
/// 玩家回应 NPC 的渠道抽象。
/// 当前实现：原生选项面板（AI 生成 2~3 个回复选项供玩家选择）。
/// 预留扩展：自由文字输入（FreeInputReplyChannel）——
/// 后续版本实现本接口即可让玩家自己打字回复，无需改动上层逻辑。
/// </summary>
public interface IPlayerReplyChannel
{
    /// <summary>渠道标识，如 "options" / "free-input"。</summary>
    string ChannelId { get; }

    /// <summary>
    /// 玩家做出回应后，把回应文本交给 AI 生成 NPC 的下一轮台词。
    /// </summary>
    /// <param name="playerReply">玩家选择的选项文本，或（未来）自由输入的文本。</param>
    Task<string> RespondAsync(Core.GenerationContext context, string npcLine, string playerReply, CancellationToken cancellationToken = default);
}
