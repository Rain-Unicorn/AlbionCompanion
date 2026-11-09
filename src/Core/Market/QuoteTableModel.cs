#nullable disable // 与仓库既有 Core 文件风格一致
using System;
using System.Collections.Generic;

namespace AlbionCompanion.Core
{
    /// <summary>数据时效三档（设计 §5.4：≤15 分钟可用 / 15–30 偏旧 / &gt;30 已过期）。</summary>
    public enum QuoteFreshness
    {
        Fresh,   // 可用（heal）
        Aging,   // 偏旧（warn）
        Expired  // 已过期（warn，不参与最低买入）
    }

    /// <summary>单格的形态（设计 §5.2「零值四种」在 AODP 可用字段下的对应）。</summary>
    public enum QuoteCellKind
    {
        /// <summary>有价：正常显示数字。</summary>
        Value,
        /// <summary>价格 0，但该城有采集时间戳 → 该城该物品确实没有挂单。</summary>
        NoOrders,
        /// <summary>价格 0 且无时间戳 → 该城没有采集到数据。</summary>
        NoData
    }

    /// <summary>
    /// 一城一行的报价视图。价格字段只有数值，**不含任何差价 / 税 / 利润**——
    /// 市场页只查价，算账归跑商页（任务边界，用户原话「跑商相关不要放到市场模块」）。
    /// </summary>
    public class QuoteViewRow
    {
        public string City;
        /// <summary>买入价 = 你为此付出 = AODP sell_price_min（最低挂单）。</summary>
        public long BuyPrice;
        /// <summary>卖出价 = 你据此到手 = AODP buy_price_max（最高收购单）。</summary>
        public long SellPrice;

        public QuoteCellKind BuyKind;
        public QuoteCellKind SellKind;

        /// <summary>买入侧字段（sell_price_min）在 AODP 响应里是否存在：区分「字段缺失」与「字段值为 0」。</summary>
        public bool BuyFieldPresent = true;
        /// <summary>卖出侧字段（buy_price_max）是否存在。</summary>
        public bool SellFieldPresent = true;

        /// <summary>本城自己的数据时间（取自该城的 sell_price_min_date），不是页头一个总时间。</summary>
        public DateTimeOffset? BuyDataAt;
        public DateTimeOffset? SellDataAt;

        /// <summary>
        /// 本城数据距 now 的**行级**时效档（取买入/卖出两路里较新的那一路，用于整行时间列与弱化）。
        /// 单侧判定请用 <see cref="BuyFreshness"/> / <see cref="SellFreshness"/>——
        /// 同一个城市两路时间戳可以差很多（卖单刚采、买单是昨天的），行级档不能代替单侧档。
        /// </summary>
        public QuoteFreshness Freshness;

        /// <summary>买入侧（sell_price_min_date）自带的时效档；无时间戳/无数据 = Expired。</summary>
        /// <remarks>
        /// P0 修复：旧实现只有一个行级 <see cref="Freshness"/>（取两路较新），底注极值买卖两侧
        /// 都用它判过期 → 「卖价是昨天、买价刚刚」的行，卖出价会被当成新鲜价参与「最高卖出」。
        /// </remarks>
        public QuoteFreshness BuyFreshness;
        /// <summary>卖出侧（buy_price_max_date）自带的时效档；无时间戳/无数据 = Expired。</summary>
        public QuoteFreshness SellFreshness;

        /// <summary>整行弱化：本城数据距今 &gt; 24h（各城可差十几小时，故必须逐城判）。</summary>
        public bool Stale;

        /// <summary>本城数据年龄（分钟，取两路较新者）；无时间为 -1。</summary>
        public int AgeMinutes = -1;

        /// <summary>买入侧数据年龄（分钟）；无时间戳为 -1。</summary>
        public int BuyAgeMinutes = -1;
        /// <summary>卖出侧数据年龄（分钟）；无时间戳为 -1。</summary>
        public int SellAgeMinutes = -1;
    }

    /// <summary>底注极值（只说价格事实，不给差额——差额是算账结论，归跑商页）。</summary>
    public class QuoteExtremes
    {
        public long MinBuy;
        public string MinBuyCity;
        public long MaxSell;
        public string MaxSellCity;
    }

    /// <summary>
    /// 报价卡的纯逻辑：把一批 MarketQuote 折成「六城六行」的视图 + 底注极值 + 空态判定。
    /// 放在 Core 而不是控件里：逐城时间戳与 0 值语义是本轮最容易错的地方，
    /// 只有在这里才能用离线测试逐条验（控件在 Linux 上跑不起来，只能靠肉眼）。
    /// </summary>
    public static class QuoteTableModel
    {
        /// <summary>亚服六城（顺序固定，保证同一物品每次渲染行序一致）。</summary>
        public static readonly string[] AsiaCities =
            { "Caerleon", "Bridgewatch", "Martlock", "Thetford", "Fort Sterling", "Lymhurst" };

        public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan AgingWindow = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan StaleWindow = TimeSpan.FromHours(24);

        /// <summary>报价卡整体状态（设计 §六 九态里与报价卡直接相关的几个）。</summary>
        public enum CardState
        {
            /// <summary>M1/M2/M7：有行可画（提示条由外层叠加）。</summary>
            Rows,
            /// <summary>M3：有数据但卖出价全 0（亚服暂无收购单）。</summary>
            AllSellEmpty,
            /// <summary>M4：整件无挂单（买卖全 0 或无数据）。</summary>
            NoListingAtAll,
            /// <summary>M5：三级未选满，不渲染任何行。</summary>
            NotSelected,
            /// <summary>M8：取不到数据且无旧数据。</summary>
            NoDataAtAll
        }

        /// <summary>时效档判定（纯函数）。</summary>
        public static QuoteFreshness Classify(TimeSpan age)
        {
            if (age <= FreshWindow) return QuoteFreshness.Fresh;
            if (age <= AgingWindow) return QuoteFreshness.Aging;
            return QuoteFreshness.Expired;
        }

        /// <summary>单格形态：0 价 + 有时间戳 = 无挂单；0 价 + 无时间戳 = 无采集数据。</summary>
        public static QuoteCellKind KindOf(long price, string dateText)
        {
            return KindOf(price, dateText, true);
        }

        /// <summary>
        /// 单格形态（区分「字段缺失」与「字段为 0」，设计规范 §八.7）：
        ///   ① 字段缺失（present=false）→ 无数据（契约异常/未上报），即使价格为 0 也不能说「无挂单」；
        ///   ② 有价 → 正常价格；
        ///   ③ 0 价 + 有时间戳 → 该城确实无挂单；
        ///   ④ 0 价 + 无时间戳 → 该城未采集到数据。
        /// </summary>
        public static QuoteCellKind KindOf(long price, string dateText, bool fieldPresent)
        {
            if (!fieldPresent) return QuoteCellKind.NoData;
            if (price > 0) return QuoteCellKind.Value;
            return MarketClient.TryParsePriceDate(dateText) == null ? QuoteCellKind.NoData : QuoteCellKind.NoOrders;
        }

        /// <summary>
        /// 把一批（同一物品的）行情折成六城视图。cities 为空时用亚服六城全集。
        /// 同一 (city, quality) 有多条时只取一个品质（优先 1，否则最小），品质切换属于筛选器的事。
        /// </summary>
        public static List<QuoteViewRow> Build(IEnumerable<MarketQuote> quotes,
            IReadOnlyCollection<string> cities, DateTimeOffset now, out int qualityUsed)
        {
            qualityUsed = 1;
            var list = new List<QuoteViewRow>();
            if (quotes == null) return list;

            var wanted = new List<string>();
            if (cities != null && cities.Count > 0)
            {
                var seen = new HashSet<string>();
                foreach (var c in cities) if (!string.IsNullOrEmpty(c) && seen.Add(c)) wanted.Add(c);
            }
            else
            {
                wanted.AddRange(AsiaCities);
            }

            var all = new List<MarketQuote>();
            foreach (var q in quotes) if (q != null) all.Add(q);

            if (all.Count > 0)
            {
                int best = int.MaxValue;
                foreach (var q in all) if (q.Quality > 0 && q.Quality < best) best = q.Quality;
                qualityUsed = best == int.MaxValue ? 1 : best;
            }

            var byCity = new Dictionary<string, MarketQuote>();
            foreach (var q in all)
            {
                if (q.Quality != qualityUsed) continue;
                byCity[q.City] = q;
            }

            foreach (var city in wanted)
            {
                MarketQuote q;
                var row = new QuoteViewRow { City = city };
                if (!byCity.TryGetValue(city, out q))
                {
                    // 缓存里没有这个城市的行：明确画「无数据」，不画 0、不画空白
                    row.BuyKind = QuoteCellKind.NoData;
                    row.SellKind = QuoteCellKind.NoData;
                    row.Freshness = QuoteFreshness.Expired;
                    row.BuyFreshness = QuoteFreshness.Expired;
                    row.SellFreshness = QuoteFreshness.Expired;
                    row.Stale = true;
                    list.Add(row);
                    continue;
                }

                row.BuyPrice = q.SellMin;   // 买入 = 你付出 = sell_price_min（最低挂单）
                row.SellPrice = q.BuyMax;   // 卖出 = 你到手 = buy_price_max（最高收购单）
                row.BuyFieldPresent = q.SellMinPresent;
                row.SellFieldPresent = q.BuyMaxPresent;
                row.BuyKind = KindOf(q.SellMin, q.SellMinDate, q.SellMinPresent);
                row.SellKind = KindOf(q.BuyMax, q.BuyMaxDate, q.BuyMaxPresent);
                row.BuyDataAt = MarketClient.TryParsePriceDate(q.SellMinDate);
                row.SellDataAt = MarketClient.TryParsePriceDate(q.BuyMaxDate);

                // 单侧时效：各用各的时间戳（买卖两路互不背书）
                SetSideAge(row.BuyDataAt, now, true, out int buyAge, out QuoteFreshness buyFresh);
                SetSideAge(row.SellDataAt, now, false, out int sellAge, out QuoteFreshness sellFresh);
                row.BuyAgeMinutes = buyAge;
                row.SellAgeMinutes = sellAge;
                row.BuyFreshness = buyFresh;
                row.SellFreshness = sellFresh;

                // 逐城行级时间：取本城两路里较新的那个（仅用于行级弱化/时间列底色）
                DateTimeOffset? newest = null;
                if (row.BuyDataAt.HasValue) newest = row.BuyDataAt;
                if (row.SellDataAt.HasValue && (!newest.HasValue || row.SellDataAt.Value > newest.Value))
                    newest = row.SellDataAt;

                if (newest.HasValue)
                {
                    var age = now - newest.Value;
                    if (age < TimeSpan.Zero) age = TimeSpan.Zero; // 采集端时钟略超前时不显示负年龄
                    row.AgeMinutes = (int)age.TotalMinutes;
                    row.Freshness = Classify(age);
                    row.Stale = age > StaleWindow;
                }
                else
                {
                    row.AgeMinutes = -1;
                    row.Freshness = QuoteFreshness.Expired;
                    row.Stale = true;
                }
                list.Add(row);
            }
            return list;
        }

        /// <summary>
        /// 单侧年龄/时效：时间戳缺失或早于 2000 哨兵 → 该侧 Expired、年龄 -1（不把「无时间」当新鲜）。
        /// 采集端时钟略超前时钳到 0，不产生负年龄。
        /// </summary>
        private static void SetSideAge(DateTimeOffset? at, DateTimeOffset now, bool isBuy,
            out int ageMinutes, out QuoteFreshness freshness)
        {
            if (!at.HasValue)
            {
                ageMinutes = -1;
                freshness = QuoteFreshness.Expired;
                return;
            }
            var age = now - at.Value;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            ageMinutes = (int)age.TotalMinutes;
            freshness = Classify(age);
        }

        /// <summary>
        /// 底注极值：只在「有价 + **该侧自己**未过期」的行里比，0 值与过期值一律排除并计数（excluded 按格计）。
        /// 有任一格被排除，底注就必须写清排除了几格——否则用户看到的「最低价」可能是错的。
        /// 买入侧用 <see cref="QuoteViewRow.BuyFreshness"/>、卖出侧用 <see cref="QuoteViewRow.SellFreshness"/>，
        /// 两路时间戳互不背书（P0：旧实现用行级最新时间给买卖两侧背书）。
        /// </summary>
        public static QuoteExtremes Extremes(IEnumerable<QuoteViewRow> rows, out int excluded)
        {
            excluded = 0;
            var ex = new QuoteExtremes();
            if (rows == null) return ex;
            foreach (var r in rows)
            {
                if (r == null) continue;
                // 最低买入：0 价与买入侧已过期不参与（拿一小时前的价当全服最低会让人跑冤枉路）
                if (r.BuyKind == QuoteCellKind.Value && r.BuyFreshness != QuoteFreshness.Expired)
                {
                    if (ex.MinBuy <= 0 || r.BuyPrice < ex.MinBuy) { ex.MinBuy = r.BuyPrice; ex.MinBuyCity = r.City; }
                }
                else excluded++;

                // 最高卖出：0 价与卖出侧已过期不参与
                if (r.SellKind == QuoteCellKind.Value && r.SellFreshness != QuoteFreshness.Expired)
                {
                    if (r.SellPrice > ex.MaxSell) { ex.MaxSell = r.SellPrice; ex.MaxSellCity = r.City; }
                }
                else excluded++;
            }
            return ex;
        }

        /// <summary>整卡状态判定（M3 / M4 / M8 的分歧点，避免 UI 里写一串 if）。</summary>
        public static CardState StateOf(IList<QuoteViewRow> rows)
        {
            if (rows == null || rows.Count == 0) return CardState.NoDataAtAll;
            int anyValue = 0, anyBuy = 0, anySell = 0;
            foreach (var r in rows)
            {
                if (r.BuyKind == QuoteCellKind.Value) { anyValue++; anyBuy++; }
                if (r.SellKind == QuoteCellKind.Value) { anyValue++; anySell++; }
            }
            if (anyValue == 0) return CardState.NoListingAtAll;
            if (anySell == 0) return CardState.AllSellEmpty;
            return CardState.Rows;
        }

        /// <summary>
        /// 单格时间文案。硬要求：每格用它自己那一路的时间戳，**不允许**页头一个总时间了事
        /// （各城可差十几小时）。同源同精度：底色档 + 相对时间都出自这里。
        /// </summary>
        public static string CellTimeText(DateTimeOffset? at, DateTimeOffset now)
        {
            if (!at.HasValue || at.Value.UtcDateTime.Year < 2000) return "无数据";
            var age = now - at.Value;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            if (age.TotalMinutes < 1) return "刚刚";
            if (age.TotalMinutes < 60) return (int)age.TotalMinutes + " 分钟前";
            if (age.TotalHours < 24) return (int)age.TotalHours + " 小时前";
            return (int)age.TotalDays + " 天前";
        }

        /// <summary>格内时间文案带时效后缀（设计 §5.4：24 分 · 偏旧 / 42 分 · 过期）。</summary>
        public static string CellTimeTextWithTier(DateTimeOffset? at, DateTimeOffset now)
        {
            if (!at.HasValue || at.Value.UtcDateTime.Year < 2000) return "无数据";
            string baseText = CellTimeText(at, now);
            var tier = Classify(now - at.Value);
            if (tier == QuoteFreshness.Fresh) return baseText;
            return baseText + (tier == QuoteFreshness.Aging ? " · 偏旧" : " · 过期");
        }

        /// <summary>
        /// 单侧相对时间（**紧凑形态**，供报价卡 108px 时间列放买卖两路）：
        /// 「刚刚 / N分 / N时 / N天」；无时间戳 = 「无」。所有相对时间都从本函数出，同源同精度。
        /// </summary>
        public static string CellTimeCompact(DateTimeOffset? at, DateTimeOffset now)
        {
            if (!at.HasValue || at.Value.UtcDateTime.Year < 2000) return "无";
            var age = now - at.Value;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            if (age.TotalMinutes < 1) return "刚刚";
            if (age.TotalMinutes < 60) return (int)age.TotalMinutes + "分";
            if (age.TotalHours < 24) return (int)age.TotalHours + "时";
            return (int)age.TotalDays + "天";
        }

        /// <summary>
        /// 数据时间列文案：买入/卖出**各自**用自己的时间戳（↓ = 买入侧 sell_price_min_date，
        /// ↑ = 卖出侧 buy_price_max_date）。旧控件把两路里较新的那个同时盖在两侧上，
        /// 会出现「卖出价是昨天的，时间列却显示刚刚」的误导。
        /// </summary>
        public static string SideTimeSummary(QuoteViewRow row, DateTimeOffset now)
        {
            if (row == null) return "无数据";
            if (row.BuyKind == QuoteCellKind.NoData && row.SellKind == QuoteCellKind.NoData)
                return "无数据";
            return "↓" + CellTimeCompact(row.BuyDataAt, now)
                   + " · ↑" + CellTimeCompact(row.SellDataAt, now);
        }

        /// <summary>两路里更差的一档（Fresh &lt; Aging &lt; Expired），供时间列/整行标色。</summary>
        public static QuoteFreshness WorstSideFreshness(QuoteViewRow row)
        {
            if (row == null) return QuoteFreshness.Expired;
            return (QuoteFreshness)Math.Max((int)row.BuyFreshness, (int)row.SellFreshness);
        }
    }
}
