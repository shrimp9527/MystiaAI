using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MystiaAI.Core;

/// <summary>
/// OpenAI 兼容的 chat completions 客户端（DeepSeek / OpenAI / GLM / Moonshot 等同一套协议）。
/// 失败（HTTP 错误 / 超时 / 解析失败 / 内容为空）一律抛带明确信息的异常，
/// 由调用方（Patches 层）catch 并回退游戏原文。
/// prompt 由 <see cref="PromptBuilder"/> 提供，本类不拼 prompt。
/// </summary>
public sealed class OpenAiCompatibleClient : IAiClient
{
    /// <summary>温度从配置读取（热重载即时生效）；仅作序列化兜底的是 Settings 里的默认值 0.8。</summary>
    private static double Temperature => PluginContext.Settings.Temperature;

    // 静态复用，避免每请求新建导致套接字耗尽；超时分摊到每请求的 CancellationToken 上
    private static readonly HttpClient Http = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly float _timeoutSeconds;
    private readonly PersonaStore _personas;

    public OpenAiCompatibleClient(
        string baseUrl, string apiKey, string model, float timeoutSeconds, PersonaStore personas)
    {
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 10f;
        _personas = personas ?? throw new ArgumentNullException(nameof(personas));
    }

    /// <summary>拼接 chat completions 端点。容忍用户在 BaseUrl 里误填全路径（…/chat/completions），避免重复拼接 404。</summary>
    private string ChatUrl
    {
        get
        {
            var url = _baseUrl.TrimEnd('/');
            const string suffix = "/chat/completions";
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                url = url.Substring(0, url.Length - suffix.Length);
            return url + suffix;
        }
    }

    public async Task<string> GenerateAsync(GenerationContext context, CancellationToken cancellationToken = default)
    {
        var messages = PromptBuilder.BuildMessages(context, GetPersona(context));
        // 诊断：记录实际发给模型的 user 消息（场景/transcript/报纸全在里面），排查「AI 不知道上下文」用
        PluginContext.Log.LogInfo(
            $"[MystiaAI] AI 请求（{context.Scene}/{context.CharacterName}）user 消息：\n{messages[messages.Count - 1].Content}");
        // max_tokens 要留足余量：deepseek-v4 等带思考链的模型会先消耗推理 token，
        // 卡太死会导致正文还没输出就被截断（表现为"AI 返回内容为空"）
        var maxTokens = Math.Max(512, EffectiveMaxLength(context) * 4);
        var body = await PostChatAsync(messages, maxTokens, stream: false, cancellationToken).ConfigureAwait(false);
        var content = ExtractContent(body);
        return Truncate(content.Trim(), context.MaxLength);
    }

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        GenerationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = PromptBuilder.BuildMessages(context, GetPersona(context));
        // max_tokens 要留足余量：deepseek-v4 等带思考链的模型会先消耗推理 token，
        // 卡太死会导致正文还没输出就被截断（表现为"AI 返回内容为空"）
        var maxTokens = Math.Max(512, EffectiveMaxLength(context) * 4);
        var payload = BuildPayload(messages, maxTokens, stream: true);
        var maxLength = context.MaxLength;

        using var timeoutCts = CreateTimeoutSource(cancellationToken);
        using var request = BuildRequest(payload);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(TimeoutMessage());
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var err = await ReadBodySnippetAsync(response).ConfigureAwait(false);
                throw new HttpRequestException(HttpErrorMessage(response, err));
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            var produced = 0;
            while (true)
            {
                string? line;
                try
                {
                    // net6 的 StreamReader.ReadLineAsync 无 CancellationToken 重载，
                    // 取消/超时由 SendAsync 的 token 中止底层流来传播
                    line = await reader.ReadLineAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(TimeoutMessage());
                }

                if (line == null)
                    break; // 流结束
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue; // 跳过 event: 行、注释行、空行

                var data = line.Substring("data:".Length).Trim();
                if (data == "[DONE]")
                    break;

                var delta = ExtractStreamDelta(data);
                if (string.IsNullOrEmpty(delta))
                    continue;

                if (maxLength > 0 && produced + delta.Length > maxLength)
                    delta = delta.Substring(0, maxLength - produced);
                produced += delta.Length;

                yield return delta;

                if (maxLength > 0 && produced >= maxLength)
                    break;
            }
        }
    }

    public async Task<IReadOnlyList<string>> GenerateReplyOptionsAsync(
        GenerationContext context, string npcLine, int optionCount, CancellationToken cancellationToken = default)
    {
        var messages = PromptBuilder.BuildReplyOptionsMessages(context, npcLine, optionCount);
        var maxTokens = Math.Max(128, optionCount * EffectiveMaxLength(context) * 3);
        var body = await PostChatAsync(messages, maxTokens, stream: false, cancellationToken).ConfigureAwait(false);
        var content = ExtractContent(body);

        var options = content
            .Split('\n')
            .Select(CleanOptionLine)
            .Where(s => s.Length > 0)
            .Take(optionCount)
            .Select(s => Truncate(s, context.MaxLength))
            .ToList();

        if (options.Count == 0)
            throw new InvalidOperationException("AI 未返回任何可用的回复选项");
        return options;
    }

    /// <summary>
    /// 角色内部名（characterKey）：优先取 Extra["characterKey"]（Patches 层若能拿到内部名会塞这里），
    /// 否则退化为显示名——英文语言下恰好与内部名一致。
    /// 分类：优先 Extra["personaCategory"]（如夜晚普客标记 NormalGuest），否则按场景推断。
    /// </summary>
    private string GetPersona(GenerationContext context)
    {
        var key = context.Extra.TryGetValue("characterKey", out var k) && !string.IsNullOrWhiteSpace(k)
            ? k
            : context.CharacterName;
        var category = context.Extra.TryGetValue("personaCategory", out var c) && !string.IsNullOrWhiteSpace(c)
            ? c
            : CategoryFromScene(context.Scene);
        return _personas.GetPersona(key, category);
    }

    private static string CategoryFromScene(ChatScene scene) => scene switch
    {
        ChatScene.DayChat => PersonaStore.CategoryDayNpc,
        _ => PersonaStore.CategorySpecialGuest, // NightChat / Evaluation 默认按稀客兜底
    };

    private async Task<string> PostChatAsync(
        IReadOnlyList<ChatMessage> messages, int maxTokens, bool stream, CancellationToken cancellationToken)
    {
        var payload = BuildPayload(messages, maxTokens, stream);
        using var timeoutCts = CreateTimeoutSource(cancellationToken);
        using var request = BuildRequest(payload);

        try
        {
            using var response = await Http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(HttpErrorMessage(response, Truncate(body, 200)));
            return body;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(TimeoutMessage());
        }
    }

    private object BuildPayload(IReadOnlyList<ChatMessage> messages, int maxTokens, bool stream)
    {
        return new
        {
            model = _model,
            messages,
            max_tokens = maxTokens,
            temperature = Temperature,
            stream,
        };
    }

    private HttpRequestMessage BuildRequest(object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return request;
    }

    private CancellationTokenSource CreateTimeoutSource(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        return cts;
    }

    private string TimeoutMessage()
    {
        return $"AI 请求超时（{_timeoutSeconds} 秒）：{ChatUrl}";
    }

    private string HttpErrorMessage(HttpResponseMessage response, string bodySnippet)
    {
        return $"AI 请求失败（HTTP {(int)response.StatusCode} {response.ReasonPhrase}）：{bodySnippet}";
    }

    private static async Task<string> ReadBodySnippetAsync(HttpResponseMessage response)
    {
        try
        {
            return Truncate(await response.Content.ReadAsStringAsync().ConfigureAwait(false), 200);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>解析非流式响应 choices[0].message.content。</summary>
    private static string ExtractContent(string body)
    {
        string? content;
        try
        {
            using var doc = JsonDocument.Parse(body);
            content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new InvalidOperationException($"AI 响应解析失败：{ex.Message}。响应片段：{Truncate(body, 200)}");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            // 把 finish_reason 和用量带进错误信息，便于区分「思考链耗尽 token」与其他空响应
            var detail = string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var choice = doc.RootElement.GetProperty("choices")[0];
                if (choice.TryGetProperty("finish_reason", out var reason))
                    detail = $"(finish_reason={reason.GetString()})";
            }
            catch { /* 诊断信息拿不到就算了 */ }
            throw new InvalidOperationException($"AI 返回内容为空{detail}");
        }
        return content;
    }

    /// <summary>解析一行 SSE data，取 choices[0].delta.content。</summary>
    private static string? ExtractStreamDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
            return delta.TryGetProperty("content", out var content) ? content.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new InvalidOperationException($"AI 流式响应解析失败：{ex.Message}。数据：{Truncate(data, 200)}");
        }
    }

    /// <summary>去掉模型可能输出的行首编号/项目符号与引号。</summary>
    private static string CleanOptionLine(string line)
    {
        var s = line.Trim().Trim('"', '“', '”');
        var i = 0;
        while (i < s.Length
               && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '、' || s[i] == ')'
                   || s[i] == '）' || s[i] == '-' || s[i] == '*' || s[i] == ' '))
        {
            i++;
        }
        // 整行都是符号时不做截断（避免误删正文）
        return i > 0 && i < s.Length ? s.Substring(i).Trim() : s;
    }

    private static int EffectiveMaxLength(GenerationContext context)
    {
        return context.MaxLength > 0 ? context.MaxLength : 50;
    }

    private static string Truncate(string s, int max)
    {
        return max > 0 && s.Length > max ? s.Substring(0, max) : s;
    }
}
