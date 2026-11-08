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
                    if (q.BuyMax > 0 && (bestBuy == null || q.BuyMax > bestBuy.BuyMax)) bestBuy = q;
                    if (q.SellMin > 0 && (bestSell == null || q.SellMin < bestSell.SellMin)) bestSell = q;
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
    }
}
