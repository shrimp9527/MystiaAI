using System;
using System.Linq;
using System.Text;
using GameData.Core.Collections;

namespace MystiaAI.Core;

/// <summary>
/// 料理信息解析（评价场景的提示词变量用）：
/// 料理简介（Sellable.Text.Description）、配方食材名、配方食材名+简介。
/// 数据链与游戏自身厨具复制逻辑一致（PureHellFryer：MatchRecipe(foodId).First().Ingredients）。
/// 注意：游戏不记录烹饪时追加的具体食材（只合并其标签），因此食材恒为配方基础食材。
/// 任何失败都返回空串，绝不上抛。
/// </summary>
public static class DishInfo
{
    /// <summary>料理本身的简介（如「外焦里嫩的招牌烤鳗…」），失败/为空返回空串。</summary>
    public static string GetDescription(Sellable? food)
    {
        try
        {
            var desc = food?.Text?.Description;
            return string.IsNullOrWhiteSpace(desc) ? string.Empty : desc.Trim();
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] DishInfo: 读取料理简介失败（置空）: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// 配方基础食材：MatchRecipe(foodId) 首个配方的 Ingredients 转本地化名，「、」连接；
    /// withDesc=true 时每个食材附带简介（八目鳗（……）、蜂蜜（……））。失败/无配方返回空串。
    /// </summary>
    public static string GetIngredients(Sellable? food, bool withDesc)
    {
        try
        {
            if (food == null) return string.Empty;
            // 与游戏一致取首个匹配配方（TryMatchRecipe 同语义：Recipes.Values.Where(FoodID 相等)）；
            // 不用 MatchRecipe 的 Linq 结果——interop 的 IEnumerable 枚举器成员全是显式接口实现，
            // foreach/Linq 均不可用，只能直接遍历字典 Values（结构体枚举器有公开成员）
            Recipe? recipe = null;
            foreach (var r in DataBaseCore.Recipes.Values)
            {
                if (r != null && r.FoodID == food.Id) { recipe = r; break; }
            }
            var ids = recipe?.Ingredients;
            if (ids == null || ids.Length == 0)
            {
                PluginContext.Log.LogInfo($"[MystiaAI] DishInfo: 料理 id={food.Id}（{food.Text?.Name}）未匹配到配方");
                return string.Empty;
            }

            var sb = new StringBuilder();
            var count = 0;
            foreach (var id in ids)
            {
                string? name = null;
                string? desc = null;
                try
                {
                    var text = DataBaseCore.RefIngredient(id)?.Text;
                    name = text?.Name;
                    if (withDesc) desc = text?.Description;
                }
                catch { /* 单个食材读取失败跳过 */ }
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (count++ > 0) sb.Append('、');
                sb.Append(name.Trim());
                if (withDesc && !string.IsNullOrWhiteSpace(desc))
                    sb.Append('（').Append(desc.Trim()).Append('）');
            }
            var foodName = food.Text?.Name;
            var ingredientsText = sb.Length > 0 ? sb.ToString() : "<无>";
            PluginContext.Log.LogInfo(
                $"[MystiaAI] DishInfo: 料理 id={food.Id}（{foodName}）配方 recipeId={recipe!.Id} → 食材[{ingredientsText}]");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] DishInfo: 读取配方食材失败（置空）: {ex.Message}");
            return string.Empty;
        }
    }
}
