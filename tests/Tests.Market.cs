using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AlbionCompanion.Core;

// 市场 + 行情数据 模块回归（dev-market）。
// 断言入口：LogicTests.Check / LogicTests.Eq（internal static，同程序集可见）。
// 约定：每个用例自建临时目录/独立服务实例，结束清理；不依赖真实网络/真实物品数据文件。
static class MarketModuleTests
{
    public static void Run()
    {
        RunQualityTests();
        RunCityTests();
        RunCacheKeyTests();
        RunEnchantResolveTests();
        RunClassifierTests();
        RunSearchScopeTests();
        RunQuoteModelTests();
        RunMarketDbTests();
        RunClockTests();
    }

    // ─────────────────────────────────────────────────────────────
    // 1) 品质口径：1..5 = 普通/良好/杰出/精良/大师（修 off-by-one）
    // ─────────────────────────────────────────────────────────────
    static void RunQualityTests()
    {
        LogicTests.Eq("品质: 1 = 普通", MarketQuality.LabelOf(1), "普通");
        LogicTests.Eq("品质: 2 = 良好", MarketQuality.LabelOf(2), "良好");
        LogicTests.Eq("品质: 3 = 杰出", MarketQuality.LabelOf(3), "杰出");
        LogicTests.Eq("品质: 4 = 精良", MarketQuality.LabelOf(4), "精良");
        LogicTests.Eq("品质: 5 = 大师", MarketQuality.LabelOf(5), "大师");
        LogicTests.Check("品质: 0 不冒充普通", MarketQuality.LabelOf(0) != "普通", MarketQuality.LabelOf(0));
        LogicTests.Check("品质: 6 不冒充大师", MarketQuality.LabelOf(6) != "大师", MarketQuality.LabelOf(6));

        var items = MarketQuality.DropdownItems();
        LogicTests.Eq("品质下拉: 全部+5 档 = 6 项", items.Length, 6);
        LogicTests.Eq("品质下拉: 首项全部", items[0], "全部");
        LogicTests.Eq("品质下拉: 第 2 项=普通(quality1)", items[1], "1 · 普通");
        LogicTests.Eq("品质下拉: 末项=大师(quality5)", items[5], "5 · 大师");

        // 旧 bug：选「普通」按 SelectedIndex-1 得到 0 → 恒空。现在索引 1..5 直通 quality。
        LogicTests.Eq("品质下拉: 选普通→quality 1（不是 0）", MarketQuality.FromDropdownIndex(1), 1);
        LogicTests.Eq("品质下拉: 选大师→quality 5", MarketQuality.FromDropdownIndex(5), 5);
        LogicTests.Eq("品质下拉: 全部→0（不过滤）", MarketQuality.FromDropdownIndex(0), 0);
        LogicTests.Eq("品质下拉: 越界→0（不猜品质）", MarketQuality.FromDropdownIndex(9), 0);
        LogicTests.Check("品质过滤: 选普通只留 1", MarketQuality.Passes(1, 1) && !MarketQuality.Passes(2, 1), "quality filter");
        LogicTests.Check("品质过滤: 全部放行", MarketQuality.Passes(1, 0) && MarketQuality.Passes(5, 0), "all filter");
    }

    // ─────────────────────────────────────────────────────────────
    // 2) 城市口径 + 黑市（跑商前置）+ 别名规范化
    // ─────────────────────────────────────────────────────────────
    static void RunCityTests()
    {
        LogicTests.Eq("城市: 亚服六城", MarketCities.AsiaSix.Length, 6);
        LogicTests.Eq("城市: 全集 = 六城 + 黑市", MarketCities.All.Length, 7);
        LogicTests.Check("城市: 含 Black Market（跑商黑市腿前置）",
            Array.IndexOf(MarketCities.All, MarketCities.BlackMarket) >= 0,
            string.Join(",", MarketCities.All));
        LogicTests.Eq("城市: 黑市规范名", MarketCities.BlackMarket, "Black Market");
        LogicTests.Eq("城市: 短名 黑市", MarketCities.ShortName("blackmarket"), "黑市");

        LogicTests.Eq("城市别名: 小写+多空格归一", MarketCities.Normalize("  fort   sterling "), "Fort Sterling");
        LogicTests.Eq("城市别名: FortSterling → Fort Sterling", MarketCities.Normalize("fortsterling"), "Fort Sterling");
        LogicTests.Eq("城市别名: 中文凯城 → Caerleon", MarketCities.Normalize("凯城"), "Caerleon");
        LogicTests.Eq("城市别名: 黑市 → Black Market", MarketCities.Normalize("黑市"), MarketCities.BlackMarket);
        LogicTests.Eq("城市别名: 未知城市只 Trim/合空格", MarketCities.Normalize("  Some   City "), "Some City");
        LogicTests.Check("城市: IsBlackMarket 别名判定", MarketCities.IsBlackMarket("black market"), "IsBlackMarket");
    }

    // ─────────────────────────────────────────────────────────────
    // 3) MarketDataService 缓存键读写同源（修「小写 ID 永久 miss」）
    // ─────────────────────────────────────────────────────────────
    static void RunCacheKeyTests()
    {
        LogicTests.Eq("缓存键: 小写 ID/城市归一",
            MarketDataCache.Key(" t4_bag ", "caerleon", 1), "T4_BAG|Caerleon|1");
        LogicTests.Eq("缓存键: 保留附魔后缀", MarketDataCache.Key("t5_2h_bow@2", "Caerleon", 1), "T5_2H_BOW@2|Caerleon|1");
        LogicTests.Eq("缓存键: 城市别名同键",
            MarketDataCache.Key("T4_BAG", "fort sterling", 1), MarketDataCache.Key("T4_BAG", "Fort Sterling", 1));
        LogicTests.Check("缓存键: 不同品质不同键",
            MarketDataCache.Key("T4_BAG", "Caerleon", 1) != MarketDataCache.Key("T4_BAG", "Caerleon", 2), "quality key");

        // 服务层：小写请求 + 大写响应 → 必须命中同一份缓存（旧实现 Put 大写/Get 小写永久 miss）
        int fetchCalls = 0;
        var clock = DateTimeOffset.Parse("2026-11-09T00:00:00Z");
        string body = "["
            + "{\"item_id\":\"T4_BAG\",\"city\":\"Caerleon\",\"quality\":1,"
            + "\"sell_price_min\":5998,\"sell_price_min_date\":\"2026-11-08T12:00:00\","
            + "\"buy_price_max\":5100,\"buy_price_max_date\":\"2026-11-08T11:00:00\"}"
            + "]";
        var svc = new MarketDataService(
            delegate (string url, CancellationToken token) { fetchCalls++; return System.Threading.Tasks.Task.FromResult(body); },
            () => clock,
            new MarketRateLimiter(600, () => clock));

        var first = svc.GetPricesAsync(new[] { "t4_bag" }, new[] { "caerleon" },
            MarketQueryPurpose.ManualQuery).GetAwaiter().GetResult();
        var q1 = first.Find(r => r.Quality == 1);
        LogicTests.Check("缓存键: 小写请求能读到真实报价（不是合成空行）",
            q1 != null && q1.SellMin == 5998L && q1.SellHasData, "q1=" + (q1 == null ? "null" : q1.SellMin.ToString()));
        LogicTests.Eq("缓存键: 一次请求写入 5 个品质条目", svc.CachedEntryCount, 5);

        // 第二次用原始大小写查询 → 必须命中缓存、不再发请求
        svc.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Caerleon" },
            MarketQueryPurpose.ManualQuery).GetAwaiter().GetResult();
        LogicTests.Eq("缓存键: 大小写互换仍命中缓存（不再重打 AODP）", fetchCalls, 1);
        LogicTests.Check("缓存键: 大写 Get 命中同一缓存项",
            svc.SnapshotCache().Any(e => e.Quote != null && e.Quote.ItemId == "T4_BAG" && e.Quote.Quality == 1),
            "snapshot miss");

        // 只读「可展示报价」快照：合成行（HasResponse=false）不返回，真实行返回
        var pricing = svc.SnapshotPricingEntries();
        LogicTests.Eq("展示快照: 只含真实上报行（1 条）", pricing.Count, 1);
        LogicTests.Check("展示快照: 合成行 HasResponse=false 被排除",
            pricing.All(e => e.Quote.Quality == 1), "quality=" + string.Join(",", pricing.Select(e => e.Quote.Quality)));

        // 黑市：sell=0 / buy>0 的行必须保留（黑市 NPC 只有买单）
        int bmCalls = 0;
        string bmBody = "[{\"item_id\":\"T4_BAG\",\"city\":\"Black Market\",\"quality\":1,"
            + "\"sell_price_min\":0,\"sell_price_min_date\":\"0001-01-01T00:00:00\","
            + "\"buy_price_max\":4200,\"buy_price_max_date\":\"2026-11-08T11:00:00\"}]";
        var svcBm = new MarketDataService(
            delegate (string url, CancellationToken token) { bmCalls++; return System.Threading.Tasks.Task.FromResult(bmBody); },
            () => clock,
            new MarketRateLimiter(600, () => clock));
        svcBm.GetPricesAsync(new[] { "T4_BAG" }, new[] { "blackmarket" },
            MarketQueryPurpose.ManualQuery).GetAwaiter().GetResult();
        var bmPricing = svcBm.SnapshotPricingEntries();
        LogicTests.Check("黑市: sell=0/buy>0 的可展示行保留",
            bmPricing.Any(e => MarketCities.IsBlackMarket(e.Quote.City) && e.Quote.BuyMax == 4200L),
            "black market row missing");
        svcBm.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Black Market" },
            MarketQueryPurpose.ManualQuery).GetAwaiter().GetResult();
        LogicTests.Eq("黑市: 城市别名与规范名同键（不重复回源）", bmCalls, 1);
    }

    // ─────────────────────────────────────────────────────────────
    // 4) 附魔：ResolveTerms 不再 Split('@')[0]（修 +1..+4 恒空）
    // ─────────────────────────────────────────────────────────────
    static void RunEnchantResolveTests()
    {
        var names = new Dictionary<string, string>();
        var full = ItemCatalog.ResolveTerms("T5_2H_BOW@2", names, new NameCache(), null);
        LogicTests.Eq("附魔: 输入 @2 原样保留", string.Join(",", full.ToArray()), "T5_2H_BOW@2");
        var list = ItemCatalog.ResolveTerms("T5_2H_BOW@1, T5_2H_BOW@2", names, new NameCache(), null);
        LogicTests.Eq("附魔: 多个附魔变体各自保留", list.Count, 2);
        LogicTests.Check("附魔: @1 与 @2 是两个不同查询目标", list.Contains("T5_2H_BOW@1") && list.Contains("T5_2H_BOW@2"),
            string.Join(",", list.ToArray()));
        var noEnch = ItemCatalog.ResolveTerms("T4_BAG", names, new NameCache(), null);
        LogicTests.Eq("附魔: 无附魔 ID 原样", string.Join(",", noEnch.ToArray()), "T4_BAG");
        LogicTests.Eq("附魔: MarketItemKey 保留附魔", MarketItemKey.Normalize(" t5_2h_bow@2 "), "T5_2H_BOW@2");
        LogicTests.Eq("附魔: 基名去后缀", MarketItemKey.BaseOf("T5_2H_BOW@2"), "T5_2H_BOW");
        LogicTests.Eq("附魔: 等级解析", MarketItemKey.EnchantOf("T5_2H_BOW@2"), 2);
    }

    // ─────────────────────────────────────────────────────────────
    // 5) 一级/二级分类：@shopcategory 数据驱动 + 无元数据回退标注
    //    （修布/皮头盔→材料、板甲头盔→身体护甲、装甲坐骑→护甲）
    // ─────────────────────────────────────────────────────────────
    static void RunClassifierTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ac_mkt_cls_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            string fmt = Path.Combine(dir, ItemIndex.FormattedFileName);
            string raw = Path.Combine(dir, ItemIndex.RawFileName);
            File.WriteAllText(fmt, "["
                + "{\"UniqueName\":\"T4_HEAD_CLOTH_SET1\",\"LocalizedNames\":{\"ZH-CN\":\"老手级学士风帽\"}},"
                + "{\"UniqueName\":\"T4_HEAD_PLATE_SET1\",\"LocalizedNames\":{\"ZH-CN\":\"老手级士兵头盔\"}},"
                + "{\"UniqueName\":\"T4_MOUNT_ARMORED_HORSE\",\"LocalizedNames\":{\"ZH-CN\":\"老手级装甲马\"}},"
                + "{\"UniqueName\":\"T4_MAIN_SHIELD\",\"LocalizedNames\":{\"ZH-CN\":\"老手级盾牌\"}},"
                + "{\"UniqueName\":\"T4_TOOL_SICKLE\",\"LocalizedNames\":{\"ZH-CN\":\"老手级镰刀\"}}"
                + "]");
            File.WriteAllText(raw, "{"
                + "\"?xml\":{\"@version\":\"1.0\"},"
                + "\"items\":{"
                + "  \"equipmentitem\":["
                + "    {\"@uniquename\":\"T4_HEAD_CLOTH_SET1\",\"@shopcategory\":\"head\",\"@shopsubcategory1\":\"cloth_helmet\",\"@weight\":\"1.2\"},"
                + "    {\"@uniquename\":\"T4_HEAD_PLATE_SET1\",\"@shopcategory\":\"head\",\"@shopsubcategory1\":\"plate_helmet\",\"@weight\":\"2.0\"},"
                + "    {\"@uniquename\":\"T4_MOUNT_ARMORED_HORSE\",\"@shopcategory\":\"mounts\",\"@shopsubcategory1\":\"basemounts\",\"@weight\":\"20\"},"
                + "    {\"@uniquename\":\"T4_MAIN_SHIELD\",\"@shopcategory\":\"offhands\",\"@shopsubcategory1\":\"shieldtype\",\"@weight\":\"3\"},"
                + "    {\"@uniquename\":\"T4_TOOL_SICKLE\",\"@shopcategory\":\"gathering\",\"@shopsubcategory1\":\"hide\",\"@weight\":\"2\"}"
                + "  ]"
                + "}}");

            var idx = ItemIndex.Load(fmt, raw);
            var tax = ItemTaxonomy.Build(idx);

            var cloth = MarketItemClassifier.Classify(idx, tax, "T4_HEAD_CLOTH_SET1");
            LogicTests.Check("分类: 布头盔走元数据=head（不再误分材料）",
                cloth.FromMetadata && cloth.CategoryKey == "head" && cloth.CategoryLabel == "头部护甲",
                cloth.CategoryKey + "/" + cloth.CategoryLabel);
            LogicTests.Eq("分类: 布头盔二级子类非空", cloth.SubKey, "cloth_helmet");
            LogicTests.Eq("分类: 布头盔二级标签", cloth.SubLabel, "布甲风帽");

            var plate = MarketItemClassifier.Classify(idx, tax, "T4_HEAD_PLATE_SET1");
            LogicTests.Check("分类: 板甲头盔=head（不再误分身体护甲）",
                plate.FromMetadata && plate.CategoryKey == "head", plate.CategoryKey);

            var mount = MarketItemClassifier.Classify(idx, tax, "T4_MOUNT_ARMORED_HORSE");
            LogicTests.Check("分类: 装甲坐骑=mounts（不再误分身体护甲）",
                mount.FromMetadata && mount.CategoryKey == "mounts" && mount.CategoryLabel == "坐骑",
                mount.CategoryKey + "/" + mount.CategoryLabel);

            var shield = MarketItemClassifier.Classify(idx, tax, "T4_MAIN_SHIELD");
            LogicTests.Eq("分类: 副手盾牌二级子类", shield.SubKey, "shieldtype");
            LogicTests.Check("二级筛选: 副手/盾牌不再恒空（taxonomy 子类命中）",
                MarketItemClassifier.Passes(shield, "offhands", "shieldtype"), "shield sub filter");
            // 旧 KeywordFor("副手","盾牌") 返回 ""（已知子类但无关键词）→ 旧逻辑全部不匹配的根因
            LogicTests.Eq("二级筛选: 旧 KeywordFor 对副手/盾牌返回空串（历史 bug 复现）",
                ItemCatalog.Taxonomy.KeywordFor("副手", "盾牌"), "");

            var sickle = MarketItemClassifier.Classify(idx, tax, "T4_TOOL_SICKLE");
            LogicTests.Eq("二级筛选: 剥皮子类 key", sickle.SubKey, "hide");
            LogicTests.Check("二级筛选: 剥皮不再恒空",
                MarketItemClassifier.Passes(sickle, "gathering", "hide"), "hide sub filter");

            // 无元数据回退并标注：不冒充数据分类，选了分类时不被放进用户选的分类里
            var unknown = MarketItemClassifier.Classify(idx, tax, "T5_UNKNOWN_ARMOR");
            LogicTests.Check("分类回退: 无元数据标 FromMetadata=false", !unknown.FromMetadata, "FromMetadata");
            LogicTests.Check("分类回退: key 带 fallback 前缀不冒充 taxonomy",
                unknown.CategoryKey != null && unknown.CategoryKey.StartsWith(MarketItemClassifier.FallbackPrefix, StringComparison.Ordinal),
                unknown.CategoryKey);
            LogicTests.Eq("分类回退: 回退标签来自关键词猜测", unknown.CategoryLabel, "身体护甲");
            LogicTests.Check("分类回退: 选分类时回退物品不冒充匹配",
                !MarketItemClassifier.Passes(unknown, "head", null), "fallback leaked into selected category");

            // 三级树定位（搜索导航用）
            var located = MarketItemClassifier.Locate(tax, "T4_HEAD_PLATE_SET1@2");
            LogicTests.Check("树定位: 找到族", located.Found && located.Family != null
                && located.Family.Family == "HEAD_PLATE_SET1", located.Found ? "found" : "missing");
            LogicTests.Eq("树定位: 阶级从 ID 解析", located.Tier, 4);
            LogicTests.Eq("树定位: 附魔从 ID 解析", located.Enchant, 2);
            LogicTests.Check("树定位: 未知物品 Found=false",
                !MarketItemClassifier.Locate(tax, "T5_UNKNOWN_ARMOR").Found, "should not locate");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 6) 搜索三态：无结果必须空表，不得回落全量缓存
    // ─────────────────────────────────────────────────────────────
    static void RunSearchScopeTests()
    {
        LogicTests.Eq("搜索: 无输入 = NoQuery", MarketSearchScope.Of("", new[] { "T4_BAG" }), MarketSearchState.NoQuery);
        LogicTests.Eq("搜索: 空白输入 = NoQuery", MarketSearchScope.Of("   ", new string[0]), MarketSearchState.NoQuery);
        LogicTests.Eq("搜索: 有输入有命中 = HasMatches",
            MarketSearchScope.Of("背包", new[] { "T4_BAG" }), MarketSearchState.HasMatches);
        LogicTests.Eq("搜索: 有输入无命中 = NoMatches（空表+提示，不回落全量）",
            MarketSearchScope.Of("不存在的词", new List<string>()), MarketSearchState.NoMatches);
        var scope = MarketSearchScope.ToScopeSet(new[] { "t4_bag@1", "T5_2H_BOW" });
        LogicTests.Check("搜索: 命中集大小写不敏感且折叠 baseId",
            scope.Contains("T4_BAG") && scope.Contains("T5_2H_BOW"), "scope set");
    }

    // ─────────────────────────────────────────────────────────────
    // 7) QuoteTableModel：双侧时效 / 逐侧极值 / 0 值四形态 / 单调时间文案
    // ─────────────────────────────────────────────────────────────
    static void RunQuoteModelTests()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

        // ① 同一城：买价新鲜（5 分钟）、卖价过期（40 分钟）→ 买卖各自判定
        var mixed = new MarketQuote
        {
            ItemId = "T4_BAG", City = "Bridgewatch", Quality = 1,
            SellMin = 8420, SellMinDate = now.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ss"),
            BuyMax = 9150, BuyMaxDate = now.AddMinutes(-40).ToString("yyyy-MM-ddTHH:mm:ss")
        };
        int qualityUsed;
        var rows = QuoteTableModel.Build(new[] { mixed }, new[] { "Bridgewatch" }, now, out qualityUsed);
        LogicTests.Eq("报价卡: 单城构建 1 行", rows.Count, 1);
        var row = rows[0];
        LogicTests.Eq("时效: 买入侧新鲜", row.BuyFreshness, QuoteFreshness.Fresh);
        LogicTests.Eq("时效: 卖出侧过期", row.SellFreshness, QuoteFreshness.Expired);
        LogicTests.Eq("时效: 行级取较新一路", row.Freshness, QuoteFreshness.Fresh);
        LogicTests.Eq("时效: 最差一侧 = 过期", QuoteTableModel.WorstSideFreshness(row), QuoteFreshness.Expired);

        int excluded;
        var ex = QuoteTableModel.Extremes(rows, out excluded);
        LogicTests.Eq("双侧极值: 买价参与最低买入", ex.MinBuy, 8420L);
        LogicTests.Eq("双侧极值: 过期卖价不参与最高卖出", ex.MaxSell, 0L);
        LogicTests.Eq("双侧极值: 被排除格数按侧计（仅卖侧 1 格）", excluded, 1);

        // ② 缺城：整行无数据 + 两侧都计排除
        var rows2 = QuoteTableModel.Build(new[] { mixed }, new[] { "Bridgewatch", "Lymhurst" }, now, out qualityUsed);
        LogicTests.Eq("缺城: 城市行数按城市列表补齐", rows2.Count, 2);
        var missing = rows2.Find(r => r.City == "Lymhurst");
        LogicTests.Check("缺城: 明确画无数据（不是 0）",
            missing != null && missing.BuyKind == QuoteCellKind.NoData && missing.SellKind == QuoteCellKind.NoData,
            "missing city kind");
        LogicTests.Eq("缺城: 两侧时效都算过期", missing.BuyFreshness, QuoteFreshness.Expired);
        QuoteTableModel.Extremes(rows2, out excluded);
        LogicTests.Eq("缺城: 极值排除格数 = 卖侧1 + 缺城2", excluded, 3);

        // ③ 0 值四形态（字段缺失 ≠ 字段为 0）
        LogicTests.Eq("0 值: 字段缺失 → 无数据", QuoteTableModel.KindOf(0, "", false), QuoteCellKind.NoData);
        LogicTests.Eq("0 值: 字段存在 + 有时间戳 → 暂无挂单",
            QuoteTableModel.KindOf(0, now.AddMinutes(-3).ToString("yyyy-MM-ddTHH:mm:ss"), true), QuoteCellKind.NoOrders);
        LogicTests.Eq("0 值: 字段存在 + 无时间戳 → 无数据", QuoteTableModel.KindOf(0, "", true), QuoteCellKind.NoData);
        LogicTests.Eq("0 值: 有价 → 正常价格", QuoteTableModel.KindOf(1234, "", true), QuoteCellKind.Value);
        var fieldMissing = new MarketQuote
        {
            ItemId = "T4_BAG", City = "Caerleon", Quality = 1,
            SellMin = 0, SellMinDate = now.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ss"),
            BuyMax = 0, BuyMaxDate = now.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ss"),
            SellMinPresent = false, BuyMaxPresent = false
        };
        rows = QuoteTableModel.Build(new[] { fieldMissing }, new[] { "Caerleon" }, now, out qualityUsed);
        LogicTests.Check("0 值: 字段缺失（即使有日期）按无数据渲染",
            rows[0].BuyKind == QuoteCellKind.NoData && rows[0].SellKind == QuoteCellKind.NoData,
            "field missing kind");

        // ④ 单调时钟 + 紧凑相对时间（报价卡时间列同源）
        LogicTests.Eq("时间文案: 30 秒 = 刚刚", QuoteTableModel.CellTimeCompact(now.AddSeconds(-30), now), "刚刚");
        LogicTests.Eq("时间文案: 5 分", QuoteTableModel.CellTimeCompact(now.AddMinutes(-5), now), "5分");
        LogicTests.Eq("时间文案: 3 时", QuoteTableModel.CellTimeCompact(now.AddHours(-3), now), "3时");
        LogicTests.Eq("时间文案: 2 天", QuoteTableModel.CellTimeCompact(now.AddDays(-2), now), "2天");
        LogicTests.Eq("时间文案: 无时间戳 = 无", QuoteTableModel.CellTimeCompact(null, now), "无");
        var sideRow = new QuoteViewRow
        {
            City = "Bridgewatch", BuyKind = QuoteCellKind.Value, SellKind = QuoteCellKind.Value,
            BuyDataAt = now.AddMinutes(-5), SellDataAt = now.AddHours(-2)
        };
        LogicTests.Eq("时间列: 买卖两路各自文案", QuoteTableModel.SideTimeSummary(sideRow, now), "↓5分 · ↑2时");
        var noDataRow = new QuoteViewRow { City = "X", BuyKind = QuoteCellKind.NoData, SellKind = QuoteCellKind.NoData };
        LogicTests.Eq("时间列: 整行无数据文案", QuoteTableModel.SideTimeSummary(noDataRow, now), "无数据");

        // ⑤ 时效三档 15/30 分钟（设计规范 §六）
        LogicTests.Eq("时效档: 15 分钟内 = 可用", QuoteTableModel.Classify(TimeSpan.FromMinutes(15)), QuoteFreshness.Fresh);
        LogicTests.Eq("时效档: 15-30 分钟 = 偏旧", QuoteTableModel.Classify(TimeSpan.FromMinutes(16)), QuoteFreshness.Aging);
        LogicTests.Eq("时效档: 30 分钟 = 偏旧", QuoteTableModel.Classify(TimeSpan.FromMinutes(30)), QuoteFreshness.Aging);
        LogicTests.Eq("时效档: 30 分钟以上 = 过期", QuoteTableModel.Classify(TimeSpan.FromMinutes(31)), QuoteFreshness.Expired);
        LogicTests.Eq("时效档: 24h 陈旧阈值", QuoteTableModel.StaleWindow, TimeSpan.FromHours(24));

        // ⑥ 整卡九态里的 M1/M3/M4（0 值四形态的汇总判定）
        var normalRow = new QuoteViewRow
        {
            City = "Caerleon", BuyKind = QuoteCellKind.Value, SellKind = QuoteCellKind.Value,
            BuyPrice = 100, SellPrice = 100, BuyFreshness = QuoteFreshness.Fresh, SellFreshness = QuoteFreshness.Fresh
        };
        LogicTests.Eq("整卡状态: 有买有卖 = Rows", QuoteTableModel.StateOf(new[] { normalRow }), QuoteTableModel.CardState.Rows);
        var sellEmptyRow = new QuoteViewRow
        {
            City = "Caerleon", BuyKind = QuoteCellKind.Value, SellKind = QuoteCellKind.NoOrders,
            BuyPrice = 100, BuyFreshness = QuoteFreshness.Fresh, SellFreshness = QuoteFreshness.Expired
        };
        LogicTests.Eq("整卡状态: 全部卖价 0 = AllSellEmpty（M3）",
            QuoteTableModel.StateOf(new[] { sellEmptyRow }), QuoteTableModel.CardState.AllSellEmpty);
        var noListingRow = new QuoteViewRow
        {
            City = "Caerleon", BuyKind = QuoteCellKind.NoOrders, SellKind = QuoteCellKind.NoOrders,
            BuyFreshness = QuoteFreshness.Expired, SellFreshness = QuoteFreshness.Expired
        };
        LogicTests.Eq("整卡状态: 整件无挂单 = NoListingAtAll（M4）",
            QuoteTableModel.StateOf(new[] { noListingRow }), QuoteTableModel.CardState.NoListingAtAll);
    }

    // ─────────────────────────────────────────────────────────────
    // 8) MarketDb：锁/快照/原子写/附魔主键/无上报过滤
    // ─────────────────────────────────────────────────────────────
    static void RunMarketDbTests()
    {
        var db = new MarketDb();
        db.Upsert(new MarketQuote { ItemId = "T4_BAG", City = "Caerleon", Quality = 1, SellMin = 100 });
        db.Upsert(new MarketQuote { ItemId = "T4_BAG@1", City = "Caerleon", Quality = 1, SellMin = 200 });
        LogicTests.Eq("本地库: 附魔变体与本体是两行", db.Rows.Count, 2);
        LogicTests.Eq("本地库: 本体价", db.Find("T4_BAG", "Caerleon", 1).SellMin, 100L);
        LogicTests.Eq("本地库: 附魔价（不互相覆盖）", db.Find("T4_BAG@1", "Caerleon", 1).SellMin, 200L);
        LogicTests.Eq("本地库: 查询附魔变体只返回该行",
            db.Query(new List<string> { "T4_BAG@1" }, new List<string> { "Caerleon" }).Count, 1);

        // 无上报过滤：两侧都为 0 丢弃；单侧 0（黑市只有买单）保留
        int before = db.Rows.Count;
        db.Upsert(new MarketQuote { ItemId = "T4_ROCK", City = "Caerleon", Quality = 1, SellMin = 0, BuyMax = 0 });
        LogicTests.Eq("本地库: 两侧无上报不落库", db.Rows.Count, before);
        LogicTests.Eq("本地库: 无上报过滤计数", db.SkippedNoDataCount, 1);
        db.Upsert(new MarketQuote
        {
            ItemId = "T4_BAG", City = MarketCities.BlackMarket, Quality = 1,
            SellMin = 0, BuyMax = 5000, BuyMaxDate = "2026-11-08T11:00:00",
            SellMinPresent = false, BuyMaxPresent = true
        });
        LogicTests.Eq("本地库: 黑市单侧 0 行保留（跑商黑市腿）", db.Rows.Count, before + 1);
        LogicTests.Check("本地库: 黑市行可按城市查到",
            db.Find("T4_BAG", "black market", 1) != null, "black market row missing");

        // 快照语义：调用方改快照不影响库
        var snapshot = db.Rows;
        snapshot.Clear();
        LogicTests.Eq("本地库: Rows 是快照（清空快照不影响库）", db.Rows.Count, before + 1);

        string dir = Path.Combine(Path.GetTempPath(), "ac_mkt_db_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "market.csv");
            db.Save(path);
            LogicTests.Check("本地库: 原子写完成后无 .tmp 残留", !File.Exists(path + ".tmp"), ".tmp left");
            string csv = File.ReadAllText(path);
            LogicTests.Check("本地库: CSV 只含一行表头", csv.Split('\n').Count(l => l.Contains("item_id")) == 1, "header");
            var loaded = MarketDb.Load(path);
            LogicTests.Eq("本地库: 重载后行数一致", loaded.Rows.Count, db.Rows.Count);
            LogicTests.Eq("本地库: 重载保留附魔行价", loaded.Find("T4_BAG@1", "Caerleon", 1).SellMin, 200L);
            LogicTests.Check("本地库: 重载保留黑市单侧 0 行",
                loaded.Find("T4_BAG", MarketCities.BlackMarket, 1) != null, "black market row lost");

            // 二次保存（替换路径）仍原子且可用
            db.Save(path);
            var loaded2 = MarketDb.Load(path);
            LogicTests.Eq("本地库: 二次原子保存后仍可解析", loaded2.Rows.Count, db.Rows.Count);
            LogicTests.Check("本地库: 二次保存后无 .tmp 残留", !File.Exists(path + ".tmp"), ".tmp left");

            // 并发烟雾：4 写线程 + 2 读线程（Rows/Query/Count/Save），断言不抛异常、最终行数正确
            var errors = new List<string>();
            var done = new ManualResetEventSlim(false);
            const int PerThread = 400;
            int expected = 4 * PerThread;
            var writers = new List<Thread>();
            for (int w = 0; w < 4; w++)
            {
                int wid = w;
                var t = new Thread(delegate ()
                {
                    try
                    {
                        for (int i = 0; i < PerThread; i++)
                        {
                            db.Upsert(new MarketQuote
                            {
                                ItemId = "T5_ITEM_" + wid + "_" + i, City = "Caerleon",
                                Quality = 1 + (i % 5), SellMin = 100 + i
                            });
                        }
                    }
                    catch (Exception ex) { lock (errors) errors.Add(ex.GetType().Name + ":" + ex.Message); }
                });
                writers.Add(t);
            }
            var readers = new List<Thread>();
            for (int r = 0; r < 2; r++)
            {
                var t = new Thread(delegate ()
                {
                    try
                    {
                        while (!done.IsSet)
                        {
                            var snap = db.Rows;
                            db.Query(new List<string> { "T5_ITEM_0_0" }, new List<string> { "Caerleon" });
                            int ignored = db.Count;
                            db.KnownItemIds();
                        }
                    }
                    catch (Exception ex) { lock (errors) errors.Add(ex.GetType().Name + ":" + ex.Message); }
                });
                readers.Add(t);
            }
            foreach (var t in readers) { t.IsBackground = true; t.Start(); }
            foreach (var t in writers) t.Start();
            foreach (var t in writers) t.Join(30000);
            done.Set();
            foreach (var t in readers) t.Join(10000);
            LogicTests.Check("本地库并发: 4 写 + 2 读不抛异常", errors.Count == 0, string.Join(" | ", errors));
            LogicTests.Eq("本地库并发: 最终行数 = 原有 + 写入", db.Rows.Count, before + 1 + expected);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 9) 单调时钟（相对时间统一函数的数据源）
    // ─────────────────────────────────────────────────────────────
    static void RunClockTests()
    {
        var a = MarketClock.UtcNow;
        var b = MarketClock.UtcNow;
        LogicTests.Check("时钟: 同一进程内不回退", b >= a, "b<a");
        var wall = DateTimeOffset.UtcNow;
        LogicTests.Check("时钟: 与墙钟量级一致（±5 分钟）",
            Math.Abs((b - wall).TotalMinutes) < 5, "clock=" + b.ToString("O") + " wall=" + wall.ToString("O"));
        LogicTests.Eq("时钟: 时效档按单调 now 判定",
            QuoteTableModel.Classify(TimeSpan.FromMinutes(10)), QuoteFreshness.Fresh);
    }
}
