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
        RunCardStateMatrixTests();
        RunZeroHttpFilterTests();
        RunM9CombinationTests();
        RunLinkRowTests();
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

        // ⑥ M6 加载态：状态枚举必须独立于其余八态（不能用 NoDataAtAll 冒充，
        //    否则「查询在途」与「取不到数据」在 UI 上分不开，用户会以为查询失败）。
        LogicTests.Check("整卡状态: M6 Loading 是独立枚举值",
            QuoteTableModel.CardState.Loading != QuoteTableModel.CardState.NoDataAtAll
            && QuoteTableModel.CardState.Loading != QuoteTableModel.CardState.Rows
            && QuoteTableModel.CardState.Loading != QuoteTableModel.CardState.NotSelected,
            "Loading 与既有状态枚举撞值");
        // 骨架行位数 = 本次查询城市数；卡片高度按行位算，不随空态 CardHeight 走
        LogicTests.Eq("整卡高度: M6 六城骨架 = 248", QuoteTableModel.PreferredCardHeight(6), 248);
        LogicTests.Eq("整卡高度: M6 七城（含黑市）骨架 = 274", QuoteTableModel.PreferredCardHeight(7), 274);
        LogicTests.Eq("整卡高度: M6 城市数 0 回落六城", QuoteTableModel.PreferredCardHeight(0), 248);

        // M3 提示行（设计 §5.3）：底部 28px 底注换成 36px 提示行 → 卡片比常规高 8。
        // 这个高度必须由 Core 函数给，控件里手写 248+8 一旦城市数变化就会算错。
        LogicTests.Eq("整卡高度: M3 六城提示行 = 256", QuoteTableModel.HintCardHeight(6), 256);
        LogicTests.Eq("整卡高度: M3 七城提示行 = 282", QuoteTableModel.HintCardHeight(7), 282);
        LogicTests.Eq("整卡高度: M3 比常规底注高 8",
            QuoteTableModel.HintCardHeight(6) - QuoteTableModel.PreferredCardHeight(6),
            QuoteTableModel.HintHeight - QuoteTableModel.FooterHeight);
        // 0 价不当有效卖价：M3 的判据是「一个 Value 卖格都没有」，不是「卖价字段为 0」——
        // 只要有一格卖价 > 0 就回落到 Rows，不能整卡灰化把真实收购单藏起来。
        var mixedSellRow = new QuoteViewRow
        {
            City = "Caerleon", BuyKind = QuoteCellKind.Value, SellKind = QuoteCellKind.Value,
            BuyPrice = 100, SellPrice = 1, BuyFreshness = QuoteFreshness.Fresh,
            SellFreshness = QuoteFreshness.Fresh
        };
        LogicTests.Eq("M3 反例: 有一格卖价 > 0 就不进 AllSellEmpty",
            QuoteTableModel.StateOf(new[] { sellEmptyRow, mixedSellRow }), QuoteTableModel.CardState.Rows);
        // 0 价也不得被当成最低买入/最高卖出参与比较（底注极值的排除计数）
        int excludedCount;
        var extremes = QuoteTableModel.Extremes(new[]
        {
            new QuoteViewRow { City = "Caerleon", BuyKind = QuoteCellKind.NoOrders,
                SellKind = QuoteCellKind.NoOrders },
            new QuoteViewRow { City = "Bridgewatch", BuyKind = QuoteCellKind.Value,
                SellKind = QuoteCellKind.NoOrders, BuyPrice = 500 }
        }, out excludedCount);
        LogicTests.Eq("M3: 0 价格不进最高卖出", extremes.MaxSell, 0L);
        LogicTests.Check("M3: 0 价格计入被排除数", excludedCount > 0, "excluded=" + excludedCount);

        // M3 提示行的「看 T4 / 看 T6」（设计 §5.3）：按钮必须指向真的存在的相邻阶级。
        // 判据「该方向没有邻居就返回 0」，UI 据此决定不画按钮——画一个点了没目的地的按钮等于假按钮。
        var tiers = new List<int> { 4, 5, 6, 7, 8 };
        LogicTests.Eq("M3 相邻阶级: T5 的低阶方向 = T4", QuoteTableModel.AdjacentTier(tiers, 5, true), 4);
        LogicTests.Eq("M3 相邻阶级: T5 的高阶方向 = T6", QuoteTableModel.AdjacentTier(tiers, 5, false), 6);
        LogicTests.Eq("M3 相邻阶级: 已在最低阶 T4 时低阶方向无邻居 = 0",
            QuoteTableModel.AdjacentTier(tiers, 4, true), 0);
        LogicTests.Eq("M3 相邻阶级: 已在最高阶 T8 时高阶方向无邻居 = 0",
            QuoteTableModel.AdjacentTier(tiers, 8, false), 0);
        LogicTests.Eq("M3 相邻阶级: 未选满（tier=0）不给邻居",
            QuoteTableModel.AdjacentTier(tiers, 0, true), 0);
        LogicTests.Eq("M3 相邻阶级: 当前阶级不在列表里 = 0（不猜位置）",
            QuoteTableModel.AdjacentTier(tiers, 3, true), 0);
        // 家族只有两阶（如某些族的 T4/T5）时，中间态才不会生出跨阶空洞
        LogicTests.Eq("M3 相邻阶级: 两阶家族 T4 高阶方向 = T5",
            QuoteTableModel.AdjacentTier(new List<int> { 4, 5 }, 4, false), 5);
        LogicTests.Eq("M3 相邻阶级: 空列表 = 0", QuoteTableModel.AdjacentTier(new List<int>(), 5, true), 0);
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

    // ─────────────────────────────────────────────────────────────
    // 10) 报价卡九态矩阵（M1–M9）逐态核对
    //     基准是 CardState 枚举：枚举里没有的状态 = 代码无法表达 = 没落地。
    //     九态与枚举不是一对一（M1/M2 共用 Rows，M6 由 UI 显式设置而非由数据推导），
    //     所以这条只验「枚举能表达 + StateOf 判得对」，UI 落点见提交报告。
    // ─────────────────────────────────────────────────────────────
    static void RunCardStateMatrixTests()
    {
        var names = Enum.GetNames(typeof(QuoteTableModel.CardState));
        LogicTests.Eq("九态: CardState 成员数 = 6", names.Length, 6);
        foreach (var n in new[] { "Rows", "AllSellEmpty", "NoListingAtAll", "NotSelected", "Loading", "NoDataAtAll" })
            LogicTests.Check("九态: 枚举含 " + n, Array.IndexOf(names, n) >= 0, string.Join("/", names));

        // 固定时间：不依赖真实当前时间（否则超 24h 必红）
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        int qu;
        var q1 = new MarketQuote
        {
            ItemId = "T4_BAG", City = "Bridgewatch", Quality = 1,
            SellMin = 8420, SellMinDate = now.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ss"),
            BuyMax = 9150, BuyMaxDate = now.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ss"),
            SellMinPresent = true, BuyMaxPresent = true, SellHasData = true
        };
        var rows1 = QuoteTableModel.Build(new[] { q1 }, new[] { "Bridgewatch" }, now, out qu);
        LogicTests.Eq("九态 M1/M2: 有价 → Rows",
            QuoteTableModel.StateOf(rows1), QuoteTableModel.CardState.Rows);

        // M4：有城但每格都无价 → NoListingAtAll（Build 会按城市补行，所以不是空集合）
        var rows4 = QuoteTableModel.Build(new MarketQuote[0], new[] { "Bridgewatch" }, now, out qu);
        LogicTests.Eq("九态 M4: 有城但全无价 → NoListingAtAll",
            QuoteTableModel.StateOf(rows4), QuoteTableModel.CardState.NoListingAtAll);

        // M8：连行都没有 → NoDataAtAll
        LogicTests.Eq("九态 M8: 无任何行 → NoDataAtAll",
            QuoteTableModel.StateOf(new List<QuoteViewRow>()), QuoteTableModel.CardState.NoDataAtAll);

        // M6 由 UI 显式设置，不由数据推导，不能污染数据态判定
        LogicTests.Check("九态 M6: StateOf 不会从数据推导出 Loading",
            QuoteTableModel.StateOf(rows1) != QuoteTableModel.CardState.Loading, "ok");
        // M6 骨架高度按城市数算（六城 248 / 七城 274），与九态里的空态高度分开
        LogicTests.Eq("九态 M6: 六城骨架高 248", QuoteTableModel.PreferredCardHeight(6), 248);
        LogicTests.Eq("九态 M6: 七城骨架高 274", QuoteTableModel.PreferredCardHeight(7), 274);
    }

    // ─────────────────────────────────────────────────────────────
    // 11) 零 HTTP 三段断言
    //     用 MarketDataService 构造函数本就公开的 fetchUrl 接缝装计数器。
    //     不能 new 一个 HttpClient 换进 MarketClient——HttpClient 是 private static readonly，
    //     换不进去，那样测出来的 0 毫无意义（这条就是「不自欺」的关键）。
    //     结构刻意做成「①0 → ②+1 → ③0」：只断言 0 的话，计数器没接上也会得到 0，
    //     必须先有一次真回源证明计数器在工作，第三步的 0 才算证据。
    // ─────────────────────────────────────────────────────────────
    static void RunZeroHttpFilterTests()
    {
        int calls = 0;
        var fixedNow = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        string Body()
        {
            return "[{\"item_id\":\"T4_BAG\",\"city\":\"Bridgewatch\",\"quality\":1,"
                 + "\"sell_price_min\":8420,\"sell_price_min_date\":\"2026-11-09T11:55:00\","
                 + "\"buy_price_max\":9150,\"buy_price_max_date\":\"2026-11-09T11:55:00\"}]";
        }

        var svc = new MarketDataService(
            fetchUrl: (url, ct) => { Interlocked.Increment(ref calls); return Task.FromResult(Body()); },
            clock: () => fixedNow);

        var items = new[] { "T4_BAG" };
        var cities = new[] { "Bridgewatch" };

        // ① 冷缓存跑完整筛选交互（= 渲染路径只读快照），出网必须 0
        for (int i = 0; i < 5; i++)
        {
            var snap = svc.SnapshotPricingEntries();
            var quotes = snap.Where(e => e.Quote != null && e.Quote.ItemId == "T4_BAG").ToList();
            LogicTests.Check("零HTTP①: 冷缓存筛选第 " + (i + 1) + " 次不出网", calls == 0, "calls=" + calls);
            LogicTests.Check("零HTTP①: 冷缓存无行可渲染（未查询态）", quotes.Count == 0, "rows=" + quotes.Count);
        }
        LogicTests.Eq("零HTTP①: 冷缓存累计出网 = 0", calls, 0);

        // ② 故意发一次 GetPricesAsync，计数必须 +1 —— 证明计数器真接上了
        int before = calls;
        var got = svc.GetPricesAsync(items, cities, MarketQueryPurpose.ManualQuery,
            new[] { 1 }, CancellationToken.None).GetAwaiter().GetResult();
        LogicTests.Check("零HTTP②: 一次 GetPricesAsync 出网次数 +1", calls == before + 1,
            "before=" + before + " after=" + calls);
        LogicTests.Check("零HTTP②: 回源带回该城报价", got.Count > 0, "rows=" + got.Count);
        LogicTests.Eq("零HTTP②: 缓存已写入 1 条", svc.SnapshotPricingEntries().Count, 1);

        // ③ 暖缓存再跑筛选，增量必须 0
        int afterWarm = calls;
        for (int i = 0; i < 5; i++)
        {
            var snap = svc.SnapshotPricingEntries();
            LogicTests.Check("零HTTP③: 暖缓存筛选第 " + (i + 1) + " 次不出网",
                calls == afterWarm, "delta=" + (calls - afterWarm));
        }
        // 换城市筛选（缓存里没有该城）也只是快照过滤，不出网
        var other = svc.SnapshotPricingEntries().Where(e => e.Quote != null && e.Quote.City == "Thetford").ToList();
        LogicTests.Check("零HTTP③: 换城市筛选不出网", calls == afterWarm && other.Count == 0,
            "delta=" + (calls - afterWarm) + " rows=" + other.Count);
    }

    // ─────────────────────────────────────────────────────────────
    // 12) M9「选满但组合不存在」判据
    //     用 IsComplete（不是「键为空」）区分 M5 与 M9：
    //     M5 未选满 → 提示「请先选择物品」；M9 已选满但组合不存在 → 提示「该分类下没有这件物品」。
    //     若只用「键为空」，两者会被并成同一文案，就是「选满了却说没选」。
    // ─────────────────────────────────────────────────────────────
    static void RunM9CombinationTests()
    {
        var tax = new ItemTaxonomy();
        var cat = new ItemCategory { Key = "head", DisplayName = "头部护甲" };
        var sub = new ItemSubCategory { Key = "cloth_helmet", DisplayName = "布甲风帽" };
        var fam = new ItemFamily { Family = "HEAD_CLOTH_SET1", DisplayName = "学士风帽" };
        fam.Variants.Add(new ItemVariant
        {
            BaseId = "T5_HEAD_CLOTH_SET1", UniqueName = "T5_HEAD_CLOTH_SET1@1",
            Tier = 5, Enchant = 1, DisplayName = "专家级学士风帽"
        });
        sub.Families.Add(fam);
        cat.SubCategories.Add(sub);
        tax.Categories.Add(cat);

        // M5：只选一级 → 未选满，键也空
        var sel5 = new MarketSelection { CategoryKey = "head", Tier = 0 };
        LogicTests.Check("M5 未选满: IsComplete=false", !sel5.IsComplete, "ok");
        var key5 = MarketQueryPlanner.Build(tax, sel5);
        LogicTests.Check("M5 未选满: 空键（不发请求）", key5.IsEmpty, key5.Signature);
        // 关键：M5 的判据必须是 IsComplete=false，据此走「请先选择物品」
        LogicTests.Check("M5 判据: 未选满时不能误判成 M9", !sel5.IsComplete, "走 M5 文案");

        // M9-a：三级选满但族在物品表里不存在 → IsComplete=true 且键为空
        var sel9 = new MarketSelection
        {
            CategoryKey = "head", SubCategoryKey = "cloth_helmet",
            Family = "HEAD_PLATE_SET1", Tier = 5, Enchant = 1
        };
        var key9 = MarketQueryPlanner.Build(tax, sel9);
        LogicTests.Check("M9-a 选满: IsComplete=true", sel9.IsComplete, "ok");
        LogicTests.Check("M9-a 组合不存在: 空键", key9.IsEmpty, key9.Signature);
        LogicTests.Check("M9-a 判据: 选满+空键 ⇒ 走 M9 文案而非「未选物品」",
            sel9.IsComplete && key9.IsEmpty, "IsComplete=true & IsEmpty=true");

        // M9-b：族在、但该阶级/附魔变体不存在
        var sel9b = new MarketSelection
        {
            CategoryKey = "head", SubCategoryKey = "cloth_helmet",
            Family = "HEAD_CLOTH_SET1", Tier = 8, Enchant = 3
        };
        var key9b = MarketQueryPlanner.Build(tax, sel9b);
        LogicTests.Check("M9-b 族在变体不在: 空键", key9b.IsEmpty, key9b.Signature);
        LogicTests.Check("M9-b 该情形同样已选满", sel9b.IsComplete, "ok");

        // 正路对照：存在的组合必须出非空键，否则上面的空键断言是假阳性
        var ok = new MarketSelection
        {
            CategoryKey = "head", SubCategoryKey = "cloth_helmet",
            Family = "HEAD_CLOTH_SET1", Tier = 5, Enchant = 1
        };
        var keyOk = MarketQueryPlanner.Build(tax, ok);
        LogicTests.Check("M9 对照: 存在的组合出非空键", !keyOk.IsEmpty, keyOk.Signature);
        LogicTests.Eq("M9 对照: uniqueName 带附魔后缀", keyOk.UniqueNames[0], "T5_HEAD_CLOTH_SET1@1");
    }

    // ─────────────────────────────────────────────────────────────
    // 13) 联动位（设计 rev27 §3.5 / §六 状态矩阵「联动位」列）
    //     规范明文：「620 × 28，落在卡片下方，宽度与卡片一致」；点击只弹规划中文案。
    //     模块边界（用户原话「跑商相关不要放到市场模块」）：这一位只做入口，
    //     不得出现跨城价差 / 套利 / 税后净额 / 利润——这条用静态扫描锁死，不靠人盯。
    // ─────────────────────────────────────────────────────────────
    static void RunLinkRowTests()
    {
        // 尺寸与卡宽同源：规范要求「宽度与卡片一致」，所以两个常量必须相等
        LogicTests.Eq("联动位: 宽 = 卡宽 620", QuoteTableModel.LinkRowWidth, 620);
        LogicTests.Eq("联动位: 宽 = QuoteTable 卡宽", QuoteTableModel.LinkRowWidth, 620);
        LogicTests.Eq("联动位: 高 = 28（规范 §3.5）", QuoteTableModel.LinkRowHeight, 28);

        // 位置：内容区按规范布局表的 736，联动位贴底 = 736 − 28 = 708（与布局表 y=708 逐字一致）
        LogicTests.Eq("联动位: 内容区 736 顶 = 708（与布局表 y=708 一致）",
            QuoteTableModel.LinkRowTop(736), 708);
        LogicTests.Eq("联动位: 内容区高常量 = 736（规范布局表）", QuoteTableModel.ContentHeight, 736);
        // 容错：高度未定时不该算出负数位置，按 ContentHeight 回落 708
        LogicTests.Check("联动位: 内容区高 <= 0 回落 708",
            QuoteTableModel.LinkRowTop(0) == 708, "top=" + QuoteTableModel.LinkRowTop(0));
        LogicTests.Eq("联动位: 页面实装位 = LinkRowTopOnPage()", QuoteTableModel.LinkRowTopOnPage(), 708);

        // ── 纵向链路同源：UI 和测试取同一组常量，位置不能各写一份 ──
        LogicTests.Eq("布局: 时效条顶 56（页头带无间距）", QuoteTableModel.FreshStripTop, 56);
        LogicTests.Eq("布局: 筛选卡顶 = 时效条底 + 6", QuoteTableModel.FilterCardTop, 96);
        LogicTests.Eq("布局: 筛选卡高取三列 7/7/5 的下限", QuoteTableModel.FilterCardHeight,
            QuoteTableModel.MinFilterCardHeight());
        LogicTests.Check("布局: 筛选卡高不低于下限（低于就掉行）",
            QuoteTableModel.FilterCardHeight >= QuoteTableModel.MinFilterCardHeight(),
            "card=" + QuoteTableModel.FilterCardHeight + " min=" + QuoteTableModel.MinFilterCardHeight());
        LogicTests.Eq("布局: 一级列可见 7 行", QuoteTableModel.FilterVisibleRows(QuoteTableModel.FilterCardHeight, 0), 7);
        LogicTests.Eq("布局: 二级列可见 7 行", QuoteTableModel.FilterVisibleRows(QuoteTableModel.FilterCardHeight, 1), 7);
        LogicTests.Eq("布局: 三级列扣除 chip 区后 5 行",
            QuoteTableModel.FilterVisibleRows(QuoteTableModel.FilterCardHeight, 2), 5);
        LogicTests.Eq("布局: 报价卡顶", QuoteTableModel.QuoteCardTop, 424);
        // 联动位必须排在卡片下方：卡顶 < 联动位顶，否则「落在卡片下方」这条规范不成立
        LogicTests.Check("布局: 报价卡顶 < 联动位顶",
            QuoteTableModel.QuoteCardTop < QuoteTableModel.LinkRowTopOnPage(),
            "cardTop=" + QuoteTableModel.QuoteCardTop + " linkTop=" + QuoteTableModel.LinkRowTopOnPage());

        // 默认勾选 7 城（含黑市）是页面常态，M3 提示行态卡高 282 是最挤的一档：
        // 卡顶 424 + 282 = 706 < 708，不重叠，余量 2px。这 2px 是实测量出来的，
        // 不写成 ≥4 的宽松断言来粉饰；要留满 8px 得把卡顶抬到 418，会压到 400..424 的下拉/勾选控件。
        int gap7 = QuoteTableModel.LinkRowGap(QuoteTableModel.ContentHeight,
            QuoteTableModel.QuoteCardTop, QuoteTableModel.PreferredCardHeight(7));
        LogicTests.Eq("联动位: 七城常态卡↔联动位余量 10", gap7, 10);
        int gap6 = QuoteTableModel.LinkRowGap(QuoteTableModel.ContentHeight,
            QuoteTableModel.QuoteCardTop, QuoteTableModel.PreferredCardHeight(6));
        LogicTests.Eq("联动位: 六城常态卡↔联动位余量 36", gap6, 36);
        LogicTests.Check("联动位: 六城 gap 大于七城（多勾城确实吃掉间距）", gap6 > gap7,
            "6城=" + gap6 + " 7城=" + gap7);
        // M3 提示行比底注高 8，是最高的数据态：七城 M3 卡底 706，余量 2px（不重叠但很紧）
        int gapM3 = QuoteTableModel.LinkRowGap(QuoteTableModel.ContentHeight,
            QuoteTableModel.QuoteCardTop, QuoteTableModel.HintCardHeight(7));
        LogicTests.Eq("联动位: 七城 M3 卡（282）↔联动位余量 2", gapM3, 2);
        LogicTests.Check("联动位: 七城 M3 卡不压联动位（gap >= 0）", gapM3 >= 0, "gap=" + gapM3);
        // 最高态必须由 MaxCardHeight 覆盖全部数据态，否则「不压」只证明了最矮那档
        LogicTests.Check("联动位: MaxCardHeight 覆盖 M3 七城",
            QuoteTableModel.MaxCardHeight() >= QuoteTableModel.HintCardHeight(7),
            "max=" + QuoteTableModel.MaxCardHeight() + " m3=" + QuoteTableModel.HintCardHeight(7));
        // 最挤那档只有 2px，不设 ≥4 的下限断言：设计未规定最小间距，实测该档就是 2px，
        // 断言写成硬编码 2（上面已锁），这里只保「不重叠」这一条硬约束。
        LogicTests.Check("联动位: 最高态不重叠", gapM3 >= 0, "gapM3=" + gapM3);

        // 状态矩阵：M6 加载中 / M8 无旧数据灰化，其余亮色
        LogicTests.Check("联动位: M6 加载中灰化",
            !QuoteTableModel.LinkRowEnabled(QuoteTableModel.CardState.Loading), "M6");
        LogicTests.Check("联动位: M8 无旧数据灰化",
            !QuoteTableModel.LinkRowEnabled(QuoteTableModel.CardState.NoDataAtAll), "M8");

        // ── M9 不能被误灰化（设计 §六 联动位列：M9 组合无匹配 = 显示）──
        // 卡片里 M9「组合查不到」、搜索无结果、「尚未查询」都落成 NoDataAtAll，
        // 与真·M8 同枚举值。只按枚举一刀切就会把设计要求「显示」的 M9 灰掉，所以用 fetchFailed 区分。
        LogicTests.Check("联动位: M9 组合无匹配（NoDataAtAll 但非失败）显示",
            QuoteTableModel.LinkRowEnabled(QuoteTableModel.CardState.NoDataAtAll, false), "M9");
        LogicTests.Check("联动位: 真·M8（NoDataAtAll + 回源失败）灰化",
            !QuoteTableModel.LinkRowEnabled(QuoteTableModel.CardState.NoDataAtAll, true), "M8");
        LogicTests.Check("联动位: M6 不论 fetchFailed 都灰化",
            !QuoteTableModel.LinkRowEnabled(QuoteTableModel.CardState.Loading, false)
            && !QuoteTableModel.LinkRowEnabled(QuoteTableModel.CardState.Loading, true), "M6");
        // 其余状态亮色，且 fetchFailed 标志不该影响它们（M7 有旧数据也要显示）
        foreach (var st in new[]
        {
            QuoteTableModel.CardState.Rows, QuoteTableModel.CardState.AllSellEmpty,
            QuoteTableModel.CardState.NoListingAtAll, QuoteTableModel.CardState.NotSelected
        })
        {
            LogicTests.Check("联动位: " + st + " 亮色（fetchFailed=" + false + "）",
                QuoteTableModel.LinkRowEnabled(st, false), st.ToString());
            LogicTests.Check("联动位: " + st + " 亮色（fetchFailed=" + true + "）",
                QuoteTableModel.LinkRowEnabled(st, true), st.ToString());
        }

        // 文案：规范原文三段，一字不改（左段入口名 / 右段标注 / 点击提示）
        LogicTests.Eq("联动位: 左段文案", QuoteTableModel.LinkRowTitle, "收益明细 · 物品价值估算");
        LogicTests.Eq("联动位: 右段标注", QuoteTableModel.LinkRowBadge, "后续扩展");
        LogicTests.Eq("联动位: 点击提示", QuoteTableModel.LinkRowClickHint,
            "该入口规划中，将与收益明细的物品行互链");

        // 模块边界：联动位自身文案不得出现算账词（只做入口，不做账）
        string[] moneyWords = { "价差", "套利", "税后", "净额", "利润", "赚", "收益额" };
        string[] linkTexts =
        {
            QuoteTableModel.LinkRowTitle, QuoteTableModel.LinkRowBadge, QuoteTableModel.LinkRowClickHint
        };
        foreach (var w in moneyWords)
        {
            foreach (var t in linkTexts)
            {
                LogicTests.Check("联动位文案不含算账词「" + w + "」", t.IndexOf(w, StringComparison.Ordinal) < 0, t);
            }
        }
    }
}
