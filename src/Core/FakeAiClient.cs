using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace MystiaAI.Core;

/// <summary>
/// 开发阶段用的假 AI：不联网，直接返回可识别的测试文本。
/// 用于在接入真实 API 之前先验证「文本替换通道」是否打通。
/// </summary>
public sealed class FakeAiClient : IAiClient
{
    public async Task<string> GenerateAsync(GenerationContext context, CancellationToken cancellationToken = default)
    {
        // 模拟真实 API 的网络耗时，供「……占位→替换」异步流程联调
        await Task.Delay(1500, cancellationToken);
        return $"[AI测试]{context.CharacterName}在{context.Scene}场景说话";
    }

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        GenerationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var full = await GenerateAsync(context, cancellationToken);
        // 模拟逐段流出，供流式显示链路联调
        foreach (var chunk in Chunk(full, 4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(80, cancellationToken);
            yield return chunk;
        }
    }

    public Task<IReadOnlyList<string>> GenerateReplyOptionsAsync(
        GenerationContext context, string npcLine, int optionCount, CancellationToken cancellationToken = default)
    {
        var options = new List<string>();
        for (var i = 1; i <= optionCount; i++)
            options.Add($"[AI测试]回复选项{i}");
        return Task.FromResult<IReadOnlyList<string>>(options);
    }

    private static IEnumerable<string> Chunk(string s, int size)
    {
        for (var i = 0; i < s.Length; i += size)
            yield return s.Substring(i, System.Math.Min(size, s.Length - i));
    }
}
