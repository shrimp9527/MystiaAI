using System;
using System.Text;
using GameData.RunTime.Common;

namespace MystiaAI.Core;

/// <summary>
/// 文文新闻（剪报）当日内容读取器。
/// 数据源：<see cref="RunTimeScheduler.newsData"/>（static，全部历史剪报列表）——
/// 档案菜单剪报页（Common.UI.NoteBookUtility.NoteBookNewsPannel.OnPanelOpen →
/// RunTimeScheduler.GetPageData(page)/GetPageCount()）分页展示的正是这份数据。
/// 「当日」判定：每天清晨 ApplyNews() 把当天新闻追加到 newsData 并打上当天 newsDate
/// （RunTimeScheduler 没有公开的当前日期 getter，docs/game-api.md F 节亦注明），
/// 因此取 newsData 中最大的 newsDate.day——当天白天时段，最新一批就是今晨刷新的当日剪报。
/// 注意：bufferedDailyNewsData 不是可靠数据源（实测白天读出为空，
/// 推测仅供刷新瞬间的报纸弹窗使用，随即被消费清空）。
/// 标题经 HistoryNewsData.GetNewsLanguage() → LanguageBase.Name 取当前语言显示文本。
/// 数据每日刷新，每次生成都现读，不缓存。
/// </summary>
public static class NewspaperReader
{
    /// <summary>
    /// 当日剪报摘要：当日各条标题以「、」连接（如「流行【神风】、流行【甜食】」）。
    /// 未解锁/无数据/任何异常都返回空串并记 Warning（含关键字段实况诊断），绝不上抛。
    /// </summary>
    public static string GetTodayNewsSummary()
    {
        try
        {
            var all = RunTimeScheduler.newsData;
            var total = all?.Count ?? 0;

            // 诊断：一次实测即可对齐各字段实况
            int? bufferedLen = null;
            var scheduledKeys = -1;
            try { bufferedLen = RunTimeScheduler.bufferedDailyNewsData?.Length; } catch { /* 诊断失败无碍 */ }
            try { scheduledKeys = RunTimeScheduler.scheduledNews?.Count ?? -1; } catch { /* 同上 */ }

            if (total == 0)
            {
                PluginContext.Log.LogWarning(
                    $"[MystiaAI] NewspaperReader: newsData 为空（剪报未解锁或今日未刷新），news 置空串" +
                    $"（newsData={total} scheduledNews键数={scheduledKeys} buffered={(bufferedLen?.ToString() ?? "null")}）");
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
                string? title = null;
                try
                {
                    if (item.newsDate.day != maxDay) continue;
                    title = item.GetNewsLanguage()?.Name;
                }
                catch (Exception ex)
                {
                    PluginContext.Log.LogWarning($"[MystiaAI] NewspaperReader: 单条剪报读取失败（跳过）: {ex.Message}");
                }
                if (string.IsNullOrWhiteSpace(title)) continue;
                if (count++ > 0) sb.Append('、');
                sb.Append(title.Trim());
            }

            PluginContext.Log.LogInfo(
                $"[MystiaAI] NewspaperReader 诊断: newsData={total} 最新day={maxDay} 当日条数={count} " +
                $"scheduledNews键数={scheduledKeys} buffered={(bufferedLen?.ToString() ?? "null")}");

            if (count == 0)
            {
                PluginContext.Log.LogWarning("[MystiaAI] NewspaperReader: 当日剪报无有效标题，news 置空串");
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
