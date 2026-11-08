#nullable disable // 与仓库既有 Core 文件风格一致
using System;
using System.Collections.Generic;
using System.Text;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// 三级筛选的产物 = 一次查价的键。设计 §「筛选产物直接映射缓存键」：
    /// （uniqueName 集合, quality 集合），城市恒为亚服六城全集。
    ///
    /// 这里刻意**不携带任何价格/差价/利润字段**——市场屏只查价，算账归跑商页（任务边界）。
    /// 键就是「查什么」，与「查出来多少」分开，保证同一筛选组合能命中同一份缓存。
    /// </summary>
    public class MarketQueryKey
    {
        /// <summary>要查的 uniqueName（含附魔后缀，如 T5_HEAD_CLOTH_SET1@1）。</summary>
        public readonly List<string> UniqueNames = new List<string>();
        /// <summary>要查的品质（AODP quality 1..5）。</summary>
        public readonly List<int> Qualities = new List<int>();

        /// <summary>稳定签名：排序后拼接，用作 single-flight 去重与「换筛选才重查」的判定依据。</summary>
        public string Signature
        {
            get
            {
                var items = new List<string>(UniqueNames);
                items.Sort(StringComparer.Ordinal);
                var qs = new List<int>(Qualities);
                qs.Sort();
                var sb = new StringBuilder();
                sb.Append(string.Join(",", items.ToArray()));
                sb.Append('|');
                for (int i = 0; i < qs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(qs[i]);
                }
                return sb.ToString();
            }
        }

        public bool IsEmpty { get { return UniqueNames.Count == 0 || Qualities.Count == 0; } }

        public int ItemCount { get { return UniqueNames.Count; } }

        /// <summary>单元格总数 = 物品 × 六城 × 品质，用于「本次查询会打多少次」的自检与日志。</summary>
        public int CellCount(int cityCount)
        {
            return UniqueNames.Count * Math.Max(0, cityCount) * Qualities.Count;
        }
    }

    /// <summary>一次筛选选择的完整状态：一级/二级/三级族 + 阶级 + 附魔 + 品质。</summary>
    public class MarketSelection
    {
        public string CategoryKey;      // 一级（元数据 key）
        public string SubCategoryKey;   // 二级（元数据 key）
        public string Family;           // 三级族键（如 HEAD_CLOTH_SET1）
        public int Tier;                // 阶级，0 = 未选
        public int Enchant;             // 附魔，0 = 无附魔
        public readonly List<int> Qualities = new List<int>();

        public bool HasCategory { get { return !string.IsNullOrEmpty(CategoryKey); } }
        public bool HasSubCategory { get { return !string.IsNullOrEmpty(SubCategoryKey); } }
        public bool HasFamily { get { return !string.IsNullOrEmpty(Family); } }

        /// <summary>三级是否选满（只有选满才允许查价；未选满 = 设计里的 M5 状态）。</summary>
        public bool IsComplete { get { return HasFamily && Tier > 0; } }

        public static readonly int[] AllQualities = { 1, 2, 3, 4, 5 };

        public MarketSelection()
        {
            Qualities.AddRange(AllQualities);
        }

        /// <summary>切换一级：按设计 §4.2 硬条件，二级/三级已选必须立刻清空，否则会查不存在的组合。</summary>
        public void SelectCategory(string key)
        {
            CategoryKey = key;
            SubCategoryKey = null;
            Family = null;
            Tier = 0;
            Enchant = 0;
        }

        /// <summary>切换二级：同样清空三级。</summary>
        public void SelectSubCategory(string key)
        {
            SubCategoryKey = key;
            Family = null;
            Tier = 0;
            Enchant = 0;
        }

        public void SelectFamily(string family)
        {
            Family = family;
            Tier = 0;
            Enchant = 0;
        }

        /// <summary>选定阶级 + 附魔变体（三级真正的落点）。</summary>
        public void SelectVariant(int tier, int enchant)
        {
            Tier = tier;
            Enchant = Math.Max(0, enchant);
        }

        public void Clear()
        {
            CategoryKey = null;
            SubCategoryKey = null;
            Family = null;
            Tier = 0;
            Enchant = 0;
            Qualities.Clear();
            Qualities.AddRange(AllQualities);
        }
    }

    /// <summary>把选择落成查价键（uniqueName 集合 + quality 集合）。纯函数，可离线测试。</summary>
    public static class MarketQueryPlanner
    {
        /// <summary>按当前选择产出查询键；未选满返回空键（UI 显示 M5「请先选择物品」，不发请求）。</summary>
        public static MarketQueryKey Build(ItemTaxonomy tax, MarketSelection selection)
        {
            var key = new MarketQueryKey();
            if (tax == null || selection == null || !selection.IsComplete) return key;

            var category = tax.Find(selection.CategoryKey);
            if (category == null) return key;

            ItemFamily family = null;
            foreach (var sub in category.SubCategories)
            {
                if (selection.HasSubCategory && sub.Key != selection.SubCategoryKey) continue;
                foreach (var f in sub.Families)
                {
                    if (f.Family == selection.Family) { family = f; break; }
                }
                if (family != null) break;
            }
            if (family == null) return key; // M9：组合在物品表里不存在

            var variant = family.Find(selection.Tier, selection.Enchant);
            if (variant == null) return key;

            key.UniqueNames.Add(variant.UniqueName);
            foreach (int q in selection.Qualities)
                if (q >= 1 && q <= 5 && !key.Qualities.Contains(q)) key.Qualities.Add(q);
            key.Qualities.Sort();
            return key;
        }

        /// <summary>面包屑三段文案：一级 › 二级 › T5 · 附魔 .1。未选满的部分返回空串。</summary>
        public static string[] Breadcrumbs(ItemTaxonomy tax, MarketSelection selection)
        {
            var parts = new string[3];
            if (tax == null || selection == null) return parts;

            var category = tax.Find(selection.CategoryKey);
            if (category != null) parts[0] = category.DisplayName;

            if (category != null && selection.HasSubCategory)
            {
                foreach (var sub in category.SubCategories)
                    if (sub.Key == selection.SubCategoryKey) { parts[1] = sub.DisplayName; break; }
            }

            if (selection.IsComplete)
            {
                string tier = "T" + selection.Tier;
                parts[2] = selection.Enchant > 0 ? tier + " · 附魔 ." + selection.Enchant : tier + " · 附魔 .0";
            }
            return parts;
        }
    }
}
