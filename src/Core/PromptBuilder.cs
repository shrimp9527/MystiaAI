using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// 全局扮演规则（来自用户的「调用说明」）：随 system prompt 一并发给模型，
    /// 约束台词风格与输出格式；角色档案与原版台词范本在 persona 资料里提供。
    /// </summary>
    private const string RoleplayRules =
        "扮演规则：" +
        "1.贴合原作风格，不OOC，不使用网络流行语；" +
        "2.台词贴合角色性格，使用短句和口语；" +
        "3.人设资料中包含该角色的档案与原版点餐/评价对话范本，" +
        "参考优先级：角色档案＞原版对话范本，尽量复刻范本的句式与口吻；" +
        "4.仅输出角色台词本身，不含旁白、引号与额外说明。";

    /// <summary>
    /// NPC 台词生成：system 为角色扮演指令，user 为场景上下文。
    /// Extra 同时带 transcript（完整对话原文）与 targetOriginal（要改写的那句）时，
    /// 走「多段对话连贯改写」路径；否则保持单句场景模式（营业闲聊/评价等）。
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildMessages(GenerationContext context, string persona)
    {
        var language = MapLanguage(context.Language);
        var max = EffectiveMaxLength(context);

        var system =
            $"你正在扮演《东方夜雀食堂》中的角色 {context.CharacterName}。{persona}。" +
            RoleplayRules +
            $"以该角色的口吻说一句话。要求：使用{language}；不超过{max}字。";

        // 多段对话改写：Patch 层传入整段原文，只改写属于当前角色的那一句
        if (context.Extra.TryGetValue("transcript", out var transcript) && !string.IsNullOrWhiteSpace(transcript)
            && context.Extra.TryGetValue("targetOriginal", out var target) && !string.IsNullOrWhiteSpace(target))
        {
            system +=
                "这场对话由多句组成，你只说其中属于你的话。" +
                "避免与上面对话中已经出现过的句式和开头重复；" +
                "口癖、口头禅除非特别贴切，否则不要用。";

            var user =
                // 时间/场景锚点放最前，避免模型按人设脑补营业场景
                $"{BuildSituationLine(context)}\n" +
                BuildNewsSection(context) +
                $"{transcript}\n" +
                "以上是你与对方的对话，最后一句是对方刚刚对你说的话。请直接承接这句话，" +
                "以你的口吻回一句话。（参考：原本的剧本里这句你说的是" +
                $"「{target}」，仅作语气参考，不必沿用其内容，可以完全不同。）" +
                "只输出这一句台词本身。";

            return new List<ChatMessage>
            {
                new ChatMessage("system", system),
                new ChatMessage("user", user),
            };
        }

        return new List<ChatMessage>
        {
            new ChatMessage("system", system),
            new ChatMessage("user", BuildSceneUser(context)),
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

        var system =
            "你正在扮演米斯蒂娅·萝蕾拉——《东方夜雀食堂》的主角，" +
            "开朗勤劳的夜雀妖怪，经营移动居酒屋的老板娘，歌声动听、待人热情。" +
            $"你要产出的是给玩家挑选的米斯蒂娅台词选项，不是在扮演 {context.CharacterName}。";

        // 米斯蒂娅视角的情境行（非营业时间会额外禁止揽客口吻）
        string user = $"{BuildMystiaSituationLine(context)}\n" + BuildNewsSection(context);

        if (context.Extra.TryGetValue("transcript", out var transcript) && !string.IsNullOrWhiteSpace(transcript))
        {
            // 带完整对话上下文：玩家要回应对方最后一句话
            user +=
                $"{transcript}\n" +
                "以上是米斯蒂娅与对方的完整对话。玩家（扮演米斯蒂娅）现在要回应对方最后这句话：" +
                $"「{npcLine}」。请给出 {optionCount} 个简短的回应选项。";
        }
        else
        {
            user += $"{context.CharacterName} 对你说：「{npcLine}」\n";
        }

        user +=
            "要求：以米斯蒂娅（开朗勤劳的居酒屋老板娘夜雀）的口吻；风格各异（比如一热情一吐槽）；" +
            $"每条不超过{max}字；每个选项一行，不要编号，不要引号，不要解释；使用{language}。";

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
    /// 当日《文文新闻》剪报段（Extra["news"]，空串=未解锁/无数据）。
    /// Extra["newsForce"]="1"（玩家输入命中报纸关键词的回合）时强制注入——
    /// 玩家在谈报纸，AI 必须知道报纸内容才能接话；其余情况按 Settings.NewsFrequency
    /// （百分比）的概率注入，避免 AI 高频提及报纸；掷骰未中或为空时返回空串，
    /// 行为与不传该键完全一致。引导语刻意用「可以」「不要每句都提」的分寸，避免模型句句带报纸。
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
            $"今日《文文新闻》剪报：{news}\n" +
            (force
                ? "如果玩家的话题涉及报纸或新闻，根据剪报内容回答；否则可以自然地把报纸内容当作闲聊话题，但不要每句都提。\n"
                : "可以自然地把报纸内容当作闲聊话题，但不要每句都提。\n");
    }

    /// <summary>
    /// 情境行（NPC 视角，"你"=NPC）：「现在是{GameTime}，{场景描述}。」，
    /// GameTime 为空时只写场景描述。对话改写与单句路径统一使用，避免两处说法打架。
    /// </summary>
    private static string BuildSituationLine(GenerationContext context)
    {
        string sceneDesc;
        switch (context.Scene)
        {
            case ChatScene.DayChat:
                sceneDesc = "你们在户外偶遇闲聊（不是营业时间，不在居酒屋里）";
                break;
            case ChatScene.NightChat:
                sceneDesc = "夜晚营业中，你在米斯蒂娅的夜雀食堂里";
                break;
            case ChatScene.Evaluation:
                sceneDesc = "夜晚营业中，你刚在居酒屋吃完料理";
                break;
            default:
                sceneDesc = "你在与米斯蒂娅闲聊";
                break;
        }
        return WithTime(context, sceneDesc);
    }

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
                sceneDesc = "你与对方在户外偶遇闲聊（不是营业时间，不在居酒屋里）";
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

    private static string BuildSceneUser(GenerationContext context)
    {
        string scene;
        switch (context.Scene)
        {
            case ChatScene.DayChat:
                scene = BuildSituationLine(context)
                    + $"和她随口闲聊一句。你与她的羁绊等级：{context.KizunaLevel}。";
                break;
            case ChatScene.NightChat:
                scene = BuildSituationLine(context)
                    + $"随口和老板娘闲聊一句。你与她的羁绊等级：{context.KizunaLevel}。";
                break;
            case ChatScene.Evaluation:
                scene = BuildEvaluationScene(context);
                break;
            default:
                scene = BuildSituationLine(context) + "和米斯蒂娅随口闲聊一句。";
                break;
        }

        // 当日新闻剪报（若有）跟在情境与场景指令之后
        var newsSection = BuildNewsSection(context);
        if (newsSection.Length > 0)
            scene += "\n" + newsSection.TrimEnd('\n');

        // 玩家上一句回应（自由输入或选项面板），要求 NPC 承接
        if (context.Extra.TryGetValue("playerReply", out var reply) && !string.IsNullOrWhiteSpace(reply))
            scene += $"\n米斯蒂娅刚刚对你说：「{reply}」。请承接她的话回应。";

        return scene;
    }

    private static string BuildEvaluationScene(GenerationContext context)
    {
        // 评价场景：Extra 里带菜品（dish）与评价等级（rating）
        var dish = context.Extra.TryGetValue("dish", out var d) && !string.IsNullOrWhiteSpace(d) ? d : "料理";
        var rating = context.Extra.TryGetValue("rating", out var r) && !string.IsNullOrWhiteSpace(r) ? r : "普通";
        return BuildSituationLine(context)
            + $"你吃的是「{dish}」，评价等级为「{rating}」。"
            + $"说出一句符合该评价的感想。你与老板娘的羁绊等级：{context.KizunaLevel}。";
    }
}
