#nullable disable // 与仓库既有 Core 文件风格一致
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// 三级联动筛选的一个可交易变体：族 + 阶级 + 附魔 → uniqueName。
    /// 游戏内集市是「物品 + 阶级 + 附魔」压一行；620 宽装不下 5 阶 × 4 附魔 = 40 行，
    /// 故本设计把一行拆成「物品名 + 阶级 chip + 附魔 chip」，层级深度不变（设计 §4.1）。
    /// </summary>
    public class ItemVariant
    {
        /// <summary>去掉附魔后缀的物品名，如 T5_HEAD_CLOTH_SET1。</summary>
        public string BaseId;
        /// <summary>带附魔后缀的完整名，如 T5_HEAD_CLOTH_SET1@1；附魔 0 时等于 BaseId。</summary>
        public string UniqueName;
        public int Tier;
        public int Enchant;
        /// <summary>该变体的本地化显示名（含阶级前缀，如「专家级学士风帽」）。</summary>
        public string DisplayName;
    }

    /// <summary>三级物品族：去掉阶级前缀与附魔后缀后的同一件东西（如学士风帽全阶级）。</summary>
    public class ItemFamily
    {
        public string Family;          // 族键，如 HEAD_CLOTH_SET1
        public string DisplayName;     // 去掉阶级前缀的族名，如 学士风帽
        public readonly List<ItemVariant> Variants = new List<ItemVariant>();

        /// <summary>该族出现过的阶级（升序）。三级列只画这些 chip，不画 1–8 全集。</summary>
        public List<int> Tiers()
        {
            var list = new List<int>();
            var seen = new HashSet<int>();
            foreach (var v in Variants)
                if (seen.Add(v.Tier)) list.Add(v.Tier);
            list.Sort();
            return list;
        }

        /// <summary>该阶级下出现过的附魔等级（升序，恒含 0 表示无附魔本体）。</summary>
        public List<int> Enchants(int tier)
        {
            var list = new List<int>();
            var seen = new HashSet<int>();
            foreach (var v in Variants)
                if (v.Tier == tier && seen.Add(v.Enchant)) list.Add(v.Enchant);
            list.Sort();
            return list;
        }

        public ItemVariant Find(int tier, int enchant)
        {
            foreach (var v in Variants)
                if (v.Tier == tier && v.Enchant == enchant) return v;
            return null;
        }

        /// <summary>该族是否存在任何带附魔的变体（否则三级行不画附魔 chip，避免画一排点不动的签）。</summary>
        public bool HasEnchantVariants()
        {
            foreach (var v in Variants)
                if (v.Enchant > 0) return true;
            return false;
        }
    }

    /// <summary>二级子类（游戏集市的材质/类型分支，如「布甲风帽」）。</summary>
    public class ItemSubCategory
    {
        public string Key;        // 元数据原始值，如 cloth_helmet
        public string DisplayName; // 中文标签，如 布甲风帽
        public readonly List<ItemFamily> Families = new List<ItemFamily>();
        public int FamilyCount { get { return Families.Count; } }
    }

    /// <summary>一级分类（装备部位，如「头部护甲」）。</summary>
    public class ItemCategory
    {
        public string Key;         // 元数据原始值，如 head
        public string DisplayName; // 中文标签，如 头部护甲
        public readonly List<ItemSubCategory> SubCategories = new List<ItemSubCategory>();
    }

    /// <summary>
    /// 三级分类树：**完全由 ao-bin-dumps 物品元数据生成，不硬编码分类树**
    /// （设计 §4.1 硬条件：硬编码的分支跟不上游戏更新，亚服新物品上线当天就会漏）。
    ///
    /// 层级来源是原始版 items.json 的三个属性，正是游戏集市菜单的同源字段：
    ///   @shopcategory      → 一级（weapons / head / armors / shoes / offhands / capes / bags / mounts …）
    ///   @shopsubcategory1  → 二级（bow / cloth_helmet / plate_armor / shieldtype …）
    ///   @shopsubcategory2  → 游戏内更细一档，本设计不下探（620 宽只容三列），仅用于诊断
    /// 三级是本层自算的「物品族 + 阶级/附魔 chip」。
    ///
    /// 本类只做三件事：按 metadata 分组、按族聚合、给 key 配中文标签。
    /// 中文标签是**标签表**不是分类树——表里缺 key 时回退到 key 本身的人性化形态并标记 Untranslated，
    /// 绝不因为缺标签就把物品从树里丢掉（丢物品比显示英文 key 更糟）。
    /// </summary>
    public class ItemTaxonomy
    {
        public readonly List<ItemCategory> Categories = new List<ItemCategory>();

        /// <summary>无法归类（元数据缺 @shopcategory）的可交易物品数。显式暴露，供 UI 告警，不静默丢弃。</summary>
        public int UnclassifiedItemCount { get; private set; }

        /// <summary>标签表未覆盖的 key（诊断用，便于按游戏更新补表，不编造标签）。</summary>
        public readonly List<string> UntranslatedKeys = new List<string>();

        public int FamilyCount { get; private set; }

        public ItemCategory Find(string key)
        {
            if (key == null) return null;
            foreach (var c in Categories)
                if (c.Key == key) return c;
            return null;
        }

        /// <summary>从物品索引构建。索引为 null / 空 → 返回空树（Count==0），由 UI 显式告警。</summary>
        public static ItemTaxonomy Build(ItemIndex index)
        {
            var tax = new ItemTaxonomy();
            if (index == null || index.Count == 0) return tax;

            var catMap = new Dictionary<string, ItemCategory>(StringComparer.Ordinal);
            var subMap = new Dictionary<string, ItemSubCategory>(StringComparer.Ordinal);
            var famMap = new Dictionary<string, ItemFamily>(StringComparer.Ordinal);

            foreach (var baseId in index.TieredBaseIds())
            {
                ItemIndexEntry entry;
                if (!index.TryGetEntry(baseId, out entry) || entry == null) continue;

                string cat = Normalize(entry.ShopCategory);
                string sub = Normalize(entry.ShopSubCategory1);
                if (cat.Length == 0)
                {
                    tax.UnclassifiedItemCount++;
                    continue; // 无分类 = 不画进树，但计数并告警
                }
                if (sub.Length == 0) sub = "other";

                ItemCategory category;
                if (!catMap.TryGetValue(cat, out category))
                {
                    category = new ItemCategory { Key = cat, DisplayName = LabelOfCategory(cat) };
                    if (category.DisplayName == null)
                    {
                        category.DisplayName = HumanizeKey(cat);
                        tax.NoteUntranslated("L1:" + cat);
                    }
                    catMap[cat] = category;
                    tax.Categories.Add(category);
                }

                string subKey = cat + "|" + sub;
                ItemSubCategory subCat;
                if (!subMap.TryGetValue(subKey, out subCat))
                {
                    subCat = new ItemSubCategory { Key = sub, DisplayName = LabelOfSub(cat, sub) };
                    if (subCat.DisplayName == null)
                    {
                        subCat.DisplayName = HumanizeKey(sub);
                        tax.NoteUntranslated("L2:" + subKey);
                    }
                    subMap[subKey] = subCat;
                    category.SubCategories.Add(subCat);
                }

                int tier = ParseTier(baseId);
                if (tier <= 0) continue;
                string family = StripTier(baseId);
                string famKey = subKey + "|" + family;
                ItemFamily fam;
                if (!famMap.TryGetValue(famKey, out fam))
                {
                    fam = new ItemFamily
                    {
                        Family = family,
                        DisplayName = entry.NameZh ?? entry.NameEn ?? family
                    };
                    famMap[famKey] = fam;
                    subCat.Families.Add(fam);
                    tax.FamilyCount++;
                }

                string name = entry.NameZh ?? entry.NameEn;
                var variant = new ItemVariant
                {
                    BaseId = baseId,
                    UniqueName = baseId,
                    Tier = tier,
                    Enchant = 0,
                    DisplayName = name ?? family
                };
                fam.Variants.Add(variant);
            }

            // 附魔变体：formatted items.json 里以 T5_X@1 形式独立成条，ItemIndex 按 baseId 合并，
            // 但记录了该 baseId 支持哪些附魔等级（EnchantLevels）。这里展开成（阶级, 附魔）矩阵。
            foreach (var cat in tax.Categories)
            {
                foreach (var subCat in cat.SubCategories)
                {
                    foreach (var fam in subCat.Families)
                    {
                        var baseVariants = new List<ItemVariant>(fam.Variants);
                        fam.Variants.Clear();
                        foreach (var bv in baseVariants)
                        {
                            ItemIndexEntry entry;
                            index.TryGetEntry(bv.BaseId, out entry);
                            var levels = entry != null && entry.EnchantLevels != null && entry.EnchantLevels.Count > 0
                                ? entry.EnchantLevels
                                : new List<int> { 0 };
                            foreach (int lv in levels)
                            {
                                if (lv <= 0) { fam.Variants.Add(bv); continue; }
                                fam.Variants.Add(new ItemVariant
                                {
                                    BaseId = bv.BaseId,
                                    UniqueName = bv.BaseId + "@" + lv.ToString(CultureInfo.InvariantCulture),
                                    Tier = bv.Tier,
                                    Enchant = lv,
                                    DisplayName = bv.DisplayName
                                });
                            }
                        }
                        fam.Variants.Sort(delegate (ItemVariant a, ItemVariant b)
                        {
                            int c = a.Tier.CompareTo(b.Tier);
                            return c != 0 ? c : a.Enchant.CompareTo(b.Enchant);
                        });
                        fam.DisplayName = DeriveFamilyName(fam);
                    }
                }
            }

            // 稳定排序：一级按「装备部位优先」的游戏习惯，其余按名字
            tax.Categories.Sort(delegate (ItemCategory a, ItemCategory b)
            {
                int ra = CategoryRank(a.Key), rb = CategoryRank(b.Key);
                if (ra != rb) return ra.CompareTo(rb);
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture);
            });
            foreach (var cat in tax.Categories)
                cat.SubCategories.Sort(delegate (ItemSubCategory a, ItemSubCategory b)
                {
                    int c = b.FamilyCount.CompareTo(a.FamilyCount); // 物品多的子类排前
                    return c != 0 ? c : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture);
                });

            return tax;
        }

        private void NoteUntranslated(string key)
        {
            if (!UntranslatedKeys.Contains(key)) UntranslatedKeys.Add(key);
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Trim().ToLowerInvariant();
        }

        /// <summary>解析物品名开头的阶级 T1..T8，解析不到返回 0。</summary>
        public static int ParseTier(string baseId)
        {
            if (string.IsNullOrEmpty(baseId) || baseId[0] != 'T' || baseId.Length < 3 || baseId[2] != '_')
                return 0;
            int tier = baseId[1] - '0';
            return tier >= 1 && tier <= 8 ? tier : 0;
        }

        /// <summary>T5_HEAD_CLOTH_SET1 → HEAD_CLOTH_SET1（剥掉阶级前缀，得到族键）。</summary>
        public static string StripTier(string baseId)
        {
            if (string.IsNullOrEmpty(baseId)) return "";
            return ParseTier(baseId) > 0 ? baseId.Substring(3) : baseId;
        }

        /// <summary>
        /// 族名由数据推导，不靠硬编码阶级词表：
        /// 同一族跨阶级的中文名形如「老手级学士风帽 / 专家级学士风帽」，取全部名字的最长公共后缀，
        /// 若该后缀以「级」开头则去掉这个「级」（阶级词本身长度不一，但恒以「级」收尾），
        /// 结果就是纯族名「学士风帽」。单阶级族无法求公共后缀，退化为「最后一个『级』之后」。
        /// </summary>
        public static string DeriveFamilyName(ItemFamily fam)
        {
            if (fam == null || fam.Variants.Count == 0) return fam == null ? "" : fam.Family;

            var names = new List<string>();
            foreach (var v in fam.Variants)
            {
                string n = v.DisplayName;
                if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
            }
            if (names.Count == 1) return StripTierWord(names[0]);

            string common = LongestCommonSuffix(names);
            if (common.Length >= 2) return StripTierWord(common);
            return fam.Family;
        }

        private static string StripTierWord(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (name[0] == '级' && name.Length > 1) return name.Substring(1);
            int idx = name.LastIndexOf('级');
            if (idx >= 0 && idx + 1 < name.Length && idx <= 4) return name.Substring(idx + 1);
            return name;
        }

        private static string LongestCommonSuffix(List<string> names)
        {
            if (names == null || names.Count == 0) return "";
            int best = 0;
            string first = names[0];
            for (int len = 1; len <= first.Length; len++)
            {
                string suffix = first.Substring(first.Length - len);
                bool all = true;
                for (int i = 1; i < names.Count && all; i++)
                    if (names[i].Length < len || !names[i].EndsWith(suffix, StringComparison.Ordinal))
                        all = false;
                if (all) best = len;
                else break;
            }
            return best == 0 ? "" : first.Substring(first.Length - best);
        }

        /// <summary>一级排序权重：先装备部位（游戏集市前两页），再素材/消耗，最后长尾。</summary>
        private static int CategoryRank(string key)
        {
            switch (key)
            {
                case "weapons": return 0;
                case "head": return 1;
                case "armors": return 2;
                case "shoes": return 3;
                case "offhands": return 4;
                case "capes": return 5;
                case "bags": return 6;
                case "mounts": return 7;
                case "consumables": return 8;
                case "gathering": return 9;
                case "crafting": return 10;
                case "farming": return 11;
                case "artefacts": return 12;
                case "furniture": return 13;
                case "vanity": return 14;
                case "other": return 16;
                default: return 15;
            }
        }

        /// <summary>一级中文标签表。<b>这是标签、不是分类树</b>：key 集合由元数据决定，这里只给已知 key 配中文。</summary>
        private static string LabelOfCategory(string key)
        {
            switch (key)
            {
                case "weapons": return "武器";
                case "head": return "头部护甲";
                case "armors": return "身体护甲";
                case "shoes": return "足部护甲";
                case "offhands": return "副手";
                case "capes": return "披风";
                case "bags": return "背包";
                case "mounts": return "坐骑";
                case "consumables": return "消耗品";
                case "gathering": return "采集装备";
                case "crafting": return "材料";
                case "farming": return "农耕";
                case "artefacts": return "神器";
                case "furniture": return "家具";
                case "vanity": return "装饰";
                case "other": return "其他";
                default: return null;
            }
        }

        /// <summary>二级中文标签表（L1|L2 → 名称）。缺项回退 HumanizeKey 并记 UntranslatedKeys。</summary>
        private static string LabelOfSub(string cat, string sub)
        {
            switch (cat + "|" + sub)
            {
                // 武器
                case "weapons|bow": return "弓";
                case "weapons|crossbow": return "十字弓";
                case "weapons|axe": return "战斧";
                case "weapons|dagger": return "匕首";
                case "weapons|mace": return "锤子";
                case "weapons|knuckles": return "战斗手套";
                case "weapons|hammer": return "战锤";
                case "weapons|quarterstaff": return "铁头棒";
                case "weapons|spear": return "长矛";
                case "weapons|sword": return "剑";
                case "weapons|arcanestaff": return "奥术法杖";
                case "weapons|cursestaff": return "诅咒法杖";
                case "weapons|firestaff": return "火焰法杖";
                case "weapons|froststaff": return "天霜法杖";
                case "weapons|holystaff": return "神圣法杖";
                case "weapons|naturestaff": return "自然法杖";
                case "weapons|shapeshifterstaff": return "幻形者法杖";
                case "weapons|other": return "其他武器";

                // 三部位护甲（游戏集市的材质分支）
                case "head|cloth_helmet": return "布甲风帽";
                case "head|leather_helmet": return "皮甲兜帽";
                case "head|plate_helmet": return "板甲头盔";
                case "head|other": return "其他头部";
                case "armors|cloth_armor": return "布甲";
                case "armors|leather_armor": return "皮甲";
                case "armors|plate_armor": return "板甲";
                case "armors|other": return "其他身体";
                case "shoes|cloth_shoes": return "布甲鞋";
                case "shoes|leather_shoes": return "皮甲鞋";
                case "shoes|plate_shoes": return "板甲鞋";
                case "shoes|other": return "其他足部";

                // 副手
                case "offhands|shieldtype": return "盾牌";
                case "offhands|torchtype": return "火炬";
                case "offhands|booktype": return "法典";
                case "offhands|other": return "其他副手";

                // 背包
                case "bags|bags": return "背包";
                case "bags|satchels": return "皮包";

                // 坐骑
                case "mounts|basemounts": return "基础坐骑";
                case "mounts|battle_mount": return "战斗坐骑";
                case "mounts|raremounts": return "稀有坐骑";

                // 消耗品
                case "consumables|potions": return "药水";
                case "consumables|food": return "食物";
                case "consumables|tomes": return "典籍";
                case "consumables|silverbag": return "银袋";
                case "consumables|other": return "其他消耗品";

                // 采集
                case "gathering|ore": return "采矿";
                case "gathering|wood": return "伐木";
                case "gathering|fiber": return "织布";
                case "gathering|hide": return "剥皮";
                case "gathering|rock": return "采石";
                case "gathering|fish": return "钓鱼";
                case "gathering|tracking": return "追踪";

                // 材料
                case "crafting|resources": return "原料";
                case "crafting|refinedresources": return "精炼材料";
                case "crafting|alchemy": return "炼金";
                case "crafting|fish": return "鱼";
                case "crafting|cityresources": return "城市资源";
                case "crafting|tokens": return "代币";

                // 农耕
                case "farming|farm": return "农田产物";
                case "farming|farmingproducts": return "农产物";
                case "farming|herbgarden": return "药草";
                case "farming|kennel": return "兽栏";
                case "farming|pasture": return "牧场";

                // 神器
                case "artefacts|weapons": return "武器神器";
                case "artefacts|head": return "头部神器";
                case "artefacts|armors": return "身体神器";
                case "artefacts|shoes": return "足部神器";
                case "artefacts|offhands": return "副手神器";
                case "artefacts|capes": return "披风神器";
                case "artefacts|fragments": return "神器碎片";
                case "artefacts|favor": return "神力";

                // 家具
                case "furniture|chest": return "箱子";
                case "furniture|repairkit": return "修理工具";
                case "furniture|world": return "场景家具";

                // 其他
                case "other|guilds": return "公会物品";
                case "other|labourers": return "劳工";
                case "other|lootitem": return "战利品";
                case "other|maps": return "地图";
                case "other|tokens": return "代币";
                case "other|trash": return "杂物";
                case "other|luxurygoods": return "珍宝";

                // 装饰
                case "vanity|killemotes": return "击杀特效";

                // 披风按城市/来源分支：城市名从 key 原样取，不臆造中文译名
                case "capes|accessoires_capes_capes": return "普通披风";
                case "capes|accessoires_capes_bridgewatch": return "桥望披风";
                case "capes|accessoires_capes_caerleon": return "凯城披风";
                case "capes|accessoires_capes_thetford": return "塞福披风";
                case "capes|accessoires_capes_fortsterling": return "斯特披风";
                case "capes|accessoires_capes_lymhurst": return "林堡披风";
                case "capes|accessoires_capes_martlock": return "马丁披风";
                case "capes|accessoires_capes_brecilien": return "Brecilien 披风";
                case "capes|accessoires_capes_avalon": return "Avalon 披风";
                case "capes|accessoires_capes_smuggler": return "走私者披风";
                case "capes|accessoires_capes_heretic": return "异端披风";
                case "capes|accessoires_capes_undead": return "亡灵披风";
                case "capes|accessoires_capes_keeper": return "守林者披风";
                case "capes|accessoires_capes_morgana": return "莫甘娜披风";
                case "capes|accessoires_capes_demon": return "恶魔披风";
                case "capes|other": return "其他披风";
                default: return null;
            }
        }

        /// <summary>无标签 key 的人性化形态：去常见前缀、下划线转空格、首字母大写。原始 key 不丢。</summary>
        public static string HumanizeKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            string k = key;
            if (k.StartsWith("accessoires_capes_", StringComparison.Ordinal))
                k = k.Substring("accessoires_capes_".Length);
            var sb = new StringBuilder();
            bool upper = true;
            foreach (char c in k)
            {
                if (c == '_')
                {
                    sb.Append(' ');
                    upper = true;
                    continue;
                }
                sb.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            return sb.ToString().Trim();
        }
    }
}
