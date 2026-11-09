#nullable disable
using System;
using System.Collections.Generic;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// 采集资源分类（数据驱动，不做 ID 区间猜测）。
    ///
    /// 实机确认的数据点（见 HANDOFF / 实际抓包）：
    ///   T4.0 矿石 = 1000，T5.1 矿石 = 1006  ⇒ 矿石：tier 每级 +1、附魔每级 +5。
    /// 旧实现把非矿石也按「T4.0 基础 ID 依次 +5」假设成 1005/1010/1015/1020，
    /// 其中 1020~1024 与矿石 4 级附魔 ID 直接重叠（同一数字既可能是石头又可能是 T4.4 矿石），
    /// 归类不可靠。现改为：
    ///   1) 数字 ID → uniqueName（ItemDatabase，抓包物品索引的权威映射）；
    ///   2) uniqueName 的「阶级_资源词根」判断资源类型（ORE/FIBER/WOOD/HIDE/ROCK）；
    ///   3) 有 ItemIndex 元数据（@shopcategory/@shopsubcategory1）时用它确认/否决：
    ///      - 元数据明确是 crafting/resources ⇒ 按词根归类；
    ///      - 元数据明确不是采集资源（如 bags/weapons）⇒ 未分类；
    ///      - 元数据缺失或未知 ⇒ 非矿石一律未分类（不臆造）；矿石是唯一实机确认类别，可兜底。
    ///   4) 完全没有 uniqueName 映射（物品索引数据缺失）时，只用「实机确认」的矿石数字规律兜底。
    /// 判不出来的返回 Unknown，由调用方归「未分类（其他）」并计数。
    /// </summary>
    public static class GatherCategories
    {
        public enum ResourceType
        {
            Ore,      // 矿石
            Fiber,    // 纤维
            Wood,     // 木材
            Hide,     // 皮革
            Stone,    // 石头
            Unknown   // 未知/未分类
        }

        /// <summary>矿石 T4.0 的确认数字 ID（实机抓包）。</summary>
        public const int OreT4BaseId = 1000;

        /// <summary>已确认的矿石数字 ID 上界（T8.4 = 1000 + 4 + 4×5）。</summary>
        private const int OreMaxOffset = 4 + 4 * 5;

        public const string CategoryOre = "矿石";
        public const string CategoryFiber = "纤维";
        public const string CategoryWood = "木材";
        public const string CategoryHide = "皮革";
        public const string CategoryStone = "石头";
        /// <summary>无法从数据判定类别时的归集桶（不臆造类别，只计数展示为「未分类」）。</summary>
        public const string CategoryUnknown = "其他";

        /// <summary>uniqueName 资源词根 → 类别。ROCK 是游戏里石头的 uniqueName 词根。</summary>
        private static readonly Dictionary<string, ResourceType> _resourceTokens =
            new Dictionary<string, ResourceType>(StringComparer.OrdinalIgnoreCase)
            {
                { "ORE", ResourceType.Ore },
                { "FIBER", ResourceType.Fiber },
                { "WOOD", ResourceType.Wood },
                { "HIDE", ResourceType.Hide },
                { "ROCK", ResourceType.Stone },
            };

        /// <summary>
        /// 测试注入：数字 ID → uniqueName 解析器；null = 生产路径走 ItemDatabase。
        /// 只在测试工程（把 src/Core 直接编进同一程序集）里可见，生产代码不设置它。
        /// </summary>
        internal static Func<int, string> UniqueNameResolverForTests = null;

        private enum MetadataKind
        {
            /// <summary>没有 ItemIndex 元数据可用来判断。</summary>
            Unavailable,
            /// <summary>元数据明确表示这是采集资源（crafting / resources）。</summary>
            Resource,
            /// <summary>元数据明确表示这不是采集资源（如 bags / weapons）。</summary>
            NotResource
        }

        /// <summary>数字 ID → 采集资源类别；判不出来返回 Unknown（调用方归「未分类」）。</summary>
        public static ResourceType GetResourceType(int itemId)
        {
            if (itemId <= 0) return ResourceType.Unknown;

            string uniqueName = ResolveUniqueName(itemId);
            if (!string.IsNullOrEmpty(uniqueName))
            {
                ResourceType byName = ClassifyUniqueName(uniqueName);
                if (byName == ResourceType.Unknown) return ResourceType.Unknown;

                switch (MetadataVerdictFor(uniqueName))
                {
                    case MetadataKind.Resource:
                        return byName;
                    case MetadataKind.NotResource:
                        // 元数据明确说不是采集资源 → 以元数据为准，绝不硬套资源类别
                        return ResourceType.Unknown;
                    default:
                        // 元数据缺失/未知：非矿石归未分类（不臆造）；矿石有实机确认数字规律兜底
                        return byName == ResourceType.Ore ? ResourceType.Ore : ResourceType.Unknown;
                }
            }

            // 物品索引数据不可用（数据缺失）→ 仅保留实机确认的矿石数字规律
            if (!UniqueNameSourceAvailable &&
                TryParseConfirmedOreId(itemId, out _, out _))
            {
                return ResourceType.Ore;
            }

            return ResourceType.Unknown;
        }

        /// <summary>
        /// 只按 uniqueName 判断资源词根（纯字符串，不查任何外部数据）。
        /// 格式：T{tier}_{ORE|FIBER|WOOD|HIDE|ROCK}[@enchant]，如 T5_ORE@1 / T8_ROCK。
        /// 非采集物品（T4_BAG、T4_2H_BOW、T4_METALBAR…）返回 Unknown。
        /// </summary>
        public static ResourceType ClassifyUniqueName(string uniqueName)
        {
            if (string.IsNullOrEmpty(uniqueName)) return ResourceType.Unknown;

            string baseId = uniqueName.Split('@')[0];
            string[] parts = baseId.Split('_');
            if (parts.Length < 2) return ResourceType.Unknown;

            int tier;
            if (!TryParseTierToken(parts[0], out tier)) return ResourceType.Unknown;

            ResourceType type;
            return _resourceTokens.TryGetValue(parts[1], out type) ? type : ResourceType.Unknown;
        }

        /// <summary>类别中文名（Unknown → 「其他」，UI 展示为「未分类」）。</summary>
        public static string CategoryName(ResourceType type)
        {
            switch (type)
            {
                case ResourceType.Ore: return CategoryOre;
                case ResourceType.Fiber: return CategoryFiber;
                case ResourceType.Wood: return CategoryWood;
                case ResourceType.Hide: return CategoryHide;
                case ResourceType.Stone: return CategoryStone;
                default: return CategoryUnknown;
            }
        }

        /// <summary>数字 ID → 类别中文名；判不出来返回「其他」（未分类桶）。</summary>
        public static string GetResourceName(int itemId)
        {
            return CategoryName(GetResourceType(itemId));
        }

        /// <summary>
        /// 阶级/附魔展示（如 T5.1）。优先从 uniqueName 解析；没有 uniqueName 时只有
        /// 实机确认的矿石数字规律可信（T4 = 1000 / T5.1 = 1006）。判不出来返回空串。
        /// </summary>
        public static string GetTier(int itemId)
        {
            string uniqueName = ResolveUniqueName(itemId);
            if (!string.IsNullOrEmpty(uniqueName))
            {
                string tier = TierOfUniqueName(uniqueName);
                if (tier != null) return tier;
            }

            if (!UniqueNameSourceAvailable)
            {
                int tierNumber, enchant;
                if (TryParseConfirmedOreId(itemId, out tierNumber, out enchant))
                    return FormatTier(tierNumber, enchant);
            }

            return "";
        }

        /// <summary>从 uniqueName 解析 T{阶级}[.{附魔}]；不是 T 前缀格式返回 null。</summary>
        public static string TierOfUniqueName(string uniqueName)
        {
            if (string.IsNullOrEmpty(uniqueName)) return null;

            string baseId = uniqueName.Split('@')[0];
            string[] parts = baseId.Split('_');
            int tier;
            if (parts.Length < 1 || !TryParseTierToken(parts[0], out tier)) return null;

            int enchant = 0;
            int at = uniqueName.IndexOf('@');
            if (at >= 0 && at + 1 < uniqueName.Length)
                int.TryParse(uniqueName.Substring(at + 1), out enchant);
            return FormatTier(tier, enchant);
        }

        /// <summary>实机确认的矿石数字 ID 反解：T4=1000，tier 每级 +1、附魔每级 +5。</summary>
        private static bool TryParseConfirmedOreId(int itemId, out int tier, out int enchant)
        {
            tier = 0;
            enchant = 0;
            int offset = itemId - OreT4BaseId;
            if (offset < 0 || offset > OreMaxOffset) return false;
            tier = 4 + (offset % 5);
            enchant = offset / 5;
            return true;
        }

        private static bool TryParseTierToken(string token, out int tier)
        {
            tier = 0;
            if (string.IsNullOrEmpty(token) || token.Length < 2) return false;
            if (token[0] != 'T' && token[0] != 't') return false;
            return int.TryParse(token.Substring(1), out tier) && tier >= 1 && tier <= 8;
        }

        private static string FormatTier(int tier, int enchant)
        {
            return enchant > 0 ? "T" + tier + "." + enchant : "T" + tier;
        }

        private static bool UniqueNameSourceAvailable
        {
            get { return UniqueNameResolverForTests != null || ItemDatabase.IsLoaded; }
        }

        private static string ResolveUniqueName(int itemId)
        {
            var resolver = UniqueNameResolverForTests;
            if (resolver != null) return resolver(itemId);
            return ItemDatabase.GetUniqueName(itemId);
        }

        /// <summary>
        /// 查 ItemIndex 元数据（@shopcategory/@shopsubcategory1/@shopsubcategory2）给采集分类投票：
        /// 明确资源 → Resource；明确其他商品类目 → NotResource；数据缺失/该物品无分类 → Unavailable。
        /// </summary>
        private static MetadataKind MetadataVerdictFor(string uniqueName)
        {
            ItemIndex index = ItemIndex.Default;
            if (index == null) return MetadataKind.Unavailable;

            ItemIndexEntry entry;
            if (!index.TryGetEntry(uniqueName, out entry) || entry == null)
                return MetadataKind.Unavailable;
            return MetadataVerdictOf(entry);
        }

        /// <summary>供测试/诊断直接对条目投票（不关心 uniqueName 解析）。</summary>
        internal static string MetadataVerdictName(ItemIndexEntry entry)
        {
            return MetadataVerdictOf(entry).ToString();
        }

        private static MetadataKind MetadataVerdictOf(ItemIndexEntry entry)
        {
            if (entry == null) return MetadataKind.Unavailable;
            string category = entry.ShopCategory;
            string sub1 = entry.ShopSubCategory1;
            string sub2 = entry.ShopSubCategory2;

            // 分类字段全缺 → 元数据缺失，不能据此判断
            if (string.IsNullOrEmpty(category) && string.IsNullOrEmpty(sub1) && string.IsNullOrEmpty(sub2))
                return MetadataKind.Unavailable;

            if (ContainsIgnoreCase(category, "crafting") ||
                ContainsIgnoreCase(category, "resource") ||
                ContainsIgnoreCase(sub1, "resource") ||
                ContainsIgnoreCase(sub2, "resource"))
            {
                return MetadataKind.Resource;
            }

            return MetadataKind.NotResource;
        }

        private static bool ContainsIgnoreCase(string text, string needle)
        {
            return !string.IsNullOrEmpty(text) &&
                   text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
