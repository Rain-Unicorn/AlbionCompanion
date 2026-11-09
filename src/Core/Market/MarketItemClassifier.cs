#nullable disable // 与仓库既有 Core 文件风格一致
using System;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// 一次物品分类结果（一级 / 二级）。<see cref="FromMetadata"/> 是诚实性标记：
    /// false = 元数据缺失、走了旧关键词回退，UI 必须把它显式标注出来（不许静默当成数据分类）。
    /// </summary>
    public class MarketItemClass
    {
        /// <summary>一级 key：元数据原值小写（如 head / mounts / weapons）；回退时为 "fallback:中文标签"。</summary>
        public string CategoryKey;
        public string CategoryLabel;
        /// <summary>二级 key：元数据原值小写（如 cloth_helmet / shieldtype）；回退时为 null。</summary>
        public string SubKey;
        public string SubLabel;
        /// <summary>true = 分类来自 @shopcategory 元数据；false = 关键词回退（UI 标注「回退」）。</summary>
        public bool FromMetadata;
        /// <summary>true = 能出现在 ItemTaxonomy 三级树里（有分类元数据）。</summary>
        public bool InTaxonomy;
    }

    /// <summary>在三级树里按物品 ID 定位到的位置（搜索结果导航用）。</summary>
    public class LocatedTaxonomyItem
    {
        public ItemCategory Category;
        public ItemSubCategory SubCategory;
        public ItemFamily Family;
        public int Tier;      // 物品 ID 的阶级（解析不到 = 0）
        public int Enchant;   // 物品 ID 的附魔（无 @N = 0）
        public bool Found;    // false = 不在任何分类里（无元数据/非 T 前缀等）

        public string CategoryKey { get { return Category == null ? null : Category.Key; } }
        public string SubKey { get { return SubCategory == null ? null : SubCategory.Key; } }
        public string FamilyKey { get { return Family == null ? null : Family.Family; } }
    }

    /// <summary>
    /// 市场物品分类器（M4 修复 P0-3「关键词猜测误分」）。
    ///
    /// 旧实现用 <see cref="ItemCatalog.CategoryOf"/> 拿 ID 里的关键词猜一级分类，实机误分：
    ///   布/皮头盔（T4_HEAD_CLOTH_SET1 / T4_HEAD_LEATHER_SET1）含 CLOTH/LEATHER → 被猜成「材料」；
    ///   板甲头盔（T4_HEAD_PLATE_SET1）含 PLATE → 被猜成「身体护甲」；
    ///   装甲坐骑（T4_MOUNT_ARMORED_HORSE）含 ARMOR → 被猜成「身体护甲」。
    /// 现在唯一权威来源是 ao-bin-dumps 的 @shopcategory / @shopsubcategory1（与游戏集市菜单同源），
    /// 也就是 <see cref="ItemTaxonomy"/> 建树用的同一份字段；关键词猜测只在**元数据缺失**时兜底，
    /// 且结果带 FromMetadata=false，UI 必须标注「回退」，绝不冒充数据分类。
    /// </summary>
    public static class MarketItemClassifier
    {
        /// <summary>回退 key 前缀：保证回退结果永远不会碰巧等于某个 taxonomy 一级 key（如 head）。</summary>
        public const string FallbackPrefix = "fallback:";

        /// <summary>按元数据分类；元数据缺失回退关键词并标记。uniqueName 可带 @附魔后缀。</summary>
        public static MarketItemClass Classify(ItemIndex index, ItemTaxonomy tax, string uniqueName)
        {
            var result = new MarketItemClass();
            string baseId = MarketDb.BaseOf(uniqueName);
            if (string.IsNullOrEmpty(baseId))
            {
                result.CategoryKey = FallbackPrefix + "其他";
                result.CategoryLabel = "其他";
                result.FromMetadata = false;
                result.InTaxonomy = false;
                return result;
            }

            ItemIndexEntry entry = null;
            if (index != null) index.TryGetEntry(baseId, out entry);

            if (entry != null && !string.IsNullOrEmpty(entry.ShopCategory))
            {
                string cat = NormalizeKey(entry.ShopCategory);
                string sub = NormalizeKey(entry.ShopSubCategory1);
                if (sub.Length == 0) sub = "other";

                result.CategoryKey = cat;
                result.SubKey = sub;
                result.FromMetadata = true;
                result.InTaxonomy = true;

                ItemCategory category = tax == null ? null : tax.Find(cat);
                result.CategoryLabel = category != null
                    ? category.DisplayName
                    : ItemTaxonomy.HumanizeKey(cat);

                result.SubLabel = ItemTaxonomy.HumanizeKey(sub);
                if (category != null)
                {
                    for (int i = 0; i < category.SubCategories.Count; i++)
                    {
                        if (category.SubCategories[i].Key != sub) continue;
                        result.SubLabel = category.SubCategories[i].DisplayName;
                        break;
                    }
                }
                return result;
            }

            // 元数据缺失：回退旧关键词猜测（显式标记，绝不当作数据分类）
            string fallback = ItemCatalog.CategoryOf(baseId);
            if (string.IsNullOrEmpty(fallback)) fallback = "其他";
            result.CategoryKey = FallbackPrefix + fallback;
            result.CategoryLabel = fallback;
            result.SubKey = null;
            result.SubLabel = null;
            result.FromMetadata = false;
            result.InTaxonomy = false;
            return result;
        }

        /// <summary>
        /// 一级/二级筛选判定（key 为空/「全部」= 不过滤）。
        /// 注意：选了分类时，无元数据物品（FromMetadata=false）**一律不匹配**——它们的分类是猜的，
        /// 放进用户明确选择的分类里就是把猜测冒充事实。
        /// </summary>
        public static bool Passes(MarketItemClass cls, string categoryKey, string subKey)
        {
            bool wantCategory = !string.IsNullOrEmpty(categoryKey);
            bool wantSub = !string.IsNullOrEmpty(subKey);
            if (cls == null) return !wantCategory && !wantSub;
            if (!wantCategory && !wantSub) return true;
            if (wantCategory && (!cls.FromMetadata || cls.CategoryKey != categoryKey)) return false;
            if (wantSub && (!cls.FromMetadata || cls.SubKey != subKey)) return false;
            return true;
        }

        /// <summary>
        /// 在三级树里定位某个 uniqueName（搜索命中后用）。
        /// 返回 Found=true 时 Category/SubCategory/Family 均非空；否则调用方走回退展示。
        /// </summary>
        public static LocatedTaxonomyItem Locate(ItemTaxonomy tax, string uniqueName)
        {
            var located = new LocatedTaxonomyItem();
            if (tax == null || string.IsNullOrEmpty(uniqueName)) return located;

            string baseId = MarketDb.BaseOf(uniqueName);
            located.Tier = ItemTaxonomy.ParseTier(baseId);
            located.Enchant = ItemCatalog.EnchantOf(uniqueName);

            for (int c = 0; c < tax.Categories.Count; c++)
            {
                var category = tax.Categories[c];
                for (int s = 0; s < category.SubCategories.Count; s++)
                {
                    var sub = category.SubCategories[s];
                    for (int f = 0; f < sub.Families.Count; f++)
                    {
                        var family = sub.Families[f];
                        if (family.Family != ItemTaxonomy.StripTier(baseId)) continue;
                        // 族键相同即同一件东西；BaseId 校验防同名不同类串位
                        bool same = false;
                        for (int v = 0; v < family.Variants.Count; v++)
                        {
                            if (family.Variants[v].BaseId == baseId) { same = true; break; }
                        }
                        if (!same) continue;
                        located.Category = category;
                        located.SubCategory = sub;
                        located.Family = family;
                        located.Found = true;
                        return located;
                    }
                }
            }
            return located;
        }

        private static string NormalizeKey(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Trim().ToLowerInvariant();
        }
    }
}
