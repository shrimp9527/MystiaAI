using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using MystiaAI.Config;

namespace MystiaAI.Core;

/// <summary>OpenAI chat completions 的一条消息。属性名按 API 要求序列化为小写。</summary>
public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; }

    [JsonPropertyName("content")]
    public string Content { get; }

    public ChatMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }
}

/// <summary>
/// 把 GenerationContext + 人设拼成 chat completions 的 messages（system + user）。
/// 客户端只负责发送，不在这里之外拼 prompt。
/// system 走 prompts.json 模板（变量：characterName/persona/language/maxLength/bondTone），
/// 模板缺失时用内置默认（与本类旧硬编码文案一致）。
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// NPC 台词生成：system 为角色扮演指令，user 为场景上下文（均走 prompts.json 模板）。
    /// Extra 同时带 transcript（完整对话原文）与 targetOriginal（要改写的那句）时，
    /// 走「多段对话连贯改写」路径（userDayChat）；否则按场景走单句模板
    /// （userDaySingle/userNightChat/userEvaluation）。
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildMessages(GenerationContext context, string persona)
    {
        var language = MapLanguage(context.Language);
        var max = EffectiveMaxLength(context);
        var vars = BuildVars(context, persona, language, max);

        var system = PluginContext.Prompts.RenderSystem(vars);
        // 长期记忆：作为 {memories} 模板变量注入 User 提示词（见 BuildVars/RenderUser），
        // 用户可在 prompts.json 的 User 模板任意位置放置；模板无挂点时由 RenderUser 兜底追加。

        // 多段对话改写：Patch 层传入整段原文，只改写属于当前角色的那一句
        if (context.Extra.TryGetValue("transcript", out var transcript) && !string.IsNullOrWhiteSpace(transcript)
            && context.Extra.TryGetValue("targetOriginal", out var target) && !string.IsNullOrWhiteSpace(target))
        {
            system +=
                "这场对话由多句组成，你只说其中属于你的话。" +
                "避免与上面对话中已经出现过的句式和开头重复；" +
                "口癖、口头禅除非特别贴切，否则不要用。";

            return new List<ChatMessage>
            {
                new ChatMessage("system", system),
                new ChatMessage("user", RenderUser(PromptTemplateStore.UserKind.DayChat, vars)),
            };
        }

        var kind = context.Scene switch
        {
            ChatScene.NightChat => PromptTemplateStore.UserKind.NightChat,
            ChatScene.Evaluation => PromptTemplateStore.UserKind.Evaluation,
            _ => PromptTemplateStore.UserKind.DaySingle,
        };
        return new List<ChatMessage>
        {
            new ChatMessage("system", system),
            new ChatMessage("user", RenderUser(kind, vars)),
        };
    }

    /// <summary>
    /// 玩家（米斯蒂娅）回复选项生成：输出 N 行、每行一条、风格各异、不带编号。
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildReplyOptionsMessages(
        GenerationContext context, string npcLine, int optionCount)
    {
        var language = MapLanguage(context.Language);
        var max = EffectiveMaxLength(context);
        var vars = BuildVars(context, string.Empty, language, max);
        vars["npcLine"] = npcLine ?? string.Empty;
        vars["optionCount"] = optionCount.ToString();

        var system = PluginContext.Prompts.RenderReplyOptionsSystem(vars);
        var user = RenderUser(PromptTemplateStore.UserKind.ReplyOptions, vars);

        return new List<ChatMessage>
        {
            new ChatMessage("system", system),
            new ChatMessage("user", user),
        };
    }

    /// <summary>
    /// 游戏语言枚举名（Chinese / CNT / English / Japanese / Korean）映射为自然语言名。
    /// 无法识别时原样透传，空值按简体中文处理。
    /// </summary>
    public static string MapLanguage(string language)
    {
        var trimmed = (language ?? string.Empty).Trim();
        switch (trimmed.ToLowerInvariant())
        {
            case "chinese": return "简体中文";
            case "cnt": return "繁体中文";
            case "english": return "English";
            case "japanese": return "日本語";
            case "korean": return "한국어";
            case "": return "简体中文";
            default: return trimmed;
        }
    }

    private static int EffectiveMaxLength(GenerationContext context)
    {
        return context.MaxLength > 0 ? context.MaxLength : 50;
    }

    /// <summary>
    /// 长期记忆段（Extra["memories"]，空串=无记忆）：过去对话的原文尾部片段，
    /// 让角色自然地承接之前聊过的话题；引导语强调「可以提及、不要每句都提」，
    /// 与报纸段同一分寸策略，避免 AI 句句翻旧账。
    /// </summary>
    private static string BuildMemoriesSection(GenerationContext context)
    {
        if (!context.Extra.TryGetValue("memories", out var memories)
            || string.IsNullOrWhiteSpace(memories))
            return string.Empty;
        return
            $"你们过去的对话记忆（以下是你与米斯蒂娅之前聊过的内容，可以自然地提及，但不要每句都提）：\n{memories.TrimEnd()}";
    }

    /// <summary>
    /// 当日《文文新闻》剪报段（Extra["news"]，空串=未解锁/无数据）。
    /// Extra["newsForce"]="1"（玩家输入命中报纸关键词的回合）时强制注入——
    /// 玩家在谈报纸，AI 必须知道报纸内容才能接话；其余情况按 Settings.NewsFrequency
    /// （百分比）的概率注入，避免 AI 高频提及报纸；掷骰未中或为空时返回空串。
    /// 注入时以「今日《文文新闻》内容为：」开头，让模型明确这是当日报纸原文，
    /// 防止模型刻意聊到「今天没有报纸」这类话题。
    /// </summary>
    private static string BuildNewsSection(GenerationContext context)
    {
        if (!context.Extra.TryGetValue("news", out var news) || string.IsNullOrWhiteSpace(news))
            return string.Empty;
        var force = context.Extra.TryGetValue("newsForce", out var f) && f == "1";
        if (!force)
        {
            // 按配置概率注入，避免 AI 高频提及报纸；Random.Shared 线程安全
            var freq = Math.Clamp(PluginContext.Settings.NewsFrequency, 0, 100);
            if (freq < 100 && Random.Shared.Next(100) >= freq)
            {
                PluginContext.Log.LogInfo($"[MystiaAI] 报纸话题本次未注入（频率 {freq}%）");
                return string.Empty;
            }
        }
        return
            $"今日《文文新闻》内容为：{news}\n" +
            (force
                ? "如果玩家的话题涉及报纸或新闻，根据剪报内容回答；否则可以自然地把报纸内容当作闲聊话题，但不要每句都提。\n"
                : "可以自然地把报纸内容当作闲聊话题，但不要每句都提。\n");
    }

    /// <summary>地点文本（Extra["location"]，空/未传时按「户外」兜底）。</summary>
    private static string LocationText(GenerationContext context)
    {
        return context.Extra.TryGetValue("location", out var loc) && !string.IsNullOrWhiteSpace(loc)
            ? loc.Trim()
            : "户外";
    }

    /// <summary>
    /// 情境行（NPC 视角，"你"=NPC）：「现在是{GameTime}，{场景描述}。」，
    /// GameTime 为空时只写场景描述。对话改写与单句路径统一使用，避免两处说法打架。
    /// </summary>
    private static string BuildSituationLine(GenerationContext context)
        => WithTime(context, SceneDesc(context));

    /// <summary>
    /// 情境行（米斯蒂娅视角，"你"=玩家扮演的米斯蒂娅），用于回复选项路径。
    /// 非营业时间（DayChat）额外追加禁止揽客/营业口吻的约束。
    /// </summary>
    private static string BuildMystiaSituationLine(GenerationContext context)
    {
        string sceneDesc;
        var notOpen = false;
        switch (context.Scene)
        {
            case ChatScene.DayChat:
                sceneDesc = $"你与对方在{LocationText(context)}偶遇闲聊（不是营业时间，不在居酒屋里）";
                notOpen = true;
                break;
            case ChatScene.NightChat:
                sceneDesc = "夜晚营业中，你在自己的居酒屋里招待客人";
                break;
            case ChatScene.Evaluation:
                sceneDesc = "夜晚营业中，对方刚在你的居酒屋吃完料理";
                break;
            default:
                sceneDesc = "你在与对方闲聊";
                break;
        }

        var line = WithTime(context, sceneDesc);
        if (notOpen)
            line += "现在不是营业时间，回复不要带揽客/营业口吻。";
        return line;
    }

    private static string WithTime(GenerationContext context, string sceneDesc)
    {
        return string.IsNullOrWhiteSpace(context.GameTime)
            ? $"{sceneDesc}。"
            : $"现在是{context.GameTime}，{sceneDesc}。";
    }

    /// <summary>场景描述（不含时间，供 {scene} 变量与情境行共用）。</summary>
    private static string SceneDesc(GenerationContext context)
    {
        switch (context.Scene)
        {
            case ChatScene.DayChat: return $"你们在{LocationText(context)}偶遇闲聊（不是营业时间，不在居酒屋里）";
            case ChatScene.NightChat: return "夜晚营业中，你在米斯蒂娅的夜雀食堂里";
            case ChatScene.Evaluation: return "夜晚营业中，你刚在居酒屋吃完料理";
            default: return "你在与米斯蒂娅闲聊";
        }
    }

    /// <summary>
    /// 组装全部模板变量（system/user 模板共用一份）。
    /// user 专用键（transcript/targetOriginal/dish/rating/news/playerReply 等）也在此取好。
    /// </summary>
    private static Dictionary<string, string> BuildVars(GenerationContext context, string persona, string language, int max)
    {
        // 羁绊语气提示词：开关关闭时恒为空；key 解析与人设同口径（characterKey 优先，退回显示名）
        var bondTone = string.Empty;
        if (PluginContext.Settings.BondPromptEnabled)
        {
            var bondKey = context.Extra.TryGetValue("characterKey", out var ck) && !string.IsNullOrWhiteSpace(ck)
                ? ck
                : context.CharacterName;
            bondTone = PluginContext.Personas.GetBondTone(bondKey, context.KizunaLevel);
        }

        // 评价语气提示词（无开关，空档即不注入）：key 解析同上
        var ratingKey = context.Extra.TryGetValue("characterKey", out var rk) && !string.IsNullOrWhiteSpace(rk)
            ? rk
            : context.CharacterName;
        var ratingTone = PluginContext.Personas.GetRatingTone(ratingKey, Extra(context, "rating"));

        // 长期记忆段：开关（MemoryEnabled）关闭或注入条数为 0 时恒为空；
        // 模板写 {memories} 即替换，未写则由 RenderUser 兜底追加
        var memories = BuildMemoriesSection(context);

        return new Dictionary<string, string>
        {
            ["characterName"] = context.CharacterName ?? string.Empty,
            ["persona"] = persona ?? string.Empty,
            ["language"] = language,
            ["maxLength"] = max.ToString(),
            ["bondTone"] = bondTone,
            ["ratingTone"] = ratingTone,
            ["gameTime"] = context.GameTime ?? string.Empty,
            ["location"] = LocationText(context),
            ["scene"] = SceneDesc(context),
            ["situationLine"] = BuildSituationLine(context),
            ["mystiaSituationLine"] = BuildMystiaSituationLine(context),
            ["transcript"] = Extra(context, "transcript"),
            ["targetOriginal"] = Extra(context, "targetOriginal"),
            ["dish"] = ExtraOr(context, "dish", "料理"),
            ["rating"] = ExtraOr(context, "rating", "普通评价"),
            ["dishDesc"] = Extra(context, "dishDesc"),
            ["dishIngredients"] = Extra(context, "dishIngredients"),
            ["dishIngredientsDesc"] = Extra(context, "dishIngredientsDesc"),
            ["news"] = BuildNewsSection(context),
            ["playerReply"] = PlayerReplySection(context),
            ["memories"] = memories, // 长期记忆段（模板变量 {memories}）
            ["npcLine"] = string.Empty, // 回复选项路径覆盖
            ["optionCount"] = "2",      // 回复选项路径覆盖
        };
    }

    /// <summary>
    /// 渲染 user 模板并收尾：折叠多余空行（报纸未注入时不留空白），去首尾空白。
    /// 长期记忆兜底：模板里没写 {memories} 时（旧版 prompts.json 或用户自定义模板未放置），
    /// 若记忆非空则追加到末尾——保证记忆功能在任何模板下都生效；
    /// 模板写了 {memories} 则已在渲染时替换，此处检测到内容已含记忆文本不会重复追加。
    /// </summary>
    private static string RenderUser(PromptTemplateStore.UserKind kind, Dictionary<string, string> vars)
    {
        var text = PluginContext.Prompts.RenderUser(kind, vars);
        while (text.Contains("\n\n")) text = text.Replace("\n\n", "\n");
        var trimmed = text.Trim();

        // 兜底：模板未放置 {memories} 且记忆非空 → 追加到末尾（兼容旧 prompts.json）
        if (vars.TryGetValue("memories", out var memories) && !string.IsNullOrWhiteSpace(memories)
            && !trimmed.Contains("你们过去的对话记忆"))
        {
            trimmed = (trimmed.Length > 0 ? trimmed + "\n" : "") + memories.Trim();
        }
        return trimmed;
    }

    /// <summary>玩家上一句回应段（自由输入或选项面板）：有则整句带承接引导，无则空串。</summary>
    private static string PlayerReplySection(GenerationContext context)
    {
        return context.Extra.TryGetValue("playerReply", out var reply) && !string.IsNullOrWhiteSpace(reply)
            ? $"米斯蒂娅刚刚对你说：「{reply}」。请承接她的话回应。"
            : string.Empty;
    }

    private static string Extra(GenerationContext context, string key)
        => context.Extra.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : string.Empty;

    private static string ExtraOr(GenerationContext context, string key, string fallback)
        => context.Extra.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
}
