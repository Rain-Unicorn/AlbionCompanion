#nullable disable // 与仓库既有 Core 文件风格一致
using System;
using System.Collections.Generic;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// 品质口径唯一入口（P0 修复：off-by-one）。
    ///
    /// AODP 的 quality 字段是 **1..5**，语义固定为：
    ///   1 = 普通 / 2 = 良好 / 3 = 杰出 / 4 = 精良 / 5 = 大师。
    /// 旧实现下拉是「全部 + 普通/良好/杰出/精良/史诗/传说」6 档，却按 <c>SelectedIndex - 1</c>
    /// 与 1..5 比较：选「普通」得到 0 → 恒空；其它档位整体上移一格（选「良好」实际过滤 quality=1）。
    ///
    /// 这里把「下拉索引 ↔ AODP quality ↔ 中文标签」收敛成纯函数，UI 只许调用本类，
    /// 测试直接钉死映射，避免再出现第二套偏移。
    /// </summary>
    public static class MarketQuality
    {
        public const int Min = 1;
        public const int Max = 5;

        /// <summary>quality 1..5 的中文标签（下标 = quality-1）。</summary>
        public static readonly string[] Labels = { "普通", "良好", "杰出", "精良", "大师" };

        public static bool IsValid(int quality)
        {
            return quality >= Min && quality <= Max;
        }

        /// <summary>quality → 中文标签；非法值返回「未知品质（N）」，不假装是普通。</summary>
        public static string LabelOf(int quality)
        {
            if (IsValid(quality)) return Labels[quality - Min];
            return "未知品质（" + quality + "）";
        }

        /// <summary>带数字的展示文案（便于和 AODP 字段对账）："1 · 普通"。</summary>
        public static string DisplayOf(int quality)
        {
            if (!IsValid(quality)) return LabelOf(quality);
            return quality + " · " + LabelOf(quality);
        }

        /// <summary>
        /// 下拉索引 → 过滤品质。0 = 全部（返回 0，表示不按品质过滤）；1..5 **直通** AODP quality；
        /// 越界回退 0（全部），绝不猜一个品质。
        /// </summary>
        public static int FromDropdownIndex(int index)
        {
            return IsValid(index) ? index : 0;
        }

        /// <summary>过滤品质 → 下拉索引（0 = 全部）。</summary>
        public static int ToDropdownIndex(int quality)
        {
            return IsValid(quality) ? quality : 0;
        }

        /// <summary>下拉选项：全部 + 5 档（与 FromDropdownIndex 同源，长度恒为 6）。</summary>
        public static string[] DropdownItems()
        {
            var list = new List<string>();
            list.Add("全部");
            for (int q = Min; q <= Max; q++) list.Add(DisplayOf(q));
            return list.ToArray();
        }

        /// <summary>品质筛选判定（filterQuality = 0 表示全部）。</summary>
        public static bool Passes(int quality, int filterQuality)
        {
            if (filterQuality <= 0) return true;
            return IsValid(quality) && quality == filterQuality;
        }
    }
}
