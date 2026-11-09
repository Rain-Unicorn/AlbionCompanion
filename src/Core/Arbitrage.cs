using System;
using System.Collections.Generic;
using AlbionCompanion.Core.Trade;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// 一条跨城跑商路线（已修正买卖角色）：
    /// **出发城按 sell_price_min 吃单买入（付钱），目的城按 buy_price_max 吃单卖出（收钱）**。
    /// 原实现把「买价最高的城」当进货地，买卖接反（规范 §10），本类字段按正确语义填充。
    /// </summary>
    public class Route
    {
        public string ItemId;
        public string BuyCity;      // 出发城（吃单买入）
        public long BuyPrice;       // 出发城 sell_price_min
        public string SellCity;     // 目的城（吃单卖出）
        public long SellPrice;      // 目的城 buy_price_max
        public long NetProfit;      // 单件净利（税后）
        public double ReturnPct;

        /// <summary>品质（1..5）；按 (item, quality) 分组，绝不跨品质混选。</summary>
        public int Quality;

        /// <summary>物品重量 kg（来自本地物品元数据）；缺失为 null，绝不当 0。</summary>
        public double? WeightKg;

        /// <summary>挂单承接量（深度）；0 = 未知（当前无数据源，见 TradeEngine.IDepthProvider）。</summary>
        public long Depth;
    }

    /// <summary>
    /// 跨城跑商套利计算（P0 修正版）：
    /// 出发城吃现成挂单买入（sell_price_min），目的城吃现成买单卖出（buy_price_max）。
    /// 税口径按规范 §7：单件净利 = buy_price_max × (1 − 销售税) − sell_price_min。
    /// 买卖双方都是「吃现成挂单」，不产生挂单创建费；黑市是 NPC，同样无费。
    /// 旧 `createFee` 参数保留只为兼容既有调用签名，**不参与净利计算**（否则会破坏规范对账的 3200）。
    /// </summary>
    public static class Arbitrage
    {
        /// <param name="sellTax">出售交易税（F2P 8% / Premium 4%）</param>
        /// <param name="createFee">历史参数，保留签名兼容；新口径下吃单交易无挂单创建费，故不参与计算</param>
        /// <param name="minNet">最低单件净利，低于此过滤</param>
        public static List<Route> Compute(List<MarketQuote> quotes, double sellTax, double createFee, long minNet)
        {
            int ignoredStale;
            return Compute(quotes, sellTax, createFee, minNet, DateTimeOffset.UtcNow, out ignoredStale);
        }

        /// <summary>陈旧报价判定阈值：与 MarketClient.SellStaleAge 同源（24 小时）。</summary>
        public static TimeSpan QuoteStaleAge { get { return MarketClient.SellStaleAge; } }

        /// <summary>
        /// 带「陈旧报价剔除数」出口的重载（P0-3）。now 可注入以便单测。
        /// droppedStale 统计被 24h 剔除的价格点（出发城卖价侧 + 目的城买价侧各计一条），供 UI 提示。
        /// </summary>
        /// <remarks>
        /// 陈旧判定必须从原始时间戳重算：MarketDb.Load 只回填价格/时间戳，不会重算 SellStale 标记。
        /// 时间戳缺失/无法解析（含 0001 哨兵）不判陈旧——与 MarketClient.IsSellStale 既有口径一致。
        /// </remarks>
        public static List<Route> Compute(List<MarketQuote> quotes, double sellTax, double createFee, long minNet,
                                          DateTimeOffset now, out int droppedStale)
        {
            droppedStale = 0;
            var routes = new List<Route>();
            if (quotes == null) return routes;
            if (sellTax < 0d) sellTax = 0d;
            if (sellTax > 0.9d) sellTax = 0.9d;

            // 按 (物品, 品质) 分组：禁止跨品质混选（原实现按 baseId 分组会把品质 1..5 混成一锅）
            var byKey = new Dictionary<string, List<MarketQuote>>(StringComparer.Ordinal);
            foreach (var q in quotes)
            {
                if (q == null || string.IsNullOrEmpty(q.ItemId)) continue;
                string key = q.ItemId + "|" + q.Quality;
                List<MarketQuote> list;
                if (!byKey.TryGetValue(key, out list))
                {
                    list = new List<MarketQuote>();
                    byKey[key] = list;
                }
                list.Add(q);
            }

            foreach (var kv in byKey)
            {
                // 出发城：最低 sell_price_min（我们吃单买入）；黑市只做目的城，永不做出发城
                MarketQuote bestOrigin = null;
                // 目的城：最高 buy_price_max（我们吃单卖出）
                MarketQuote bestDest = null;

                foreach (var q in kv.Value)
                {
                    if (q.SellMin > 0L && !TradeCities.IsBlackMarket(q.City))
                    {
                        if (IsQuoteDateStale(q.SellMinDate, now)) droppedStale++;
                        else if (bestOrigin == null || q.SellMin < bestOrigin.SellMin) bestOrigin = q;
                    }
                    if (q.BuyMax > 0L)
                    {
                        if (IsQuoteDateStale(q.BuyMaxDate, now)) droppedStale++;
                        else if (bestDest == null || q.BuyMax > bestDest.BuyMax) bestDest = q;
                    }
                }

                if (bestOrigin == null || bestDest == null) continue;
                if (string.Equals(bestOrigin.City, bestDest.City, StringComparison.OrdinalIgnoreCase)) continue;

                // 规范 §7：税后卖入所得 = buy_price_max × (1 − 销售税)；吃单成本 = sell_price_min
                long sellNet = (long)Math.Round(bestDest.BuyMax * (1.0d - sellTax), MidpointRounding.AwayFromZero);
                long net = sellNet - bestOrigin.SellMin;
                if (net < minNet) continue;

                routes.Add(new Route
                {
                    ItemId = kv.Value[0].ItemId,
                    Quality = kv.Value[0].Quality,
                    BuyCity = bestOrigin.City,
                    BuyPrice = bestOrigin.SellMin,
                    SellCity = bestDest.City,
                    SellPrice = bestDest.BuyMax,
                    NetProfit = net,
                    ReturnPct = bestOrigin.SellMin > 0L ? net * 100.0d / bestOrigin.SellMin : 0d,
                    WeightKg = ItemIndex.Default != null ? ItemIndex.Default.WeightKgOf(kv.Value[0].ItemId) : null,
                    Depth = 0L
                });
            }

            routes.Sort(delegate (Route a, Route b)
            {
                int c = b.NetProfit.CompareTo(a.NetProfit);
                if (c != 0) return c;
                return string.Compare(a.ItemId, b.ItemId, StringComparison.Ordinal);
            });
            return routes;
        }

        /// <summary>
        /// 单个价格点是否陈旧：时间戳可解析且距 now 超过 24h。
        /// 时间戳缺失 / 无法解析时**不判陈旧**（无法证伪即保持可用），与
        /// MarketClient.IsSellStale 的既有口径一致，也避免把「无时间列的历史本地库」全量清空。
        /// </summary>
        public static bool IsQuoteDateStale(string priceDate, DateTimeOffset now)
        {
            var date = MarketClient.TryParsePriceDate(priceDate);
            return date.HasValue && (now - date.Value) > MarketClient.SellStaleAge;
        }
    }
}
