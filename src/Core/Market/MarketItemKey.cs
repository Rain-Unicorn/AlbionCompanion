#nullable disable // 与仓库既有 Core 文件风格一致
using System;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// 物品 ID（uniqueName）规范化唯一入口。
    ///
    /// ① <b>保留附魔</b>：T5_2H_BOW 与 T5_2H_BOW@1 是不同商品，不能折叠（P0：旧实现到处
    ///    <c>Split('@')[0]</c>，导致附魔筛选恒空、附魔变体在本地库互相覆盖）；
    /// ② 规范化 = Trim + ToUpperInvariant：AODP 响应里 item_id 恒为大写，而用户可能输入小写
    ///    （t4_bag）。缓存键/本地库主键必须与响应同源，否则小写查询永久 miss。
    /// </summary>
    public static class MarketItemKey
    {
        /// <summary>查询/缓存/本地库主键用的规范形态（Trim + 大写，保留 @附魔后缀）。</summary>
        public static string Normalize(string itemId)
        {
            return string.IsNullOrEmpty(itemId) ? "" : itemId.Trim().ToUpperInvariant();
        }

        /// <summary>去掉 @附魔后缀的基名（仅用于展示/重量/陈旧分组，**不要**拿它当主键）。</summary>
        public static string BaseOf(string itemId)
        {
            string id = Normalize(itemId);
            int at = id.IndexOf('@');
            return at >= 0 ? id.Substring(0, at) : id;
        }

        /// <summary>附魔等级：@N → N；无后缀或解析失败 = 0。</summary>
        public static int EnchantOf(string itemId)
        {
            string id = Normalize(itemId);
            int at = id.IndexOf('@');
            if (at < 0 || at + 1 >= id.Length) return 0;
            int level;
            return int.TryParse(id.Substring(at + 1), out level) && level >= 0 ? level : 0;
        }

        /// <summary>两个 ID 是否同一商品（规范化后比较）。</summary>
        public static bool Same(string a, string b)
        {
            return Normalize(a) == Normalize(b);
        }

        /// <summary>展示文案：基名 + 附魔后缀（如 T5_2H_BOW +1）。</summary>
        public static string DisplayOf(string itemId)
        {
            string id = Normalize(itemId);
            int enchant = EnchantOf(id);
            return enchant > 0 ? BaseOf(id) + " +" + enchant : id;
        }
    }
}
