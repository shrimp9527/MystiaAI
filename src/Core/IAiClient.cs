using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MystiaAI.Core;

/// <summary>
/// AI 文本生成后端抽象。
/// 任何供应商（OpenAI 兼容 API / 本地模型 / 测试用假 AI）都实现此接口，
/// 上层（Patch 层）只依赖接口，不关心具体供应商。
/// 流式与非流式从第一天起就在接口上，避免后续 breaking change。
/// </summary>
public interface IAiClient
{
    /// <summary>非流式生成一段文本。</summary>
    Task<string> GenerateAsync(GenerationContext context, CancellationToken cancellationToken = default);

    /// <summary>流式生成，逐段产出文本片段。</summary>
    IAsyncEnumerable<string> GenerateStreamAsync(GenerationContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// 生成玩家（米斯蒂娅）的回复选项，供原生选项面板显示。
    /// 自由输入扩展不走此方法，见 <see cref="Reply.IPlayerReplyChannel"/>。
    /// </summary>
    Task<IReadOnlyList<string>> GenerateReplyOptionsAsync(
        GenerationContext context, string npcLine, int optionCount, CancellationToken cancellationToken = default);
}
