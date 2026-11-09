#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AlbionCompanion.Core.Trade
{
    /// <summary>跑商路线档（规范 §2 三档）。</summary>
    public enum TradeRouteMode
    {
        /// <summary>① 五个主城 → 凯尔莱恩黑市（默认）。</summary>
        BlackMarket = 0,

        /// <summary>② 五王城两两互跑（有向 20 条腿，先选目的城）。</summary>
        RoyalLoop = 1,

        /// <summary>③ 主城 ↔ 布雷西里恩：只做可行性提示，不排行。</summary>
        Brecilien = 2
    }

    /// <summary>差价榜排序口径（规范 §3：每 kg 净利默认）。</summary>
    public enum TradeSort
    {
        PerKg = 0,
        PerUnit = 1,
        PerTrip = 2
    }

    /// <summary>城市清单与中文名。黑市是 NPC 市场（只挂买单、只收装备），只能做目的城。</summary>
    public static class TradeCities
    {
        public const string BlackMarket = "Black Market";
        public const string Caerleon = "Caerleon";
        public const string Martlock = "Martlock";
        public const string Thetford = "Thetford";
        public const string FortSterling = "Fort Sterling";
        public const string Bridgewatch = "Bridgewatch";
        public const string Lymhurst = "Lymhurst";
        public const string Brecilien = "Brecilien";

        /// <summary>五个王城（②档；凯尔莱恩与黑市不在其中）。顺序固定保证腿序稳定。</summary>
        public static readonly string[] Royal =
        {
            Lymhurst, Bridgewatch, FortSterling, Martlock, Thetford
        };

        public static bool IsBlackMarket(string city)
        {
            if (string.IsNullOrEmpty(city)) return false;
            return string.Equals(city.Trim(), BlackMarket, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(city.Trim(), "BlackMarket", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>城市中文短名（跑商页统一口径，和市场页各自独立）。</summary>
        public static string Zh(string city)
        {
            if (string.IsNullOrEmpty(city)) return "";
            switch (city.Trim())
            {
                case "Caerleon": return "凯城";
                case "Bridgewatch": return "桥望";
                case "Martlock": return "马丁";
                case "Thetford": return "塞福";
                case "Fort Sterling": return "斯特";
                case "Lymhurst": return "林堡";
                case "Black Market": return "黑市";
                case "Brecilien": return "布雷西";
                default: return city;
            }
        }

        /// <summary>AODP 基址 → 服务器短标识（只读全局，不新增多服 UI）。</summary>
        public static string ServerLabel()
        {
            string url = MarketClient.ServerBaseUrl ?? "";
            if (url.IndexOf("east", StringComparison.OrdinalIgnoreCase) >= 0) return "east · 亚服";
            if (url.IndexOf("europe", StringComparison.OrdinalIgnoreCase) >= 0) return "europe · 欧服";
            if (url.IndexOf("west", StringComparison.OrdinalIgnoreCase) >= 0) return "west · 美服";
            return "east · 亚服";
        }
    }

    /// <summary>物品元数据读取口（生产实现是 ItemIndex；测试可注入内存字典）。</summary>
    public interface ITradeItemMeta
    {
        /// <summary>本地化显示名；无元数据返回 null（调用方回退到 itemId）。</summary>
        string DisplayNameOf(string itemId);

        /// <summary>重量（kg）；未知返回 null——**绝不当 0**。</summary>
        double? WeightKgOf(string itemId);
    }

    /// <summary>
    /// ItemIndex 适配器（ItemIndex 为 null = 元数据缺失，全部返回 null，由 UI 标「重量未知」）。
    /// 命名避开 M3 既有的 <c>Core.ItemIndexMeta</c>（物品索引侧车），防止类型歧义。
    /// </summary>
    public sealed class TradeItemMeta : ITradeItemMeta
    {
        private readonly ItemIndex _index;

        public TradeItemMeta(ItemIndex index) { _index = index; }

        public string DisplayNameOf(string itemId)
        {
            if (_index == null || string.IsNullOrEmpty(itemId)) return null;
            return _index.DisplayNameOf(itemId);
        }

        public double? WeightKgOf(string itemId)
        {
            if (_index == null || string.IsNullOrEmpty(itemId)) return null;
            return _index.WeightKgOf(itemId);
        }
    }

    /// <summary>
    /// 挂单承接量（深度）读取口。规范 §7 的深度来源（orderbook / history）尚未接入且本模块禁止新增 HTTP，
    /// 所以**默认实现返回 0 = 未知**，UI 诚实显示「深度未知」，装载只受负重限制。
    /// 测试可注入假深度验证 min(负重件数, 深度) 的贪心语义；未来接 orderbook 只需替换实现。
    /// </summary>
    public interface IDepthProvider
    {
        /// <summary>返回该物品在某城的承接量；0 = 未知/无数据（绝不当成可无限装）。</summary>
        long DepthOf(string itemId, int quality, string city);
    }

    /// <summary>默认深度：未知（0）。</summary>
    public sealed class NullDepthProvider : IDepthProvider
    {
        public static readonly NullDepthProvider Instance = new NullDepthProvider();
        public long DepthOf(string itemId, int quality, string city) { return 0L; }
    }

    /// <summary>内存深度表（测试用；生产不默认启用，避免拿假数据当真实承接量）。</summary>
    public sealed class DictionaryDepthProvider : IDepthProvider
    {
        private readonly Dictionary<string, long> _depth = new Dictionary<string, long>(StringComparer.Ordinal);

        public void Set(string itemId, int quality, string city, long depth)
        {
            _depth[Key(itemId, quality, city)] = depth;
        }

        public long DepthOf(string itemId, int quality, string city)
        {
            long value;
            return _depth.TryGetValue(Key(itemId, quality, city), out value) ? value : 0L;
        }

        private static string Key(string itemId, int quality, string city)
        {
            return (itemId ?? "") + "|" + quality + "|" + (city ?? "");
        }
    }

    /// <summary>一条可执行腿：出发城按 sell_price_min 吃单买入，目的城按 buy_price_max 吃单卖出。</summary>
    public class TradeLeg
    {
        public string ItemId;
        public int Quality;

        /// <summary>中文显示名（无元数据时为 null，UI 回退 itemId）。</summary>
        public string DisplayName;

        public string OriginCity;
        public string DestCity;

        /// <summary>吃单成本 = 出发城 sell_price_min（0 报价已在计算层过滤，绝不会是 0）。</summary>
        public long BuyUnit;

        /// <summary>卖入所得原值 = 目的城 buy_price_max（>0，0 报价已在计算层过滤）。</summary>
        public long SellUnit;

        /// <summary>税后卖入所得 = round(buy_price_max × (1 − 销售税))；黑市是 NPC 买卖，无挂单费。</summary>
        public long SellNet;

        /// <summary>单件净利 = SellNet − BuyUnit（可为负；负腿由 UI 折叠并计数）。</summary>
        public long NetPerUnit;

        /// <summary>回报率（%，对吃单成本）。</summary>
        public double ReturnPct;

        /// <summary>物品重量 kg；未知为 null（绝不显示 0）。</summary>
        public double? WeightKg;

        /// <summary>每 kg 净利；重量未知时为 null，排序排到最后并标「重量未知」。</summary>
        public double? PerKg;

        /// <summary>挂单深度（承接量）；0 且 DepthKnown=false 表示未知（当前默认，见 IDepthProvider）。</summary>
        public long Depth;
        public bool DepthKnown;

        /// <summary>出发城报价时间戳（原始 AODP 字符串，UTC）。</summary>
        public string OriginDate;

        /// <summary>目的城报价时间戳（原始 AODP 字符串，UTC）。</summary>
        public string DestDate;

        public bool IsNegative { get { return NetPerUnit < 0; } }

        /// <summary>价格缺失侧（用于「缺上报数据」说明）；正常腿为空串。</summary>
        public string MissingNote = "";
    }

    /// <summary>缺上报数据的腿（0 报价/陈旧报价），不参与排序、不进负利润折叠。</summary>
    public class MissingLeg
    {
        public string ItemId;
        public int Quality;
        public string OriginCity;
        public string DestCity;
        public MissingReason Reason;

        public string ReasonText
        {
            get
            {
                switch (Reason)
                {
                    case MissingReason.OriginNoSell: return "出发城无卖单";
                    case MissingReason.DestNoBuy: return "目的城无买单";
                    case MissingReason.OriginStale: return "出发城报价过期(>24h)";
                    case MissingReason.DestStale: return "目的城报价过期(>24h)";
                    default: return "无上报数据";
                }
            }
        }
    }

    public enum MissingReason
    {
        OriginNoSell = 0,
        DestNoBuy = 1,
        OriginStale = 2,
        DestStale = 3
    }

    /// <summary>一次计算的完整产物：可执行腿 + 缺报腿 + 统计。</summary>
    public class TradeBoard
    {
        public TradeRouteMode Mode;
        public List<TradeLeg> Legs = new List<TradeLeg>();
        public List<MissingLeg> Missing = new List<MissingLeg>();

        /// <summary>缺报腿总数（Missing 可能被截断，用于「N 条缺上报数据」计数）。</summary>
        public int MissingTotal;

        /// <summary>税后为负的腿数（默认折叠但计数可见）。</summary>
        public int NegativeCount;

        /// <summary>被 24h 陈旧剔除的价格点条数。</summary>
        public int DroppedStale;

        /// <summary>枚举过的候选腿数（含缺报），用于诊断。</summary>
        public int CandidateCount;

        /// <summary>参与计算的报价里最旧一条的年龄（分钟）；无有效报价为 -1。</summary>
        public int OldestQuoteAgeMinutes = -1;

        public int ProfitableCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < Legs.Count; i++) if (Legs[i].NetPerUnit > 0) count++;
                return count;
            }
        }

        /// <summary>正利润腿按排序口径降序（每 kg 未知排最后，再按单件净利）。</summary>
        public List<TradeLeg> PositiveSorted(TradeSort sort, double weightLimitKg)
        {
            var list = new List<TradeLeg>();
            for (int i = 0; i < Legs.Count; i++)
                if (Legs[i].NetPerUnit > 0) list.Add(Legs[i]);
            TradeEngine.SortInPlace(list, sort, weightLimitKg);
            return list;
        }

        /// <summary>负利润腿（折叠区）按净利从大到小（亏损从少到多）。</summary>
        public List<TradeLeg> NegativeSorted()
        {
            var list = new List<TradeLeg>();
            for (int i = 0; i < Legs.Count; i++)
                if (Legs[i].NetPerUnit < 0) list.Add(Legs[i]);
            list.Sort(delegate (TradeLeg a, TradeLeg b)
            {
                int c = b.NetPerUnit.CompareTo(a.NetPerUnit);
                return c != 0 ? c : string.Compare(a.ItemId, b.ItemId, StringComparison.Ordinal);
            });
            return list;
        }
    }

    /// <summary>装载方案里的一行（一个 SKU）。</summary>
    public class LoadLine
    {
        public TradeLeg Leg;
        public long Pieces;
        public double WeightKg;
        public long Profit;
    }

    /// <summary>负重贪心装载结果（规范 §4）。</summary>
    public class LoadPlan
    {
        public List<LoadLine> Lines = new List<LoadLine>();
        public double WeightLimitKg;
        public double UsedKg;
        public long TotalProfit;
        public long TotalPieces;

        /// <summary>负重到顶（还有盈利腿但剩余负重装不下任何一件）。</summary>
        public bool WeightCapped;

        /// <summary>深度到顶（至少一行被挂单承接量卡住）。</summary>
        public bool DepthCapped;

        /// <summary>有盈利腿因重量未知无法装填（不猜重量，单独提示）。</summary>
        public bool HasUnknownWeight;

        /// <summary>该目的城盈利腿已全部装入（未受任何限制）。</summary>
        public bool AllLoaded;

        public double RemainingKg { get { return Math.Max(0d, WeightLimitKg - UsedKg); } }

        /// <summary>
        /// 密度（银/kg，规范 §4 汇总条 + §9 对账口径）：合计 ÷ **负重上限**。
        /// 对账锚点：816,912 ÷ 150 = 5,446（与规范 §9 一致；按已用 149.1 会得 5,479，口径不符）。
        /// </summary>
        public double DensityPerKg { get { return WeightLimitKg > 0d ? TotalProfit / WeightLimitKg : 0d; } }

        /// <summary>按实际已用重量算的密度（诊断用，UI 不展示，避免与 §9 口径混淆）。</summary>
        public double DensityOnLoadedKg { get { return UsedKg > 0d ? TotalProfit / UsedKg : 0d; } }


        public int SkuCount { get { return Lines.Count; } }
    }

    /// <summary>
    /// 跑商计算核心（纯逻辑、可离线单测）：
    /// ① 出发城按 sell_price_min 吃单买入（花钱），目的城按 buy_price_max 吃单卖出（收钱）——
    ///    这是对既有「把买价最高的城当进货地」接反错误的修正（规范 §10）；
    /// ② 按 (itemId, quality) 分组，绝不跨品质混选；
    /// ③ 黑市只做目的城，出发城永不出现黑市；
    /// ④ 0 报价在计算层过滤（不产生腿、不进排序），单列「缺上报数据」；
    /// ⑤ 价格时间戳 > 24h 的报价剔除并计数（保留 P0-3 口径）。
    /// 税口径按规范 §7：单件净利 = buy_price_max×(1−销售税) − sell_price_min。
    /// 双方都是「吃现成挂单」，不产生挂单创建费，故没有 2.5% 项（黑市为 NPC，同样无费）。
    /// </summary>
    public static class TradeEngine
    {
        /// <summary>24h 陈旧阈值（与市场/套利同一口径）。</summary>
        public static TimeSpan QuoteStaleAge { get { return MarketClient.SellStaleAge; } }

        /// <summary>缺报列表内存上限（计数仍如实统计 MissingTotal，避免大库把内存打爆）。</summary>
        public const int MaxMissingRecords = 2000;

        /// <summary>
        /// 构建差价榜。selectedDest 只对 ②档生效：null/空 = 枚举五王城全部 20 条有向腿；
        /// 指定目的城 = 只算该城腿（规范 §4「方案只算该城腿」）。
        /// </summary>
        public static TradeBoard Build(IEnumerable<MarketQuote> rows, TradeRouteMode mode, string selectedDest,
            ITradeItemMeta meta, IDepthProvider depth, double sellTax, DateTimeOffset now)
        {
            var board = new TradeBoard { Mode = mode };
            if (rows == null) return board;
            if (sellTax < 0d) sellTax = 0d;
            if (sellTax > 0.9d) sellTax = 0.9d;

            string[] origins = TradeCities.Royal;
            string[] dests;
            if (mode == TradeRouteMode.BlackMarket)
            {
                dests = new[] { TradeCities.BlackMarket };
            }
            else if (mode == TradeRouteMode.RoyalLoop)
            {
                if (!string.IsNullOrEmpty(selectedDest) && Array.IndexOf(TradeCities.Royal, selectedDest) >= 0)
                    dests = new[] { selectedDest };
                else
                    dests = TradeCities.Royal;
            }
            else
            {
                dests = new[] { TradeCities.Brecilien };
            }

            var groups = new Dictionary<string, List<MarketQuote>>(StringComparer.Ordinal);
            foreach (var q in rows)
            {
                if (q == null || string.IsNullOrEmpty(q.ItemId)) continue;
                string key = q.ItemId + "|" + q.Quality.ToString(CultureInfo.InvariantCulture);
                List<MarketQuote> list;
                if (!groups.TryGetValue(key, out list))
                {
                    list = new List<MarketQuote>();
                    groups[key] = list;
                }
                list.Add(q);
            }

            int oldestAge = -1;
            foreach (var kv in groups)
            {
                var quotes = kv.Value;
                string itemId = quotes[0].ItemId;
                int quality = quotes[0].Quality;

                var byCity = new Dictionary<string, MarketQuote>(StringComparer.OrdinalIgnoreCase);
                foreach (var q in quotes) byCity[q.City ?? ""] = q;

                // 陈旧判定按「价格点」计一次（同一目的城被 5 个出发城枚举时不能重复计数）；
                // 24h 剔除保留 P0-3：出发城卖价、目的城买价两侧都判。
                var staleSell = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var staleBuy = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < quotes.Count; i++)
                {
                    var q = quotes[i];
                    string city = q.City ?? "";
                    bool relevantOrigin = Array.IndexOf(origins, city) >= 0 && !TradeCities.IsBlackMarket(city);
                    bool relevantDest = Array.IndexOf(dests, city) >= 0;
                    if (relevantOrigin && q.SellMin > 0L)
                    {
                        bool stale = IsStale(q.SellMinDate, now);
                        staleSell[city] = stale;
                        if (stale) board.DroppedStale++;
                        else oldestAge = MinAge(oldestAge, AgeMinutes(q.SellMinDate, now));
                    }
                    if (relevantDest && q.BuyMax > 0L)
                    {
                        bool stale = IsStale(q.BuyMaxDate, now);
                        staleBuy[city] = stale;
                        if (stale) board.DroppedStale++;
                        else oldestAge = MinAge(oldestAge, AgeMinutes(q.BuyMaxDate, now));
                    }
                }

                double? weight = meta != null ? meta.WeightKgOf(itemId) : null;
                string displayName = meta != null ? meta.DisplayNameOf(itemId) : null;

                for (int oi = 0; oi < origins.Length; oi++)
                {
                    string origin = origins[oi];
                    if (TradeCities.IsBlackMarket(origin)) continue; // 黑市永不做出发城（防御性，origins 本就不含）
                    MarketQuote originQuote;
                    byCity.TryGetValue(origin, out originQuote);
                    long buyUnit = originQuote != null ? originQuote.SellMin : 0L;
                    bool originStale = false;
                    if (buyUnit > 0L)
                    {
                        bool stale;
                        if (staleSell.TryGetValue(origin, out stale) && stale) { originStale = true; buyUnit = 0L; }
                    }

                    for (int di = 0; di < dests.Length; di++)
                    {
                        string dest = dests[di];
                        if (string.Equals(origin, dest, StringComparison.OrdinalIgnoreCase)) continue;
                        MarketQuote destQuote;
                        byCity.TryGetValue(dest, out destQuote);
                        long sellUnit = destQuote != null ? destQuote.BuyMax : 0L;
                        bool destStale = false;
                        if (sellUnit > 0L)
                        {
                            bool stale;
                            if (staleBuy.TryGetValue(dest, out stale) && stale) { destStale = true; sellUnit = 0L; }
                        }

                        if (buyUnit <= 0L && sellUnit <= 0L) continue; // 两侧全无数据：不产生噪音缺报

                        board.CandidateCount++;
                        if (buyUnit <= 0L || sellUnit <= 0L)
                        {
                            var reason = buyUnit <= 0L
                                ? (originStale ? MissingReason.OriginStale : MissingReason.OriginNoSell)
                                : (destStale ? MissingReason.DestStale : MissingReason.DestNoBuy);
                            board.MissingTotal++;
                            if (board.Missing.Count < MaxMissingRecords)
                            {
                                board.Missing.Add(new MissingLeg
                                {
                                    ItemId = itemId,
                                    Quality = quality,
                                    OriginCity = origin,
                                    DestCity = dest,
                                    Reason = reason
                                });
                            }
                            continue;
                        }

                        long sellNet = (long)Math.Round(sellUnit * (1.0d - sellTax), MidpointRounding.AwayFromZero);
                        long net = sellNet - buyUnit;
                        long depthValue = 0L;
                        if (depth != null)
                        {
                            depthValue = depth.DepthOf(itemId, quality, dest);
                            if (depthValue < 0L) depthValue = 0L;
                        }
                        var leg = new TradeLeg
                        {
                            ItemId = itemId,
                            Quality = quality,
                            DisplayName = displayName,
                            OriginCity = origin,
                            DestCity = dest,
                            BuyUnit = buyUnit,
                            SellUnit = sellUnit,
                            SellNet = sellNet,
                            NetPerUnit = net,
                            ReturnPct = buyUnit > 0L ? net * 100.0d / buyUnit : 0d,
                            WeightKg = weight,
                            PerKg = weight.HasValue && weight.Value > 0d ? net / weight.Value : (double?)null,
                            Depth = depthValue,
                            DepthKnown = depthValue > 0L,
                            OriginDate = originQuote != null ? originQuote.SellMinDate : "",
                            DestDate = destQuote != null ? destQuote.BuyMaxDate : ""
                        };
                        if (net < 0L) board.NegativeCount++;
                        board.Legs.Add(leg);
                    }
                }
            }

            board.OldestQuoteAgeMinutes = oldestAge;
            return board;
        }

        /// <summary>
        /// ②档默认目的城 = 榜首腿的目的城（规范 §4）。轻量扫描：每组取出发城最低 sell_price_min、
        /// 目的城最高 buy_price_max，算净利找全局最优腿；无有效组合返回 null。
        /// </summary>
        public static string FindBestDestination(IEnumerable<MarketQuote> rows, double sellTax, DateTimeOffset now)
        {
            if (rows == null) return null;
            if (sellTax < 0d) sellTax = 0d;
            if (sellTax > 0.9d) sellTax = 0.9d;

            var groups = new Dictionary<string, List<MarketQuote>>(StringComparer.Ordinal);
            foreach (var q in rows)
            {
                if (q == null || string.IsNullOrEmpty(q.ItemId)) continue;
                string key = q.ItemId + "|" + q.Quality.ToString(CultureInfo.InvariantCulture);
                List<MarketQuote> list;
                if (!groups.TryGetValue(key, out list)) { list = new List<MarketQuote>(); groups[key] = list; }
                list.Add(q);
            }

            long bestNet = long.MinValue;
            string bestDest = null;
            foreach (var kv in groups)
            {
                var byCity = new Dictionary<string, MarketQuote>(StringComparer.OrdinalIgnoreCase);
                foreach (var q in kv.Value) byCity[q.City ?? ""] = q;

                MarketQuote originBest = null;
                foreach (string city in TradeCities.Royal)
                {
                    MarketQuote q;
                    if (!byCity.TryGetValue(city, out q) || q.SellMin <= 0L) continue;
                    if (IsStale(q.SellMinDate, now)) continue;
                    if (originBest == null || q.SellMin < originBest.SellMin) originBest = q;
                }
                if (originBest == null) continue;

                foreach (string city in TradeCities.Royal)
                {
                    if (string.Equals(city, originBest.City, StringComparison.OrdinalIgnoreCase)) continue;
                    MarketQuote q;
                    if (!byCity.TryGetValue(city, out q) || q.BuyMax <= 0L) continue;
                    if (IsStale(q.BuyMaxDate, now)) continue;
                    long net = (long)Math.Round(q.BuyMax * (1.0d - sellTax), MidpointRounding.AwayFromZero) - originBest.SellMin;
                    if (net > bestNet)
                    {
                        bestNet = net;
                        bestDest = city;
                    }
                }
            }
            return bestDest;
        }

        /// <summary>按排序口径原地排序（每 kg 未知排最后，再按单件净利降序）。</summary>
        public static void SortInPlace(List<TradeLeg> legs, TradeSort sort, double weightLimitKg)
        {
            if (legs == null) return;
            legs.Sort(delegate (TradeLeg a, TradeLeg b)
            {
                if (sort == TradeSort.PerKg)
                {
                    bool aHas = a.PerKg.HasValue;
                    bool bHas = b.PerKg.HasValue;
                    if (aHas && bHas)
                    {
                        int c = b.PerKg.Value.CompareTo(a.PerKg.Value);
                        if (c != 0) return c;
                    }
                    else if (aHas != bHas)
                    {
                        return aHas ? -1 : 1;
                    }
                }
                else if (sort == TradeSort.PerTrip)
                {
                    long pa = TripProfitFor(a, weightLimitKg);
                    long pb = TripProfitFor(b, weightLimitKg);
                    int c = pb.CompareTo(pa);
                    if (c != 0) return c;
                }
                int n = b.NetPerUnit.CompareTo(a.NetPerUnit);
                return n != 0 ? n : string.Compare(a.ItemId, b.ItemId, StringComparison.Ordinal);
            });
        }

        /// <summary>
        /// 单件净利 × 这件物品单独装满负重/深度时能装的件数（排序「单趟利润」用）。
        /// **深度未知时返回 0**：不拿「负重件数」冒充挂单承接量，避免把单趟利润算虚。
        /// </summary>
        public static long TripProfitFor(TradeLeg leg, double weightLimitKg)
        {
            if (leg == null || !leg.DepthKnown) return 0L;
            return leg.NetPerUnit * PiecesFor(leg, weightLimitKg);
        }

        /// <summary>只装这一条腿时能装的件数 = min(负重装得下的件数, 挂单深度)；重量未知/负重未填返回 0。</summary>
        public static long PiecesFor(TradeLeg leg, double weightLimitKg)
        {
            if (leg == null || leg.NetPerUnit <= 0L) return 0L;
            if (weightLimitKg <= 0d || !leg.WeightKg.HasValue || leg.WeightKg.Value <= 0d) return 0L;
            long byWeight = (long)Math.Floor(weightLimitKg / leg.WeightKg.Value + 1e-9d);
            if (byWeight <= 0L) return 0L;
            if (leg.DepthKnown)
            {
                long byDepth = leg.Depth;
                return byDepth < byWeight ? byDepth : byWeight;
            }
            return byWeight;
        }

        /// <summary>
        /// 负重贪心装填（规范 §4）：盈利腿按每 kg 净利降序，逐件装 min(剩余负重装得下, 挂单深度)。
        /// 重量未知的腿不装（不猜重量）并置 HasUnknownWeight；两种受限（负重/深度）分开暴露给 UI。
        /// </summary>
        public static LoadPlan BuildLoad(IEnumerable<TradeLeg> legs, double weightLimitKg)
        {
            var plan = new LoadPlan { WeightLimitKg = weightLimitKg };
            if (legs == null || weightLimitKg <= 0d) return plan;

            var positive = new List<TradeLeg>();
            foreach (var leg in legs)
                if (leg != null && leg.NetPerUnit > 0L) positive.Add(leg);

            SortInPlace(positive, TradeSort.PerKg, weightLimitKg);

            double used = 0d;
            for (int i = 0; i < positive.Count; i++)
            {
                var leg = positive[i];
                if (!leg.WeightKg.HasValue || leg.WeightKg.Value <= 0d)
                {
                    plan.HasUnknownWeight = true;
                    continue;
                }
                double remaining = weightLimitKg - used;
                if (leg.WeightKg.Value > remaining + 1e-9d)
                {
                    plan.WeightCapped = true; // 继续看更轻的腿，不直接 break（排序键是每 kg 而非重量）
                    continue;
                }
                long pieces = (long)Math.Floor(remaining / leg.WeightKg.Value + 1e-9d);
                if (leg.DepthKnown && leg.Depth < pieces)
                {
                    pieces = leg.Depth;
                    plan.DepthCapped = true;
                }
                if (pieces <= 0L) continue;
                // 浮点兜底：按件数回算不得超过负重上限
                while (pieces > 0L && used + pieces * leg.WeightKg.Value > weightLimitKg + 1e-9d) pieces--;
                if (pieces <= 0L) { plan.WeightCapped = true; continue; }

                var line = new LoadLine
                {
                    Leg = leg,
                    Pieces = pieces,
                    WeightKg = pieces * leg.WeightKg.Value,
                    Profit = leg.NetPerUnit * pieces
                };
                plan.Lines.Add(line);
                used += line.WeightKg;
                plan.TotalPieces += pieces;
                plan.TotalProfit += line.Profit;
            }

            plan.UsedKg = used;
            plan.AllLoaded = !plan.WeightCapped && !plan.DepthCapped && !plan.HasUnknownWeight
                             && plan.Lines.Count == positive.Count;
            return plan;
        }

        /// <summary>路径 A 保本门槛：目的价 / 出发价 &gt; 1/(1−销售税)（规范 §7 调研口径）。</summary>
        public static double PathAThreshold(double sellTax)
        {
            double tax = Math.Max(0d, Math.Min(0.9d, sellTax));
            return 1.0d / (1.0d - tax);
        }

        /// <summary>单个价格点是否陈旧（时间戳可解析且距 now &gt; 24h；缺失/哨兵不判陈旧，与 P0-3 口径一致）。</summary>
        public static bool IsStale(string priceDate, DateTimeOffset now)
        {
            var date = MarketClient.TryParsePriceDate(priceDate);
            return date.HasValue && (now - date.Value) > QuoteStaleAge;
        }

        private static int AgeMinutes(string priceDate, DateTimeOffset now)
        {
            var date = MarketClient.TryParsePriceDate(priceDate);
            if (!date.HasValue) return -1;
            var age = now - date.Value;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            double minutes = age.TotalMinutes;
            if (minutes > int.MaxValue) minutes = int.MaxValue;
            return (int)minutes;
        }

        private static int MinAge(int current, int candidate)
        {
            if (candidate < 0) return current;
            if (current < 0) return candidate;
            return candidate < current ? candidate : current;
        }
    }
}
