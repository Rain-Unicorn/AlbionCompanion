using System;
using System.Collections.Generic;

namespace AlbionCompanion.Core
{
    public class Route
    {
        public string ItemId;
        public string BuyCity;
        public long BuyPrice;
        public string SellCity;
        public long SellPrice;
        public long NetProfit;
        public double ReturnPct;
    }

    /// <summary>跨城跑商套利计算：在 A 城挂收购单收货，运到 B 城挂出售单卖出。</summary>
    public static class Arbitrage
    {
        /// <param name="sellTax">出售交易税（默认 0.08）</param>
        /// <param name="createFee">挂单创建费（买/卖单都收，默认 0.025）</param>
        /// <param name="minNet">最低单件净利，低于此过滤</param>
        public static List<Route> Compute(List<MarketQuote> quotes, double sellTax, double createFee, long minNet)
        {
            int ignoredStale;
            return Compute(quotes, sellTax, createFee, minNet, DateTimeOffset.UtcNow, out ignoredStale);
        }

        /// <summary>陈旧报价判定阈值：与 MarketClient.SellStaleAge 同源（24 小时）。</summary>
        public static TimeSpan QuoteStaleAge { get { return MarketClient.SellStaleAge; } }

        /// <summary>
        /// 带「陈旧报价剔除数」出口的重载（P0-3）。now 可注入以便单测；
        /// droppedStale 返回被剔除的陈旧价格点条数（卖侧 + 买侧各自计一条），供 UI 提示。
        /// </summary>
        /// <remarks>
        /// 陈旧判定**必须从原始字段重算**，不能只读 SellStale 标记：
        /// MarketDb.Load 只回填 ItemId/City/Quality/SellMin/SellMinDate/BuyMax/BuyMaxDate，
        /// 不会重新计算 SellHasData / SellStale（那是 MarketClient.Parse 的产物）。
        /// 若直接依赖 SellStale，本地库读出来的每一行都会被当成「非陈旧」而使本修复失效。
        /// </remarks>
        public static List<Route> Compute(List<MarketQuote> quotes, double sellTax, double createFee, long minNet,
                                          DateTimeOffset now, out int droppedStale)
        {
            droppedStale = 0;
            var routes = new List<Route>();
            if (quotes == null) return routes;

            // 按物品基名分组
            var byItem = new Dictionary<string, List<MarketQuote>>();
            foreach (var q in quotes)
            {
                string baseId = MarketDb.BaseOf(q.ItemId);
                List<MarketQuote> list;
                if (!byItem.TryGetValue(baseId, out list))
                {
                    list = new List<MarketQuote>();
                    byItem[baseId] = list;
                }
                list.Add(q);
            }

            foreach (var kv in byItem)
            {
                // 买：找收购价最高的城（挂收购单收货）
                MarketQuote bestBuy = null;
                // 卖：找挂卖价最低的城（我们过去挂卖单卖给买家）
                MarketQuote bestSell = null;

                foreach (var q in kv.Value)
                {
                    // P0-3：陈旧报价不得参与选价。价格 0 本来就等于「无上报数据」，
                    // 这里额外要求「有价 + 时间戳未超过 24h」，否则一律剔除并计数。
                    if (q.BuyMax > 0)
                    {
                        if (IsQuoteDateStale(q.BuyMaxDate, now)) droppedStale++;
                        else if (bestBuy == null || q.BuyMax > bestBuy.BuyMax) bestBuy = q;
                    }
                    if (q.SellMin > 0)
                    {
                        if (IsQuoteDateStale(q.SellMinDate, now)) droppedStale++;
                        else if (bestSell == null || q.SellMin < bestSell.SellMin) bestSell = q;
                    }
                }

                if (bestBuy == null || bestSell == null) continue;
                if (bestBuy.City == bestSell.City) continue;

                // 卖城净收：卖价扣 8% 交易税，再扣 2.5% 创建费
                double netSell = bestSell.SellMin * (1.0 - sellTax - createFee);
                // 买城成本：买价 + 2.5% 创建费
                double costBuy = bestBuy.BuyMax * (1.0 + createFee);
                long net = (long)Math.Round(netSell - costBuy);
                if (net < minNet) continue;

                routes.Add(new Route
                {
                    ItemId = kv.Key,
                    BuyCity = bestBuy.City,
                    BuyPrice = bestBuy.BuyMax,
                    SellCity = bestSell.City,
                    SellPrice = bestSell.SellMin,
                    NetProfit = net,
                    ReturnPct = costBuy > 0 ? net / costBuy * 100.0 : 0
                });
            }

            routes.Sort(delegate(Route a, Route b)
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
