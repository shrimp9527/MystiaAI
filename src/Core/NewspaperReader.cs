using System;
using System.Text;
using GameData.Core.Collections;
using GameData.RunTime.Common;

namespace MystiaAI.Core;

/// <summary>
/// 文文新闻（剪报）当日内容读取器。
/// 数据源：<see cref="RunTimeScheduler.newsData"/>（static，全部历史剪报列表）——
/// 档案菜单剪报页（Common.UI.NoteBookUtility.NoteBookNewsPannel.OnPanelOpen →
/// RunTimeScheduler.GetPageData(page)/GetPageCount()）分页展示的正是这份数据。
/// 「当日」判定：每天清晨新一天的新闻先入 bufferedDailyNewsData，玩家看报时
/// CheckAndApplyNewDailyNewsData() 把它追加到 newsData 并打上当天 newsDate
/// （RunTimeScheduler 没有公开的当前日期 getter，docs/game-api.md F 节亦注明），
/// 因此取 newsData 中最大的 newsDate.day——当天白天时段，最新一批就是今晨刷新的当日剪报。
/// 注意：bufferedDailyNewsData 不是可靠数据源（实测白天读出为空，
/// 推测仅供刷新瞬间的报纸弹窗使用，随即被消费清空）。
/// 标题与正文经 HistoryNewsData.GetNewsLanguage() → LanguageBase.Name/.Description 取当前语言
/// 显示文本（正文由该方法完成 $a-$z 占位符替换）；未解锁新闻（图鉴不显示）用
/// DataBaseScheduler.IsNewsPresent(newsLabel) 过滤，防止 NEWS_NAME:xxx 垃圾文本进 prompt。
/// 数据每日刷新，每次生成都现读，不缓存。
/// </summary>
public static class NewspaperReader
{
    /// <summary>
    /// 当日剪报摘要：当日各条「标题：正文」以「；」连接（如「流行【神风】：今天大家都想吃……」）。
    /// 未解锁/无数据/任何异常都返回空串并记 Warning（含关键字段实况诊断），绝不上抛。
    /// </summary>
    public static string GetTodayNewsSummary()
    {
        try
        {
            var all = RunTimeScheduler.newsData;
            var total = all?.Count ?? 0;

            if (total == 0)
            {
                PluginContext.Log.LogWarning(
                    $"[MystiaAI] NewspaperReader: newsData 为空（剪报未解锁或今日未刷新），news 置空串");
                return string.Empty;
            }

            // 当日 = newsData 中最大的 newsDate.day（今晨 ApplyNews 追加的那批）
            var maxDay = int.MinValue;
            foreach (var item in all!)
            {
                try
                {
                    if (item.newsDate.day > maxDay) maxDay = item.newsDate.day;
                }
                catch { /* 单条日期读取失败跳过 */ }
            }

            var sb = new StringBuilder();
            var count = 0;
            foreach (var item in all!)
            {
                try
                {
                    if (item.newsDate.day != maxDay) continue;
                    // 未解锁的新闻不过滤会拿到语言库兜底垃圾文本（NEWS_NAME:xxx），
                    // 与图鉴剪报页同口径：只取 IsNewsPresent 的条目
                    if (!DataBaseScheduler.IsNewsPresent(item.newsLabel)) continue;
                    var lang = item.GetNewsLanguage();
                    var title = lang?.Name;
                    if (string.IsNullOrWhiteSpace(title) || title.StartsWith("NEWS_NAME:")) continue;
                    if (count++ > 0) sb.Append('；');
                    sb.Append(title.Trim());
                    var body = lang?.Description;
                    if (!string.IsNullOrWhiteSpace(body) && !body.StartsWith("NEWS_DESC:"))
                        sb.Append('：').Append(body.Trim());
                }
                catch (Exception ex)
                {
                    PluginContext.Log.LogWarning($"[MystiaAI] NewspaperReader: 单条剪报读取失败（跳过）: {ex.Message}");
                }
            }

            if (count == 0)
            {
                PluginContext.Log.LogWarning("[MystiaAI] NewspaperReader: 当日剪报无有效条目，news 置空串");
                return string.Empty;
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] NewspaperReader.GetTodayNewsSummary 异常（返回空串）: {ex.Message}");
            return string.Empty;
        }
    }
}
