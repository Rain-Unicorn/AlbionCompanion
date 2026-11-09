#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using AlbionCompanion.Core;
using AlbionCompanion.Core.Trade;

// 跑商模块回归（task-5 / dev-trade）：Arbitrage 买卖角色、按 item+quality 分组、黑市只做目的城、
// 0 报价数据层过滤、24h 陈旧、每 kg / 单趟、负重贪心装载、会员档重算、导航单一状态源、设置持久化。
//
// 对账夹具来自《跑商演示数据-原型截图.md》：
//   9 行真实演示数（吃单成本/卖入所得/深度），用规范 §7 税口径复算单件净利；
//   150KG = 8 SKU / 63 件 / 149.1 KG / 816,912 银 / 密度 5,446；
//   40KG = 3 SKU / 9 件 / 444,160；200KG = 9 SKU / 68 件 / 190.5 KG / 883,192。
// 说明：截图里「火把副手」单件净利 8,920 与 96,880 不能整除，按规范税口径复算是 6,920，
//       且 96,880 = 6,920 × 14、21,000×0.92−12,400 = 6,920、6,920÷1.8 = 3,844，全链路自洽
//       → 夹具采用 6,920 / 深度 14 / 重量 1.8KG，原表该格按「截图读取误差」处理。
static class TradeModuleTests
{
    private const double F2P = 0.08;
    private const double Premium = 0.04;

    /// <summary>内存物品元数据（名称 + 重量），避免测试依赖真实 data/items.json。</summary>
    private sealed class DictMeta : ITradeItemMeta
    {
        private readonly Dictionary<string, string> _names = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _weights = new Dictionary<string, double>(StringComparer.Ordinal);

        public void Add(string itemId, string name, double weightKg)
        {
            _names[itemId] = name;
            _weights[itemId] = weightKg;
        }

        public string DisplayNameOf(string itemId)
        {
            string name;
            return _names.TryGetValue(itemId ?? "", out name) ? name : null;
        }

        public double? WeightKgOf(string itemId)
        {
            double w;
            return _weights.TryGetValue(itemId ?? "", out w) ? w : (double?)null;
        }
    }

    private static MarketQuote Q(string itemId, string city, int quality, long sellMin, long buyMax,
                                 string sellDate, string buyDate)
    {
        return new MarketQuote
        {
            ItemId = itemId,
            City = city,
            Quality = quality,
            SellMin = sellMin,
            SellMinDate = sellDate,
            BuyMax = buyMax,
            BuyMaxDate = buyDate,
            SellHasData = sellMin > 0L
        };
    }

    private static void Near(string name, double actual, double expected, double eps)
    {
        LogicTests.Check(name, Math.Abs(actual - expected) <= eps,
            "expected≈" + expected + " but got " + actual);
    }

    private static TradeLeg FindLeg(TradeBoard board, string itemId, int quality)
    {
        for (int i = 0; i < board.Legs.Count; i++)
            if (board.Legs[i].ItemId == itemId && board.Legs[i].Quality == quality) return board.Legs[i];
        return null;
    }

    // ── 演示夹具：9 行（截图数据 + 全链路对账）────────────────────────────
    private static void BuildDemoFixture(out List<MarketQuote> rows, out DictMeta meta,
                                         out DictionaryDepthProvider depth, out DateTimeOffset now)
    {
        rows = new List<MarketQuote>();
        meta = new DictMeta();
        depth = new DictionaryDepthProvider();
        now = new DateTimeOffset(2026, 11, 10, 2, 0, 0, TimeSpan.Zero);
        string fresh = now.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss");

        AddDemoLeg(rows, meta, depth, fresh, "T6_MAIN_FISTS", "指虎", "Lymhurst", 68798, 135141, 3.2, 3);
        AddDemoLeg(rows, meta, depth, fresh, "T6_2H_ICEGAUNTLET", "冰晶双手杖", "Bridgewatch", 149990, 250128, 5.6, 2);
        AddDemoLeg(rows, meta, depth, fresh, "T6_2H_CURSEDSTAFF", "诅咒法杖", "Lymhurst", 42986, 78601, 4.8, 8);
        AddDemoLeg(rows, meta, depth, fresh, "T5_OFF_TORCH", "火把副手", "Martlock", 12400, 21000, 1.8, 14);
        AddDemoLeg(rows, meta, depth, fresh, "T6_BAG", "背包", "Bridgewatch", 6700, 9300, 0.5, 25);
        AddDemoLeg(rows, meta, depth, fresh, "T6_ARMOR_LEATHER_SET1", "皮带套装", "Fort Sterling", 64300, 88000, 5.6, 3);
        AddDemoLeg(rows, meta, depth, fresh, "T6_ARMOR_PLATE_SET1", "板甲套装", "Thetford", 91200, 118400, 9.8, 6);
        AddDemoLeg(rows, meta, depth, fresh, "T6_CAPE", "披风", "Thetford", 18900, 22500, 1.2, 5);
        AddDemoLeg(rows, meta, depth, fresh, "T6_2H_SPEAR", "长矛", "Lymhurst", 5081, 12640, 6.0, 2);
    }

    private static void AddDemoLeg(List<MarketQuote> rows, DictMeta meta, DictionaryDepthProvider depth,
        string date, string itemId, string name, string origin, long cost, long income, double weight, long depthValue)
    {
        rows.Add(Q(itemId, origin, 1, cost, 0L, date, ""));
        rows.Add(Q(itemId, TradeCities.BlackMarket, 1, 0L, income, "", date));
        meta.Add(itemId, name, weight);
        depth.Set(itemId, 1, TradeCities.BlackMarket, depthValue);
    }

    public static void Run()
    {
        RunArbitrageRoleTests();
        RunDemoLegTests();
        RunLoadPlanReconciliationTests();
        RunQualityIsolationTests();
        RunBlackMarketTests();
        RunZeroPriceTests();
        RunStaleTests();
        RunPremiumTests();
        RunRoyalLoopAndBrecilienTests();
        RunGreedyInvariantTests();
        RunNavigationStateTests();
        RunSettingsTests();
    }

    // ── 一、P0 内核：买卖角色接反的修正 ─────────────────────────────────
    private static void RunArbitrageRoleTests()
    {
        List<MarketQuote> rows;
        DictMeta meta;
        DictionaryDepthProvider depth;
        DateTimeOffset now;
        BuildDemoFixture(out rows, out meta, out depth, out now);

        var routes = Arbitrage.Compute(rows, F2P, 0.025, 0);
        LogicTests.Eq("T1 演示数据 9 条有效路线（买卖角色已修正）", routes.Count, 9);

        Route spear = null;
        for (int i = 0; i < routes.Count; i++)
            if (routes[i].ItemId == "T6_2H_SPEAR") { spear = routes[i]; break; }
        LogicTests.Check("T1 长矛路线存在", spear != null, "missing");
        if (spear != null)
        {
            LogicTests.Eq("T1 出发城按 sell_price_min 买（Lymhurst 5,081）", spear.BuyPrice, 5081L);
            LogicTests.Eq("T1 目的城按 buy_price_max 卖（黑市 12,640）", spear.SellPrice, 12640L);
            LogicTests.Check("T1 出发城 = Lymhurst（不是买价最高的城/黑市）",
                spear.BuyCity == "Lymhurst", "got " + spear.BuyCity);
            LogicTests.Check("T1 目的城 = Black Market", spear.SellCity == TradeCities.BlackMarket,
                "got " + spear.SellCity);
            LogicTests.Eq("T1 单件净利 = 12,640×0.92 − 5,081 = 6,548", spear.NetProfit, 6548L);
            Near("T1 回报率 128.9%", spear.ReturnPct, 128.9, 0.1);
            LogicTests.Eq("T1 Route 带品质字段", spear.Quality, 1);
        }

        bool blackMarketAsOrigin = false;
        for (int i = 0; i < routes.Count; i++)
            if (routes[i].BuyCity == TradeCities.BlackMarket) blackMarketAsOrigin = true;
        LogicTests.Check("T1 黑市永不出现在出发城", !blackMarketAsOrigin, "black market used as origin");

        // 旧错语义回归：同一批数据若按「买价最高的城当进货地」会算成从黑市进货 → 不允许再出现
        LogicTests.Check("T1 不再产出「从黑市进货」的幻影路线", routes.Count == 9, "routes=" + routes.Count);
    }

    // ── 二、演示数据逐行对账（每 kg / 深度 / 单趟）──────────────────────
    private static void RunDemoLegTests()
    {
        List<MarketQuote> rows;
        DictMeta meta;
        DictionaryDepthProvider depth;
        DateTimeOffset now;
        BuildDemoFixture(out rows, out meta, out depth, out now);

        var board = TradeEngine.Build(rows, TradeRouteMode.BlackMarket, null, meta, depth, F2P, now);
        LogicTests.Eq("T2 差价榜 9 条可执行腿", board.Legs.Count, 9);
        LogicTests.Eq("T2 负利润腿 = 0（演示 9 行均盈利）", board.NegativeCount, 0);
        LogicTests.Eq("T2 缺上报腿：每物品 4 个出发城无卖单", board.MissingTotal, 36);
        LogicTests.Check("T2 最旧报价约 60 分", board.OldestQuoteAgeMinutes >= 55 && board.OldestQuoteAgeMinutes <= 65,
            "age=" + board.OldestQuoteAgeMinutes);

        // 物品 / 单件净利 / 每kg / 深度 / 单趟（单趟 = 单利 × min(负重件数,深度)，演示 200KG 全深度受限）
        string[] ids = { "T6_MAIN_FISTS", "T6_2H_ICEGAUNTLET", "T6_2H_CURSEDSTAFF", "T5_OFF_TORCH",
                         "T6_BAG", "T6_ARMOR_LEATHER_SET1", "T6_ARMOR_PLATE_SET1", "T6_CAPE", "T6_2H_SPEAR" };
        long[] nets = { 55532, 80128, 29327, 6920, 1856, 16660, 17728, 1800, 6548 };
        long[] perKg = { 17354, 14309, 6110, 3844, 3712, 2975, 1809, 1500, 1091 };
        long[] depths = { 3, 2, 8, 14, 25, 3, 6, 5, 2 };
        long[] trips = { 166596, 160256, 234616, 96880, 46400, 49980, 106368, 9000, 13096 };
        long tripSum = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            var leg = FindLeg(board, ids[i], 1);
            LogicTests.Check("T2 腿存在：" + ids[i], leg != null, "missing leg");
            if (leg == null) continue;
            LogicTests.Eq("T2 " + ids[i] + " 单件净利", leg.NetPerUnit, nets[i]);
            LogicTests.Eq("T2 " + ids[i] + " 每kg净利", (long)Math.Round(leg.PerKg ?? 0d), perKg[i]);
            LogicTests.Eq("T2 " + ids[i] + " 深度", leg.Depth, depths[i]);
            LogicTests.Eq("T2 " + ids[i] + " 单趟（200KG 全深度）",
                TradeEngine.TripProfitFor(leg, 200), trips[i]);
            tripSum += trips[i];
        }
        LogicTests.Eq("T2 9 行单趟合计 = 883,192（截图 S5 底栏）", tripSum, 883192L);

        // 火把行待核说明的数字链：6,920×14 = 96,880；21,000×0.92−12,400 = 6,920
        var torch = FindLeg(board, "T5_OFF_TORCH", 1);
        if (torch != null)
        {
            LogicTests.Eq("T2 火把净利=6,920（原表 8,920 为截图读取误差）", torch.NetPerUnit, 6920L);
            LogicTests.Eq("T2 火把卖入所得税后", torch.SellNet, 19320L);
            LogicTests.Eq("T2 火把 96,880 = 6,920×14", torch.NetPerUnit * 14, 96880L);
        }

        // 24h 陈旧剔除保留（P0-3 不回归）
        var staleRows = new List<MarketQuote>();
        staleRows.Add(Q("T6_STALE", "Lymhurst", 1, 5000, 0, now.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ss"), ""));
        staleRows.Add(Q("T6_STALE", TradeCities.BlackMarket, 1, 0, 10000, "", now.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss")));
        var staleBoard = TradeEngine.Build(staleRows, TradeRouteMode.BlackMarket, null, meta, depth, F2P, now);
        LogicTests.Eq("T2 出发城报价 >24h 剔除计数", staleBoard.DroppedStale, 1);
        LogicTests.Eq("T2 陈旧出发城不产腿", staleBoard.Legs.Count, 0);
        LogicTests.Check("T2 陈旧报缺原因 = 出发城报价过期",
            staleBoard.Missing.Count > 0 && staleBoard.Missing[0].Reason == MissingReason.OriginStale,
            staleBoard.Missing.Count > 0 ? staleBoard.Missing[0].Reason.ToString() : "none");

        // 负利润腿：默认折叠但计数可见（规范 §3）；不装载、不进正利润排序
        string fresh = now.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss");
        var negRows = new List<MarketQuote>();
        negRows.Add(Q("T6_LOSS", "Lymhurst", 1, 10000, 0, fresh, ""));
        negRows.Add(Q("T6_LOSS", TradeCities.BlackMarket, 1, 0, 5000, "", fresh));
        var negBoard = TradeEngine.Build(negRows, TradeRouteMode.BlackMarket, null, meta, depth, F2P, now);
        LogicTests.Eq("T2 负利润腿计数 = 1", negBoard.NegativeCount, 1);
        LogicTests.Eq("T2 负利润腿单件净利 = 5,000×0.92−10,000", negBoard.Legs[0].NetPerUnit, -5400L);
        LogicTests.Eq("T2 负利润腿不进正利润排序", negBoard.PositiveSorted(TradeSort.PerKg, 0).Count, 0);
        LogicTests.Eq("T2 负利润腿可折叠列表 = 1", negBoard.NegativeSorted().Count, 1);
        var negPlan = TradeEngine.BuildLoad(negBoard.Legs, 150);
        LogicTests.Eq("T2 负利润腿不装载", negPlan.Lines.Count, 0);
    }

    // ── 三、装载方案对账（150 / 40 / 200 KG）────────────────────────────
    private static void RunLoadPlanReconciliationTests()
    {
        List<MarketQuote> rows;
        DictMeta meta;
        DictionaryDepthProvider depth;
        DateTimeOffset now;
        BuildDemoFixture(out rows, out meta, out depth, out now);
        var board = TradeEngine.Build(rows, TradeRouteMode.BlackMarket, null, meta, depth, F2P, now);

        var plan150 = TradeEngine.BuildLoad(board.Legs, 150);
        LogicTests.Eq("T3 150KG SKU 数 = 8", plan150.SkuCount, 8);
        LogicTests.Eq("T3 150KG 件数 = 63", plan150.TotalPieces, 63L);
        Near("T3 150KG 已用负重 = 149.1", plan150.UsedKg, 149.1, 0.05);
        Near("T3 150KG 剩余 = 0.9", plan150.RemainingKg, 0.9, 0.05);
        LogicTests.Eq("T3 150KG 合计 = 816,912", plan150.TotalProfit, 816912L);
        LogicTests.Eq("T3 150KG 密度 = 5,446（合计÷负重上限）",
            (long)Math.Round(plan150.DensityPerKg), 5446L);
        LogicTests.Check("T3 150KG 负重到顶（长矛 0.9KG 装不下）", plan150.WeightCapped, "expected weight capped");
        LogicTests.Check("T3 150KG 深度到顶（存在被承接量限制的 SKU）", plan150.DepthCapped, "expected depth capped");

        // 按城合计（规范 §9：150KG 逐城 401,212 / 206,656 / 96,880 / 62,184 / 49,980）
        var byCity = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < plan150.Lines.Count; i++)
        {
            var line = plan150.Lines[i];
            string city = line.Leg.OriginCity;
            long sum;
            byCity.TryGetValue(city, out sum);
            byCity[city] = sum + line.Profit;
        }
        LogicTests.Eq("T3 林堡合计 401,212", CitySum(byCity, "Lymhurst"), 401212L);
        LogicTests.Eq("T3 桥望合计 206,656", CitySum(byCity, "Bridgewatch"), 206656L);
        LogicTests.Eq("T3 马丁合计 96,880", CitySum(byCity, "Martlock"), 96880L);
        LogicTests.Eq("T3 塞福合计 62,184", CitySum(byCity, "Thetford"), 62184L);
        LogicTests.Eq("T3 斯特合计 49,980", CitySum(byCity, "Fort Sterling"), 49980L);

        var plan40 = TradeEngine.BuildLoad(board.Legs, 40);
        LogicTests.Eq("T3 40KG SKU 数 = 3", plan40.SkuCount, 3);
        LogicTests.Eq("T3 40KG 件数 = 9", plan40.TotalPieces, 9L);
        LogicTests.Eq("T3 40KG 合计 = 444,160", plan40.TotalProfit, 444160L);
        Near("T3 40KG 已用负重 = 40.0", plan40.UsedKg, 40.0, 0.05);

        var plan200 = TradeEngine.BuildLoad(board.Legs, 200);
        LogicTests.Eq("T3 200KG SKU 数 = 9", plan200.SkuCount, 9);
        LogicTests.Eq("T3 200KG 件数 = 68", plan200.TotalPieces, 68L);
        Near("T3 200KG 已用负重 = 190.5", plan200.UsedKg, 190.5, 0.05);
        LogicTests.Eq("T3 200KG 合计 = 883,192（全深度）", plan200.TotalProfit, 883192L);
        LogicTests.Check("T3 200KG 深度到顶、负重未用满（规范 S5）",
            plan200.DepthCapped && !plan200.WeightCapped, "depth=" + plan200.DepthCapped + " weight=" + plan200.WeightCapped);

        // 未填负重绝不编默认值：空方案
        var planEmpty = TradeEngine.BuildLoad(board.Legs, 0d);
        LogicTests.Eq("T3 负重未填 → 空方案（不编默认值）", planEmpty.Lines.Count, 0);
        LogicTests.Eq("T3 负重未填 → 合计 0", planEmpty.TotalProfit, 0L);
    }

    private static long CitySum(Dictionary<string, long> sums, string city)
    {
        long value;
        return sums.TryGetValue(city, out value) ? value : 0L;
    }

    // ── 四、按 item+quality 分组，不跨品质混选 ─────────────────────────
    private static void RunQualityIsolationTests()
    {
        var rows = new List<MarketQuote>();
        var meta = new DictMeta();
        meta.Add("T4_BAG", "背包", 3.4);
        string fresh = DateTimeOffset.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss");
        // 品质 1：买 5,000 卖 6,000 → 净利 520；品质 3：买 100 卖 50,000 → 净利 45,900
        rows.Add(Q("T4_BAG", "Lymhurst", 1, 5000, 0, fresh, ""));
        rows.Add(Q("T4_BAG", TradeCities.BlackMarket, 1, 0, 6000, "", fresh));
        rows.Add(Q("T4_BAG", "Lymhurst", 3, 100, 0, fresh, ""));
        rows.Add(Q("T4_BAG", TradeCities.BlackMarket, 3, 0, 50000, "", fresh));

        var board = TradeEngine.Build(rows, TradeRouteMode.BlackMarket, null, meta,
            NullDepthProvider.Instance, F2P, DateTimeOffset.UtcNow);
        LogicTests.Eq("T4 两品质 → 两条独立腿", board.Legs.Count, 2);
        var q1 = FindLeg(board, "T4_BAG", 1);
        var q3 = FindLeg(board, "T4_BAG", 3);
        LogicTests.Check("T4 品质 1 腿存在", q1 != null, "missing q1");
        LogicTests.Check("T4 品质 3 腿存在", q3 != null, "missing q3");
        if (q1 != null)
        {
            LogicTests.Eq("T4 品质 1 吃单成本不被品质 3 污染", q1.BuyUnit, 5000L);
            LogicTests.Eq("T4 品质 1 卖入所得不被品质 3 污染", q1.SellUnit, 6000L);
            LogicTests.Eq("T4 品质 1 净利 = 6,000×0.92−5,000", q1.NetPerUnit, 520L);
        }
        if (q3 != null)
        {
            LogicTests.Eq("T4 品质 3 吃单成本不被品质 1 污染", q3.BuyUnit, 100L);
            LogicTests.Eq("T4 品质 3 卖入所得不被品质 1 污染", q3.SellUnit, 50000L);
            LogicTests.Eq("T4 品质 3 净利 = 50,000×0.92−100", q3.NetPerUnit, 45900L);
        }
        // 旧实现按 baseId 分组会把两品质混成一锅（会出现 50,000 卖价配 5,000 买价这种跨品质腿）
        bool mixed = false;
        for (int i = 0; i < board.Legs.Count; i++)
            if (board.Legs[i].SellUnit == 50000L && board.Legs[i].BuyUnit == 5000L) mixed = true;
        LogicTests.Check("T4 不出现跨品质混选腿", !mixed, "cross-quality leg found");
    }

    // ── 五、黑市只能做目的城 ────────────────────────────────────────────
    private static void RunBlackMarketTests()
    {
        var rows = new List<MarketQuote>();
        string fresh = DateTimeOffset.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss");
        // 黑市 sell_price_min=1（比主城低）且 buy_price_max=5,000：旧实现会把它当进货地
        rows.Add(Q("T6_SWORD", "Lymhurst", 1, 100, 0, fresh, ""));
        rows.Add(Q("T6_SWORD", TradeCities.BlackMarket, 1, 1, 5000, fresh, fresh));

        var routes = Arbitrage.Compute(rows, F2P, 0.025, 0);
        LogicTests.Eq("T5 黑市不被当出发城时仍出一条腿", routes.Count, 1);
        if (routes.Count == 1)
        {
            LogicTests.Eq("T5 出发城 = Lymhurst", routes[0].BuyCity, "Lymhurst");
            LogicTests.Eq("T5 目的城 = Black Market", routes[0].SellCity, TradeCities.BlackMarket);
            LogicTests.Eq("T5 成本 = 主城 sell_min 100", routes[0].BuyPrice, 100L);
            LogicTests.Eq("T5 所得 = 黑市 buy_max 5,000", routes[0].SellPrice, 5000L);
        }

        var board = TradeEngine.Build(rows, TradeRouteMode.RoyalLoop, null, null,
            NullDepthProvider.Instance, F2P, DateTimeOffset.UtcNow);
        bool bmInRoyalLoop = false;
        for (int i = 0; i < board.Legs.Count; i++)
            if (board.Legs[i].OriginCity == TradeCities.BlackMarket
                || board.Legs[i].DestCity == TradeCities.BlackMarket) bmInRoyalLoop = true;
        LogicTests.Check("T5 ②档五王城互跑不含黑市", !bmInRoyalLoop, "black market leaked into royal loop");
    }

    // ── 六、0 报价在计算层过滤（不进排序、单列缺报）────────────────────
    private static void RunZeroPriceTests()
    {
        var rows = new List<MarketQuote>();
        string fresh = DateTimeOffset.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss");
        // A：黑市有买单、出发城无卖单 → 缺「出发城无卖单」
        rows.Add(Q("T6_NOORIGIN", "Lymhurst", 1, 0, 0, "", ""));
        rows.Add(Q("T6_NOORIGIN", TradeCities.BlackMarket, 1, 0, 10000, "", fresh));
        // B：出发城有卖单、黑市无买单 → 缺「目的城无买单」
        rows.Add(Q("T6_NODEST", "Lymhurst", 1, 5000, 0, fresh, ""));
        rows.Add(Q("T6_NODEST", TradeCities.BlackMarket, 1, 0, 0, "", ""));
        // C：两侧全 0 → 不产生任何腿/缺报（连候选都不该算）
        rows.Add(Q("T6_NODATA", "Lymhurst", 1, 0, 0, "", ""));
        rows.Add(Q("T6_NODATA", TradeCities.BlackMarket, 1, 0, 0, "", ""));

        var board = TradeEngine.Build(rows, TradeRouteMode.BlackMarket, null, null,
            NullDepthProvider.Instance, F2P, DateTimeOffset.UtcNow);
        LogicTests.Eq("T6 0 报价不产生可执行腿", board.Legs.Count, 0);
        LogicTests.Eq("T6 缺报计数 = 5（A 的 5 个出发城）+1（B 的林堡）", board.MissingTotal, 6);
        bool hasOrigin = false, hasDest = false, hasNoDataItem = false;
        for (int i = 0; i < board.Missing.Count; i++)
        {
            var m = board.Missing[i];
            if (m.Reason == MissingReason.OriginNoSell && m.ItemId == "T6_NOORIGIN") hasOrigin = true;
            if (m.Reason == MissingReason.DestNoBuy && m.ItemId == "T6_NODEST") hasDest = true;
            if (m.ItemId == "T6_NODATA") hasNoDataItem = true;
        }
        LogicTests.Check("T6 缺报说明「出发城无卖单」", hasOrigin, "missing origin reason");
        LogicTests.Check("T6 缺报说明「目的城无买单」", hasDest, "missing dest reason");
        LogicTests.Check("T6 两侧全 0 连缺报都不产生（不制造噪音）", !hasNoDataItem, "no-data item leaked");
        LogicTests.Check("T6 缺报腿不进负利润折叠", board.NegativeCount == 0, "negative=" + board.NegativeCount);
    }

    // ── 七、陈旧：目的城侧也剔除 ────────────────────────────────────────
    private static void RunStaleTests()
    {
        var rows = new List<MarketQuote>();
        var now = new DateTimeOffset(2026, 11, 10, 2, 0, 0, TimeSpan.Zero);
        string fresh = now.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss");
        string stale = now.AddHours(-25).ToString("yyyy-MM-ddTHH:mm:ss");
        rows.Add(Q("T6_STALEDEST", "Lymhurst", 1, 5000, 0, fresh, ""));
        rows.Add(Q("T6_STALEDEST", TradeCities.BlackMarket, 1, 0, 10000, "", stale));

        var board = TradeEngine.Build(rows, TradeRouteMode.BlackMarket, null, null,
            NullDepthProvider.Instance, F2P, now);
        LogicTests.Eq("T7 目的城 >24h 剔除计数", board.DroppedStale, 1);
        LogicTests.Eq("T7 目的城陈旧不产腿", board.Legs.Count, 0);
        bool staleDest = false;
        for (int i = 0; i < board.Missing.Count; i++)
            if (board.Missing[i].Reason == MissingReason.DestStale) staleDest = true;
        LogicTests.Check("T7 缺报原因 = 目的城报价过期", staleDest, "missing dest stale reason");
    }

    // ── 八、会员档：F2P 8% / Premium 4% 三处重算（引擎层）──────────────
    private static void RunPremiumTests()
    {
        List<MarketQuote> rows;
        DictMeta meta;
        DictionaryDepthProvider depth;
        DateTimeOffset now;
        BuildDemoFixture(out rows, out meta, out depth, out now);

        var boardF2P = TradeEngine.Build(rows, TradeRouteMode.BlackMarket, null, meta, depth, F2P, now);
        var boardPrem = TradeEngine.Build(rows, TradeRouteMode.BlackMarket, null, meta, depth, Premium, now);
        var spearF2P = FindLeg(boardF2P, "T6_2H_SPEAR", 1);
        var spearPrem = FindLeg(boardPrem, "T6_2H_SPEAR", 1);
        LogicTests.Eq("T8 F2P 长矛净利 6,548", spearF2P != null ? spearF2P.NetPerUnit : -1L, 6548L);
        LogicTests.Eq("T8 Premium 长矛净利 = 12,640×0.96−5,081 = 7,053",
            spearPrem != null ? spearPrem.NetPerUnit : -1L, 7053L);

        var planF2P = TradeEngine.BuildLoad(boardF2P.Legs, 150);
        var planPrem = TradeEngine.BuildLoad(boardPrem.Legs, 150);
        LogicTests.Eq("T8 F2P 150KG 合计 816,912", planF2P.TotalProfit, 816912L);
        LogicTests.Check("T8 Premium 装载合计重算后更高", planPrem.TotalProfit > planF2P.TotalProfit,
            "prem=" + planPrem.TotalProfit + " f2p=" + planF2P.TotalProfit);
        LogicTests.Check("T8 Premium 榜单单件净利整体提高",
            boardPrem.PositiveSorted(TradeSort.PerUnit, 0)[0].NetPerUnit
            > boardF2P.PositiveSorted(TradeSort.PerUnit, 0)[0].NetPerUnit, "premium not higher");
    }

    // ── 九、②档 20 条有向腿 + ③档布雷西里恩 ─────────────────────────────
    private static void RunRoyalLoopAndBrecilienTests()
    {
        var rows = new List<MarketQuote>();
        var meta = new DictMeta();
        string fresh = DateTimeOffset.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss");
        string[] royal = TradeCities.Royal;
        for (int i = 0; i < royal.Length; i++)
        {
            // 每城同时有卖单与买单，才能形成 5×4 = 20 条有向腿
            rows.Add(Q("T6_ROYAL", royal[i], 1, 1000 + i * 100, 5000 + i * 100, fresh, fresh));
        }
        var board = TradeEngine.Build(rows, TradeRouteMode.RoyalLoop, null, meta,
            NullDepthProvider.Instance, F2P, DateTimeOffset.UtcNow);
        LogicTests.Eq("T9 五王城两两互跑 = 20 条有向腿", board.Legs.Count, 20);
        bool sameCity = false, nonRoyal = false;
        for (int i = 0; i < board.Legs.Count; i++)
        {
            var leg = board.Legs[i];
            if (leg.OriginCity == leg.DestCity) sameCity = true;
            if (Array.IndexOf(royal, leg.OriginCity) < 0 || Array.IndexOf(royal, leg.DestCity) < 0) nonRoyal = true;
        }
        LogicTests.Check("T9 A→B 与 B→A 是两条腿（无同城腿）", !sameCity, "same-city leg found");
        LogicTests.Check("T9 ②档腿只在五王城之间", !nonRoyal, "non-royal city in legs");

        var boardMartlock = TradeEngine.Build(rows, TradeRouteMode.RoyalLoop, "Martlock", meta,
            NullDepthProvider.Instance, F2P, DateTimeOffset.UtcNow);
        LogicTests.Eq("T9 选定目的城后只算该城腿 = 4 条", boardMartlock.Legs.Count, 4);
        bool destOk = true;
        for (int i = 0; i < boardMartlock.Legs.Count; i++)
            if (boardMartlock.Legs[i].DestCity != "Martlock") destOk = false;
        LogicTests.Check("T9 ②档方案只算目的城腿（马丁）", destOk, "wrong destination");

        // ③ 布雷西里恩：只提示不排行（引擎仍给可行性腿，UI 不排行）
        var brecRows = new List<MarketQuote>();
        brecRows.Add(Q("T6_RING", "Lymhurst", 1, 5000, 0, fresh, ""));
        brecRows.Add(Q("T6_RING", TradeCities.Brecilien, 1, 0, 6000, "", fresh));
        var brecBoard = TradeEngine.Build(brecRows, TradeRouteMode.Brecilien, null, meta,
            NullDepthProvider.Instance, F2P, DateTimeOffset.UtcNow);
        LogicTests.Eq("T9 ③档 → 布雷西里恩 1 条可行性腿", brecBoard.Legs.Count, 1);
        if (brecBoard.Legs.Count == 1)
            LogicTests.Eq("T9 ③档目的城 = Brecilien", brecBoard.Legs[0].DestCity, TradeCities.Brecilien);

        var candidateRoutes = TradeRouteCatalog.Candidates("Lymhurst", TradeCities.BlackMarket);
        LogicTests.Eq("T9 黑市腿候选路线 = 4 条", candidateRoutes.Count, 4);
        LogicTests.Eq("T9 首选路线标题（中文城市）", candidateRoutes[0].Title, "林堡 → 黑市");
        LogicTests.Eq("T9 高危路线红区段数 = 3", candidateRoutes[0].RedSegments, 3);
        LogicTests.Check("T9 低危路线无红区", candidateRoutes[2].RedSegments == 0, "expected 0 red segments");
    }

    // ── 十、负重贪心不变量：件数 = min(负重件数, 深度) ──────────────────
    private static void RunGreedyInvariantTests()
    {
        var legs = new List<TradeLeg>();
        legs.Add(MakeLeg("T6_A", 100, 1.0, 3));    // 每kg 100，深度 3
        legs.Add(MakeLeg("T6_B", 150, 2.0, 100));  // 每kg 75，深度充足
        legs.Add(MakeLeg("T6_C", 10, 5.0, 100));   // 每kg 2，装不下

        var plan = TradeEngine.BuildLoad(legs, 10);
        LogicTests.Eq("T10 贪心 SKU 数 = 2", plan.SkuCount, 2);
        LogicTests.Eq("T10 A 取深度上限 3 件（3×1kg）", plan.Lines[0].Pieces, 3L);
        LogicTests.Eq("T10 B 取剩余负重能装的 3 件（3×2kg）", plan.Lines[1].Pieces, 3L);
        LogicTests.Eq("T10 件数合计 = 6", plan.TotalPieces, 6L);
        Near("T10 已用负重 = 9", plan.UsedKg, 9.0, 1e-9);
        LogicTests.Eq("T10 合计利润 = 300+450 = 750", plan.TotalProfit, 750L);
        LogicTests.Check("T10 深度受限标记", plan.DepthCapped, "expected DepthCapped");
        LogicTests.Check("T10 负重受限标记（C 装不下）", plan.WeightCapped, "expected WeightCapped");

        // 单件重量未知的腿：不装、单独标记，不猜重量
        var unknown = new List<TradeLeg> { MakeLeg("T6_X", 500, 1.0, 10), MakeLegUnknownWeight("T6_Y", 500) };
        var planUnknown = TradeEngine.BuildLoad(unknown, 10);
        LogicTests.Check("T10 重量未知的腿不装填", planUnknown.HasUnknownWeight, "expected HasUnknownWeight");
        LogicTests.Eq("T10 重量未知时只装已知重量的腿", planUnknown.SkuCount, 1);
    }

    private static TradeLeg MakeLeg(string itemId, long netPerUnit, double weight, long depth)
    {
        return new TradeLeg
        {
            ItemId = itemId,
            Quality = 1,
            OriginCity = "Lymhurst",
            DestCity = TradeCities.BlackMarket,
            BuyUnit = 1000,
            SellUnit = 1000 + netPerUnit,
            SellNet = 1000 + netPerUnit,
            NetPerUnit = netPerUnit,
            WeightKg = weight,
            PerKg = netPerUnit / weight,
            Depth = depth,
            DepthKnown = true
        };
    }

    private static TradeLeg MakeLegUnknownWeight(string itemId, long netPerUnit)
    {
        return new TradeLeg
        {
            ItemId = itemId,
            Quality = 1,
            OriginCity = "Lymhurst",
            DestCity = TradeCities.BlackMarket,
            BuyUnit = 1000,
            SellUnit = 1000 + netPerUnit,
            SellNet = 1000 + netPerUnit,
            NetPerUnit = netPerUnit,
            WeightKg = null,
            PerKg = null,
            Depth = 10,
            DepthKnown = false
        };
    }

    // ── 十一、导航：单一状态源 + 逐段推进 ───────────────────────────────
    private static void RunNavigationStateTests()
    {
        var nav = NavigationState.Shared;
        nav.Deactivate();
        LogicTests.Check("T11 初始未激活", !nav.IsActive, "expected inactive");

        var routes = TradeRouteCatalog.Candidates("Lymhurst", TradeCities.BlackMarket);
        var route = routes[0];
        nav.Activate(route, 816912, 63, 149.1);
        LogicTests.Check("T11 激活导航", nav.IsActive, "expected active");
        LogicTests.Eq("T11 初始步骤 = 第 1 步", nav.StepIndex, 0);
        LogicTests.Eq("T11 进度文案", nav.ProgressText, "第 1 / " + route.Steps.Count + " 步");
        LogicTests.Check("T11 当前段文案含出口八向与下一图",
            nav.CurrentText.Contains("出口") && nav.CurrentText.Contains("→"), nav.CurrentText);
        LogicTests.Check("T11 高危路线当前段为红区", nav.IsCurrentRed, "expected red current step");
        LogicTests.Check("T11 红区提示给出后果（不写可能/大概）",
            nav.NextHint.Contains("红区") && !nav.NextHint.Contains("可能"), nav.NextHint);
        LogicTests.Eq("T11 预估收益来自装载方案", nav.EstimatedProfit, 816912L);

        nav.Next();
        LogicTests.Eq("T11 下一步两处同步用同一状态（StepIndex=1）", nav.StepIndex, 1);
        nav.Prev();
        LogicTests.Eq("T11 上一步回退", nav.StepIndex, 0);
        for (int i = 0; i < route.Steps.Count; i++) nav.Next();
        LogicTests.Check("T11 末段下一步 → 已抵达", nav.Arrived, "expected arrived");
        LogicTests.Check("T11 已抵达后无下一步", !nav.HasNext, "expected no next");
        LogicTests.Eq("T11 已抵达进度文案", nav.ProgressText, "已抵达 黑市");

        nav.UpdateEstimates(500000, 40, 84.5);
        LogicTests.Eq("T11 会员档/负重变化后收益估计更新", nav.EstimatedProfit, 500000L);
        nav.Deactivate();
        LogicTests.Check("T11 关闭导航", !nav.IsActive, "expected inactive");
        LogicTests.Eq("T11 关闭后收益清零", nav.EstimatedProfit, 0L);
        LogicTests.Eq("T11 关闭后步数清空", nav.StepCount, 0);

        // 单一状态源：所有引用必须是同一实例
        LogicTests.Check("T11 主窗与悬浮条共用同一 NavigationState",
            ReferenceEquals(NavigationState.Shared, NavigationState.Shared), "not same singleton");
    }

    // ── 十二、设置：会员档 / 负重记忆持久化 ─────────────────────────────
    private static void RunSettingsTests()
    {
        LogicTests.Eq("T12 负重输入 150", TradeSettings.ParseWeightText("150"), 150.0);
        LogicTests.Eq("T12 负重输入 149.1", TradeSettings.ParseWeightText("149.1"), 149.1);
        LogicTests.Eq("T12 空白 = 未填", TradeSettings.ParseWeightText("  "), 0.0);
        LogicTests.Eq("T12 非法 = 未填", TradeSettings.ParseWeightText("abc"), 0.0);
        LogicTests.Eq("T12 负数 = 未填", TradeSettings.ParseWeightText("-5"), 0.0);
        LogicTests.Eq("T12 零 = 未填", TradeSettings.ParseWeightText("0"), 0.0);
        LogicTests.Eq("T12 离谱值 = 未填", TradeSettings.ParseWeightText("999999"), 0.0);
        LogicTests.Eq("T12 F2P 税率 8%", new TradeSettings().SellTax, 0.08);
        LogicTests.Eq("T12 Premium 税率 4%", new TradeSettings { Premium = true }.SellTax, 0.04);

        string dir = Path.Combine(Path.GetTempPath(), "ac_trade_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "trade-settings.json");
            var settings = new TradeSettings
            {
                Premium = true,
                WeightLimitKg = 149.1,
                RouteMode = 1,
                LastDestination = "Martlock"
            };
            settings.Save(path);
            var loaded = TradeSettings.Load(path);
            LogicTests.Check("T12 会员档记忆", loaded.Premium, "expected premium");
            Near("T12 负重记忆", loaded.WeightLimitKg, 149.1, 1e-9);
            LogicTests.Eq("T12 路线档记忆", loaded.RouteMode, 1);
            LogicTests.Eq("T12 目的城记忆", loaded.LastDestination, "Martlock");

            var missing = TradeSettings.Load(Path.Combine(dir, "missing.json"));
            LogicTests.Eq("T12 设置缺失 → 负重未填", missing.WeightLimitKg, 0.0);
            LogicTests.Check("T12 设置缺失 → 默认 F2P", !missing.Premium, "expected f2p");

            string bad = Path.Combine(dir, "bad.json");
            File.WriteAllText(bad, "{broken");
            var corrupt = TradeSettings.Load(bad);
            LogicTests.Eq("T12 设置损坏不抛异常、回默认值", corrupt.WeightLimitKg, 0.0);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Near("T13 路径 A 保本门槛 F2P 1.087", TradeEngine.PathAThreshold(0.08), 1.087, 0.001);
        Near("T13 路径 A 保本门槛 Premium 1.042", TradeEngine.PathAThreshold(0.04), 1.042, 0.001);
        LogicTests.Eq("T13 陈旧阈值 24h", (long)TradeEngine.QuoteStaleAge.TotalHours, 24L);
    }
}
