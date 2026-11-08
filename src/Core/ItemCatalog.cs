using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlbionCompanion.Core
{
    public class ItemDef
    {
        public string UniqueName;
        public string DisplayName;
    }

    /// <summary>本地中文名称缓存：{ 物品ID -> 中文名 }，支持模糊子串匹配。</summary>
    public class NameCache
    {
        public Dictionary<string, string> Map = new Dictionary<string, string>();

        public void Add(string id, string zh)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(zh)) return;
            string baseId = id.Split('@')[0];
            if (!Map.ContainsKey(baseId)) Map[baseId] = zh;
        }

        public List<string> FuzzyIds(string token, int limit)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(token)) return list;
            string needle = token.Trim();
            foreach (var kv in Map)
            {
                if (kv.Value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    list.Add(kv.Key);
                    if (list.Count >= limit) break;
                }
            }
            return list;
        }

        public static NameCache Load(string path)
        {
            var cache = new NameCache();
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (raw != null)
                        foreach (var kv in raw)
                            cache.Map[kv.Key] = kv.Value;
                }
            }
            catch { }
            return cache;
        }

        public void Save(string path)
        {
            try
            {
                var json = JsonSerializer.Serialize(Map);
                File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    /// <summary>
    /// 物品目录：把中文物品名解析成 UniqueName，并查询官方渲染服务器上的图标。
    /// 数据源是 albion-online-data.com 自带的 /items 检索页（支持 ZH-CN 显示名，需全名匹配）。
    /// </summary>
    public static class ItemCatalog
    {
        public const string LangZh = "ZH-CN";

        static ItemCatalog()
        {
            // 与 MarketClient 相同：显式启用 TLS 1.2
            try
            {
                ServicePointManager.SecurityProtocol =
                    (SecurityProtocolType)3072 /* Tls12 */ |
                    (SecurityProtocolType)768 /* Tls11  */ |
                    SecurityProtocolType.Tls;
            }
            catch { }
        }

        public static string BuildSearchUrl(string query, string lang)
        {
            return "https://www.albion-online-data.com/items?q="
                   + Uri.EscapeDataString(query ?? "")
                   + "&lang=" + Uri.EscapeDataString(lang ?? LangZh);
        }

        public static string BuildIconUrl(string baseId, int quality)
        {
            return "https://render.albiononline.com/v1/item/" + baseId
                   + ".png?quality=" + Math.Max(1, quality);
        }

        public static List<ItemDef> ParseSearchHtml(string html)
        {
            var list = new List<ItemDef>();
            if (string.IsNullOrEmpty(html)) return list;

            var re = new Regex(
                "<td class=\"items-table__unique\"><code>([^<]+)</code></td>\\s*" +
                "<td class=\"items-table__name\">\\s*([^<]*?)\\s*</td>",
                RegexOptions.Singleline);

            foreach (Match m in re.Matches(html))
            {
                list.Add(new ItemDef
                {
                    UniqueName = m.Groups[1].Value.Trim(),
                    DisplayName = m.Groups[2].Value.Trim()
                });
            }
            return list;
        }

        public static List<ItemDef> Search(string query, string lang, int timeoutSeconds)
        {
            string url = BuildSearchUrl(query, lang);
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = timeoutSeconds * 1000;
            request.UserAgent = "AlbionCompanion/1.0";
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return ParseSearchHtml(reader.ReadToEnd());
            }
        }

        public static List<string> SplitTerms(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (var raw in text.Split(',', '，'))
            {
                string token = raw.Trim();
                if (token.Length > 0) list.Add(token);
            }
            return list;
        }

        /// <summary>纯 ASCII 且含下划线或附魔符号，视为物品 UniqueName，原样查询。</summary>
        public static bool LooksLikeItemId(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            foreach (char c in token)
                if (c > 127) return false;
            return token.IndexOf('_') >= 0 || token.IndexOf('@') >= 0;
        }

        /// <summary>从 UniqueName 开头解析等阶 T1..T8，解析不到返回 0。</summary>
        public static int TierOf(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;
            var m = Regex.Match(itemId, "^T([1-8])");
            int tier;
            return m.Success && int.TryParse(m.Groups[1].Value, out tier) ? tier : 0;
        }

        /// <summary>解析附魔 @1..@4，无则 0。</summary>
        public static int EnchantOf(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;
            var m = Regex.Match(itemId, "@([0-9])");
            int enchant;
            return m.Success && int.TryParse(m.Groups[1].Value, out enchant) ? enchant : 0;
        }

        /// <summary>从 UniqueName 关键词粗分类别，对齐游戏集市的分类。</summary>
        public static string CategoryOf(string itemId)
        {
            string id = (itemId ?? "").ToUpperInvariant();
            if (id.Length == 0) return "其他";

            // 背包
            if (id.IndexOf("BAG") >= 0 || id.IndexOf("BACKPACK") >= 0) return "背包";
            // 披风
            if (id.IndexOf("CAPE") >= 0) return "披风";
            // 头部
            if (id.IndexOf("HELMET") >= 0 || id.IndexOf("HOOD") >= 0) return "头部护甲";
            // 足部
            if (id.IndexOf("SHOES") >= 0 || id.IndexOf("BOOTS") >= 0) return "足部护甲";
            // 身体护甲（胸甲/夹克/长袍）
            if (id.IndexOf("ARMOR") >= 0 || id.IndexOf("JACKET") >= 0 ||
                id.IndexOf("ROBE") >= 0 || id.IndexOf("PLATE") >= 0) return "身体护甲";
            // 副手
            if (id.IndexOf("OFFHAND") >= 0 || id.IndexOf("SHIELD") >= 0 ||
                id.IndexOf("TORCH") >= 0 || id.IndexOf("FOCUS") >= 0 ||
                id.IndexOf("JOURNAL") >= 0) return "副手";
            // 采集工具
            if (id.IndexOf("TOOL_") >= 0 || id.IndexOf("FISHINGROD") >= 0 ||
                id.IndexOf("SICKLE") >= 0 || id.IndexOf("PICKAXE") >= 0 ||
                id.IndexOf("HATCHET") >= 0) return "采集装备";
            // 坐骑
            if (id.IndexOf("MOUNT") >= 0 || id.IndexOf("HORSE") >= 0 ||
                id.IndexOf("OX") >= 0 || id.IndexOf("DIREWOLF") >= 0 ||
                id.IndexOf("COUGAR") >= 0 || id.IndexOf("MULE") >= 0 ||
                id.IndexOf("GIANTSTAG") >= 0 || id.IndexOf("DEER") >= 0 ||
                id.IndexOf("MAMMOTH") >= 0 || id.IndexOf("DWARF") >= 0) return "坐骑";
            // 消耗品
            if (id.IndexOf("POTION") >= 0 || id.IndexOf("POISON") >= 0 ||
                id.IndexOf("FOOD") >= 0 || id.IndexOf("MEAL") >= 0 ||
                id.IndexOf("STEW") >= 0 || id.IndexOf("FISH") >= 0 ||
                id.IndexOf("DINNER") >= 0 || id.IndexOf("SANDWICH") >= 0 ||
                id.IndexOf("PIE") >= 0 || id.IndexOf("SALAD") >= 0) return "消耗品";
            // 家具
            if (id.IndexOf("FURNITURE") >= 0 || id.IndexOf("KITCHEN") >= 0 ||
                id.IndexOf("WORKSHOP") >= 0) return "家具";
            // 神器（Artifact / 秘境装备）
            if (id.IndexOf("ARTIFACT") >= 0 || id.IndexOf("KEEPER") >= 0 ||
                id.IndexOf("DEMON") >= 0 || id.IndexOf("AVALON") >= 0 ||
                id.IndexOf("MIST") >= 0 || id.IndexOf("HELL") >= 0 ||
                id.IndexOf("LURONITE") >= 0 || id.IndexOf("FORGELING") >= 0) return "神器";
            // 材料
            if (id.IndexOf("ORE") >= 0 || id.IndexOf("WOOD") >= 0 ||
                id.IndexOf("FIBER") >= 0 || id.IndexOf("HIDE") >= 0 ||
                id.IndexOf("ROCK") >= 0 || id.IndexOf("STONE") >= 0 ||
                id.IndexOf("METALBAR") >= 0 || id.IndexOf("CLOTH") >= 0 ||
                id.IndexOf("LEATHER") >= 0 || id.IndexOf("PLANK") >= 0) return "材料";
            // 武器（各类武器关键词或 1H_/2H_ 前缀）
            if (id.IndexOf("SWORD") >= 0 || id.IndexOf("AXE") >= 0 || id.IndexOf("MACE") >= 0 ||
                id.IndexOf("HAMMER") >= 0 || id.IndexOf("BOW") >= 0 || id.IndexOf("STAFF") >= 0 ||
                id.IndexOf("DAGGER") >= 0 || id.IndexOf("SPEAR") >= 0 || id.IndexOf("QUARTERSTAFF") >= 0 ||
                id.IndexOf("FIRE") >= 0 || id.IndexOf("ARCANE") >= 0 || id.IndexOf("CURSED") >= 0 ||
                id.IndexOf("HOLY") >= 0 || id.IndexOf("NATURE") >= 0 || id.IndexOf("FROST") >= 0 ||
                id.IndexOf("1H_") >= 0 || id.IndexOf("2H_") >= 0) return "武器";
            return "其他";
        }

        /// <summary>三级联动分类表：L1 类别 → L2 子类 → L3 具体武器（对齐游戏集市菜单）。</summary>
        public static class Taxonomy
        {
            /// <summary>L1 → L2 列表（第一项恒为"全部"）。</summary>
            private static readonly Dictionary<string, string[]> SubMap = new Dictionary<string, string[]>
            {
                { "武器", new[] { "全部", "弓", "十字弓", "战斧", "匕首", "锤子", "战斗手套",
                                  "战锤", "铁头棒", "长矛", "剑", "奥术法杖", "诅咒法杖",
                                  "火焰法杖", "天霜法杖", "神圣法杖", "自然法杖", "幻形者法杖" } },
                { "身体护甲", new[] { "全部", "板甲", "皮甲", "布甲" } },
                { "头部护甲", new[] { "全部", "板甲", "皮甲", "布甲" } },
                { "足部护甲", new[] { "全部", "板甲", "皮甲", "布甲" } },
                { "副手", new[] { "全部", "盾牌", "火炬", "法典", "其他副手" } },
                { "披风", new[] { "全部" } },
                { "背包", new[] { "全部", "背包", "皮包" } },
                { "坐骑", new[] { "全部", "马", "牛", "狼", "鹿", "其他" } },
                { "消耗品", new[] { "全部", "药水", "食物", "鱼类" } },
                { "采集装备", new[] { "全部", "采矿", "伐木", "织布", "剥皮", "钓鱼" } },
                { "制作", new[] { "全部" } },
                { "材料", new[] { "全部", "矿石", "木材", "纤维", "皮革", "石头", "金属锭", "木板", "布料" } },
                { "家具", new[] { "全部" } },
                { "神器", new[] { "全部" } },
                { "农耕", new[] { "全部" } },
                { "浮华物品", new[] { "全部" } },
                { "其他", new[] { "全部" } },
            };

            /// <summary>L1/L2 → L3 具体物品中文名列表（不含"全部"）。</summary>
            private static readonly Dictionary<string, string[]> L3Map = new Dictionary<string, string[]>
            {
                { "武器/弓", new[] { "弓", "长弓", "战弓", "低语之弓", "哀嚎之弓", "巴顿弓", "破雾之弓", "天行者之弓" } },
                { "武器/十字弓", new[] { "十字弓", "重型十字弓", "轻型十字弓", "怒怨连弩", "霹雷连弩", "围城弓", "塑能炮", "弧光爆破者" } },
                { "武器/战斧", new[] { "战斧", "巨斧", "战戟", "腐蚀召唤神斧", "赤炎镰刀", "熊爪神斧", "裂域斧", "水晶收割者" } },
                { "武器/匕首", new[] { "匕首", "双匕首", "钩爪", "血刃", "恶魔之牙", "冥使匕首", "逼怒拳刃", "双子杀手" } },
                { "武器/锤子", new[] { "锤子", "长锤", "巨锤", "撼墓之锤", "锻造之锤", "守林木桩", "正义之手大锤", "雷霆战锤" } },
            };

            /// <summary>L1+L2 → 用于在物品 ID 中匹配的大写关键词；null 表示不限制。</summary>
            private static readonly Dictionary<string, string> KeywordMap = new Dictionary<string, string>
            {
                { "武器/弓", "BOW" },
                { "武器/十字弓", "CROSSBOW" },
                { "武器/战斧", "AXE" },
                { "武器/匕首", "DAGGER" },
                { "武器/锤子", "MACE" },
                { "武器/战斗手套", "FISTS" },
                { "武器/战锤", "HAMMER" },
                { "武器/铁头棒", "QUARTERSTAFF" },
                { "武器/长矛", "SPEAR" },
                { "武器/剑", "SWORD" },
                { "武器/奥术法杖", "ARCANESTAFF" },
                { "武器/诅咒法杖", "CURSED" },
                { "武器/火焰法杖", "FIRESTAFF" },
                { "武器/天霜法杖", "FROSTSTAFF" },
                { "武器/神圣法杖", "HOLYSTAFF" },
                { "武器/自然法杖", "NATURESTAFF" },
                { "身体护甲/板甲", "PLATE" },
                { "身体护甲/皮甲", "LEATHER" },
                { "身体护甲/布甲", "CLOTH" },
                { "头部护甲/板甲", "PLATE" },
                { "头部护甲/皮甲", "LEATHER" },
                { "头部护甲/布甲", "CLOTH" },
                { "足部护甲/板甲", "PLATE" },
                { "足部护甲/皮甲", "LEATHER" },
                { "足部护甲/布甲", "CLOTH" },
                { "背包/背包", "BAG" },
                { "背包/皮包", "BACKPACK" },
                { "坐骑/马", "HORSE" },
                { "坐骑/牛", "OX" },
                { "坐骑/狼", "WOLF" },
                { "坐骑/鹿", "STAG" },
                { "消耗品/药水", "POTION" },
                { "消耗品/食物", "FOOD" },
                { "消耗品/鱼类", "FISH" },
                { "采集装备/采矿", "PICKAXE" },
                { "采集装备/伐木", "HATCHET" },
                { "采集装备/织布", "SICKLE" },
                { "材料/矿石", "ORE" },
                { "材料/木材", "WOOD" },
                { "材料/纤维", "FIBER" },
                { "材料/皮革", "HIDE" },
                { "材料/石头", "ROCK" },
                { "材料/金属锭", "METALBAR" },
                { "材料/木板", "PLANK" },
                { "材料/布料", "CLOTH" },
            };

            public static string[] L1List()
            {
                var list = new List<string> { "全部" };
                var seen = new HashSet<string>();
                foreach (var k in SubMap.Keys) if (seen.Add(k)) list.Add(k);
                if (OverrideL1 != null) foreach (var k in OverrideL1) if (seen.Add(k)) list.Add(k);
                return list.ToArray();
            }

            public static string[] L2List(string l1)
            {
                if (string.IsNullOrEmpty(l1) || l1 == "全部") return new[] { "全部" };
                string[] arr;
                if (SubMap.TryGetValue(l1, out arr)) return arr;
                if (OverrideL2 != null && OverrideL2.TryGetValue(l1, out arr)) return arr;
                return new[] { "全部" };
            }

            public static string[] L3List(string l1, string l2)
            {
                if (string.IsNullOrEmpty(l1) || l1 == "全部" || string.IsNullOrEmpty(l2) || l2 == "全部")
                    return new[] { "全部" };
                string key = l1 + "/" + l2;
                var all = new List<string> { "全部" };
                string[] arr;
                if (L3Map.TryGetValue(key, out arr)) all.AddRange(arr);
                if (OverrideL3 != null && OverrideL3.TryGetValue(key, out arr))
                {
                    var seen = new HashSet<string>(all);
                    foreach (var s in arr) if (seen.Add(s)) all.Add(s);
                }
                return all.ToArray();
            }

            /// <summary>外部覆盖（用户在采集 Tab 录入的分类）。</summary>
            public static List<string> OverrideL1;
            public static Dictionary<string, string[]> OverrideL2;
            public static Dictionary<string, string[]> OverrideL3;

            /// <summary>从 JSON 文件加载用户自定义分类。</summary>
            public static void LoadOverride(string path)
            {
                try
                {
                    if (!File.Exists(path)) return;
                    using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return;

                    if (root.TryGetProperty("l1", out var l1Elem) && l1Elem.ValueKind == JsonValueKind.Array)
                    {
                        OverrideL1 = new List<string>();
                        foreach (var o in l1Elem.EnumerateArray())
                            OverrideL1.Add(o.GetString() ?? "");
                    }
                    if (root.TryGetProperty("l2", out var l2Elem) && l2Elem.ValueKind == JsonValueKind.Object)
                    {
                        OverrideL2 = new Dictionary<string, string[]>();
                        foreach (var kv in l2Elem.EnumerateObject())
                        {
                            if (kv.Value.ValueKind == JsonValueKind.Array)
                            {
                                var list = new List<string> { "全部" };
                                foreach (var o in kv.Value.EnumerateArray())
                                    list.Add(o.GetString() ?? "");
                                OverrideL2[kv.Name] = list.ToArray();
                            }
                        }
                    }
                    if (root.TryGetProperty("l3", out var l3Elem) && l3Elem.ValueKind == JsonValueKind.Object)
                    {
                        OverrideL3 = new Dictionary<string, string[]>();
                        foreach (var kv in l3Elem.EnumerateObject())
                        {
                            if (kv.Value.ValueKind == JsonValueKind.Array)
                            {
                                var list = new List<string>();
                                foreach (var o in kv.Value.EnumerateArray())
                                    list.Add(o.GetString() ?? "");
                                OverrideL3[kv.Name] = list.ToArray();
                            }
                        }
                    }
                }
                catch { }
            }

            /// <summary>从完整分类树 JSON 加载（游戏官方三级分类）。</summary>
            public static void LoadFromJson(string path)
            {
                try
                {
                    if (!File.Exists(path)) return;
                    using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Array) return;

                    SubMap.Clear();
                    L3Map.Clear();
                    KeywordMap.Clear();

                    foreach (var l1 in root.EnumerateArray())
                    {
                        if (l1.ValueKind != JsonValueKind.Object) continue;
                        string l1Name = l1.TryGetProperty("name_cn", out var n1) ? n1.GetString() ?? "?" : "?";
                        var l2List = new List<string>();

                        if (!l1.TryGetProperty("children", out var children1) || children1.ValueKind != JsonValueKind.Array)
                        { SubMap[l1Name] = l2List.ToArray(); continue; }
                        foreach (var l2 in children1.EnumerateArray())
                        {
                            if (l2.ValueKind != JsonValueKind.Object) continue;
                            string l2Name = l2.TryGetProperty("name_cn", out var n2) ? n2.GetString() ?? "?" : "?";
                            string l2En = l2.TryGetProperty("name_en", out var ne2) ? ne2.GetString() ?? "" : "";
                            l2List.Add(l2Name);
                            KeywordMap[l1Name + "/" + l2Name] = l2En.ToUpperInvariant();

                            if (l2.TryGetProperty("children", out var children2) && children2.ValueKind == JsonValueKind.Array)
                            {
                                var l3List = new List<string>();
                                foreach (var l3 in children2.EnumerateArray())
                                {
                                    if (l3.ValueKind != JsonValueKind.Object) continue;
                                    l3List.Add(l3.TryGetProperty("name_cn", out var n3) ? n3.GetString() ?? "?" : "?");
                                }
                                L3Map[l1Name + "/" + l2Name] = l3List.ToArray();
                            }
                        }
                        SubMap[l1Name] = l2List.ToArray();
                    }
                }
                catch { }
            }

            /// <summary>返回 L2 对应的 ID 关键词（大写）；"全部"或无映射返回 null。</summary>
            public static string KeywordFor(string l1, string l2)
            {
                if (string.IsNullOrEmpty(l2) || l2 == "全部") return null;
                string key = l1 + "/" + l2;
                string kw;
                return KeywordMap.TryGetValue(key, out kw) ? kw : ""; // ""=已知子类但无关键词→全部不匹配
            }
        }

        /// <summary>离线 tokenize：像物品 ID 的词直通；其余先查本地缓存的模糊匹配。</summary>
        public static List<string> ResolveTermsOffline(string text, Dictionary<string, string> idToName, NameCache cache)
        {
            var seen = new HashSet<string>();
            var result = new List<string>();
            foreach (string token in SplitTerms(text))
            {
                if (LooksLikeItemId(token))
                {
                    string baseId = token.Split('@')[0];
                    if (!seen.Contains(baseId)) { seen.Add(baseId); result.Add(baseId); }
                    continue;
                }
                if (cache != null)
                {
                    foreach (string id in cache.FuzzyIds(token, 8))
                    {
                        if (!seen.Contains(id))
                        {
                            seen.Add(id);
                            result.Add(id);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 把用户输入解析成实际要查的物品 ID 列表。中文词先在本地缓存里模糊匹配，
        /// 未命中再走 /items 全名检索；检索结果会写回 idToName（由调用方并入缓存）。
        /// </summary>
        public static List<string> ResolveTerms(string text, Dictionary<string, string> idToName,
                                                NameCache cache, int timeoutSeconds)
        {
            var result = new List<string>();
            var seen = new HashSet<string>();

            foreach (string token in SplitTerms(text))
            {
                if (LooksLikeItemId(token))
                {
                    string baseId = token.Split('@')[0];
                    if (!seen.Contains(baseId)) { seen.Add(baseId); result.Add(baseId); }
                    continue;
                }

                bool localHit = false;
                if (cache != null)
                {
                    foreach (string id in cache.FuzzyIds(token, 8))
                    {
                        if (!seen.Contains(id))
                        {
                            seen.Add(id);
                            result.Add(id);
                        }
                        localHit = true;
                    }
                }
                if (localHit) continue;

                List<ItemDef> found;
                try { found = Search(token, LangZh, timeoutSeconds); }
                catch { found = new List<ItemDef>(); }

                int taken = 0;
                foreach (ItemDef def in found)
                {
                    string baseId = (def.UniqueName ?? "").Split('@')[0];
                    if (baseId.Length == 0) continue;
                    if (!seen.Contains(baseId))
                    {
                        seen.Add(baseId);
                        result.Add(baseId);
                    }
                    if (!string.IsNullOrEmpty(def.DisplayName) && !idToName.ContainsKey(baseId))
                        idToName[baseId] = def.DisplayName;
                    if (++taken >= 5) break;
                }
            }
            return result;
        }
    }
}
