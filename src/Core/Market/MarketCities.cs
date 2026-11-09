#nullable disable // 与仓库既有 Core 文件风格一致
using System;
using System.Collections.Generic;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// 市场城市口径唯一入口（P0：黑市城市 + 缓存键城市别名）。
    ///
    /// ① 亚服六城 = Caerleon / Bridgewatch / Martlock / Thetford / Fort Sterling / Lymhurst；
    /// ② **Black Market（凯尔莱恩黑市）是第 7 个可查城市**：跑商的黑市腿（普通城收 → 黑市卖）
    ///    依赖行情里有黑市报价，所以市场页/自动更新的城市数组必须带上它，否则永远算不出黑市路线；
    /// ③ AODP 城市参数区分大小写/空格，缓存键必须与请求参数同源规范化（Trim + 别名归一），
    ///    否则 "fort sterling" 与 "Fort Sterling" 会各占一份缓存、小写请求永久 miss。
    ///
    /// 未知城市不做猜测：只 Trim/合并空格后原样返回（AODP 自己会拒绝或返回空），绝不改名。
    /// </summary>
    public static class MarketCities
    {
        /// <summary>凯尔莱恩黑市（AODP 使用的正式 location 名）。</summary>
        public const string BlackMarket = "Black Market";

        /// <summary>亚服六城（顺序固定，与 QuoteTableModel.AsiaCities 一致）。</summary>
        public static readonly string[] AsiaSix =
        {
            "Caerleon", "Bridgewatch", "Martlock", "Thetford", "Fort Sterling", "Lymhurst"
        };

        /// <summary>市场页 / 自动更新拉取的城市全集：六城 + 黑市。</summary>
        public static readonly string[] All =
        {
            "Caerleon", "Bridgewatch", "Martlock", "Thetford", "Fort Sterling", "Lymhurst", BlackMarket
        };

        /// <summary>城市短名（仅用于底注/表格窄列；完整名用于 AODP 请求）。</summary>
        public static string ShortName(string city)
        {
            string c = Normalize(city);
            switch (c)
            {
                case "Caerleon": return "凯城";
                case "Bridgewatch": return "桥望";
                case "Martlock": return "马丁";
                case "Thetford": return "塞福";
                case "Fort Sterling": return "斯特";
                case "Lymhurst": return "林堡";
                case BlackMarket: return "黑市";
                default: return string.IsNullOrEmpty(city) ? "—" : city;
            }
        }

        // 别名表：键为「去空格 + 小写」形态，值为规范城市名。中文名兼容历史设置/用户输入。
        private static readonly Dictionary<string, string> AliasToCanonical = BuildAliases();

        private static Dictionary<string, string> BuildAliases()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string canonical, params string[] aliases)
            {
                map[NormalizeKey(canonical)] = canonical;
                if (aliases == null) return;
                foreach (var a in aliases) map[NormalizeKey(a)] = canonical;
            }

            Add("Caerleon", "kaerleon", "凯城", "卡拉", "卡利恩");
            Add("Bridgewatch", "bridge watch", "桥望", "桥望城");
            Add("Martlock", "mart lock", "马丁", "马特", "马特洛克");
            Add("Thetford", "thet ford", "塞福", "塞特", "塞特福德");
            Add("Fort Sterling", "fortsterling", "fort", "斯特", "斯特林");
            Add("Lymhurst", "lym hurst", "林堡", "林木", "林赫斯特");
            Add(BlackMarket, "blackmarket", "黑市", "黑市（凯尔莱恩）", "凯尔莱恩黑市");
            return map;
        }

        private static string NormalizeKey(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char ch in raw)
            {
                if (char.IsWhiteSpace(ch)) continue;
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 城市规范化：Trim + 合并内部空白；命中别名表返回规范名，否则返回处理后的原值（不改未知城市）。
        /// 结果用于：缓存键、single-flight 请求参数、城市显示。
        /// </summary>
        public static string Normalize(string city)
        {
            if (string.IsNullOrEmpty(city)) return "";

            // Trim + 合并连续空白（"Fort   Sterling" → "Fort Sterling"）
            var sb = new System.Text.StringBuilder(city.Length);
            bool lastSpace = false;
            bool started = false;
            foreach (char ch in city)
            {
                bool isSpace = char.IsWhiteSpace(ch);
                if (isSpace)
                {
                    if (!started) continue; // 前导空白
                    lastSpace = true;
                    continue;
                }
                if (lastSpace && sb.Length > 0) sb.Append(' ');
                lastSpace = false;
                sb.Append(ch);
                started = true;
            }
            string trimmed = sb.ToString();
            if (trimmed.Length == 0) return "";

            string canonical;
            if (AliasToCanonical.TryGetValue(NormalizeKey(trimmed), out canonical)) return canonical;
            return trimmed;
        }

        /// <summary>黑市判定（别名归一后比较）。</summary>
        public static bool IsBlackMarket(string city)
        {
            return Normalize(city) == BlackMarket;
        }

        /// <summary>规范名全集的一份新数组（调用方可以随意改，不影响共享状态）。</summary>
        public static string[] AllCopy()
        {
            return (string[])All.Clone();
        }
    }
}
