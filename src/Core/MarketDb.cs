using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// 本地行情库：CSV 落盘，按 (物品 uniqueName, 城市, 品质) 主键 upsert。
    ///
    /// 线程模型（P0 修复）：两个后台 worker（市场页回源 / 自动更新）写，UI 60s 定时读 + 跑商页读，
    /// 旧实现裸 List 无任何锁 → 读写并发时 List 内部数组扩容可让读者看到 null 行/漏行/抛异常。
    /// 现在：
    ///   ① 所有公开方法都在同一把锁内；
    ///   ② <see cref="Rows"/> 返回**副本快照**（读法保持 List 兼容，调用方不能借它改库）；
    ///   ③ <see cref="Save"/> 用 tmp + Replace 原子写，崩溃/断电不会留下半截 CSV 把历史全废掉；
    ///   ④ 主键用完整 uniqueName（含 @附魔），附魔变体不再互相覆盖。
    ///
    /// 数据层「无上报」过滤：**只有 sell_price_min 与 buy_price_max 两侧都为 0** 才视为无上报，
    /// 这种行在 Upsert / Load / Rows / Save 都不进入本地库（不把 0 写进 CSV、不让 0 进 UI）。
    /// 单侧 0 必须保留——凯尔莱恩黑市是 NPC、只有买单（sell=0 / buy>0），过滤掉它跑商黑市腿就没了；
    /// 市场页仍把 sell 侧 0 显示成「暂无挂单」空态（显示口径 ≠ 持久化口径）。
    /// 缓存层仍保留 HasResponse 行用于防重复请求（见 MarketDataService）。
    /// </summary>
    public class MarketDb
    {
        public static readonly string[] Header =
            new[] { "item_id", "city", "quality", "sell_min", "sell_min_date", "buy_max", "buy_max_date" };

        /// <summary>2 小时内不重复拉（按 baseId 记，附魔共享同一基名的拉取节流）。</summary>
        public const int StaleMinutes = 120;

        private readonly object _sync = new object();
        private readonly List<MarketQuote> _rows = new List<MarketQuote>();

        /// <summary>每个物品最后成功拉取时间（按 baseId）。</summary>
        private readonly Dictionary<string, DateTime> _lastFetch = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>被「两侧均无上报」过滤丢弃的报价条数（诊断/测试用，证明过滤真的在数据层发生）。</summary>
        private int _skippedNoData;

        public int SkippedNoDataCount { get { lock (_sync) { return _skippedNoData; } } }

        /// <summary>
        /// 全部本地行情行的**快照**：返回一个新 List，调用方排序/清空不影响库内数据。
        /// 保留 List 读法（Count/索引/foreach/TradeTab 的 <c>var all = Rt.Db.Rows</c>）以兼容既有调用方。
        /// 两侧都为 0（完全无上报）的行永远不出现在快照里；单侧 0（黑市只有买单）保留。
        /// </summary>
        public List<MarketQuote> Rows
        {
            get
            {
                lock (_sync)
                {
                    var copy = new List<MarketQuote>(_rows.Count);
                    for (int i = 0; i < _rows.Count; i++)
                        if (_rows[i] != null && HasAnyPrice(_rows[i])) copy.Add(_rows[i]);
                    return copy;
                }
            }
        }

        /// <summary>库内行数（快照口径，与 <see cref="Rows"/> 一致）。</summary>
        public int Count { get { lock (_sync) { return CountLocked(); } } }

        private int CountLocked()
        {
            int n = 0;
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i] != null && HasAnyPrice(_rows[i])) n++;
            return n;
        }

        /// <summary>
        /// 数据层「无上报」过滤：**两侧都为 0 才算无上报**（= MarketClient.Parse 的口径）。
        /// 单侧 0 必须保留：凯尔莱恩黑市是 NPC、只有买单（sell_price_min=0 / buy_price_max>0），
        /// 丢掉这类行会导致黑市行情整行消失、跑商黑市腿永远算不出。
        /// 这种完全无上报的行在 Upsert / Load / Rows / Save 都不进入本地库（不把 0 写进 CSV、不让 0 进 UI）；
        /// 市场页的显示口径另算：sell 侧 0 渲染成「暂无挂单」空态，而不是把整行藏掉。
        /// </summary>
        private static bool HasAnyPrice(MarketQuote q)
        {
            return q != null && (q.SellMin > 0 || q.BuyMax > 0);
        }

        // ── 拉取节流（按 baseId）────────────────────────────────────────────

        /// <summary>返回超过 StaleMinutes 未拉取的物品列表（入参可为带附魔/小写 ID，按基名判）。</summary>
        public List<string> GetStaleItems(List<string> allItems)
        {
            var result = new List<string>();
            if (allItems == null) return result;
            lock (_sync)
            {
                DateTime now = DateTime.Now;
                foreach (var id in allItems)
                {
                    string baseId = BaseOf(id);
                    if (baseId.Length == 0) continue;
                    DateTime t;
                    if (!_lastFetch.TryGetValue(baseId, out t) || (now - t).TotalMinutes >= StaleMinutes)
                        result.Add(baseId);
                }
            }
            return result;
        }

        /// <summary>标记一批物品已在当前时间成功拉取。</summary>
        public void MarkFetched(IEnumerable<string> itemIds)
        {
            if (itemIds == null) return;
            lock (_sync)
            {
                DateTime now = DateTime.Now;
                foreach (var id in itemIds)
                {
                    string baseId = BaseOf(id);
                    if (baseId.Length == 0) continue;
                    _lastFetch[baseId] = now;
                }
            }
        }

        /// <summary>保存拉取时间戳到单独文件（原子写）。</summary>
        public void SaveFetchTimes(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                lock (_sync)
                {
                    foreach (var kv in _lastFetch)
                        sb.Append(kv.Key).Append('=').Append(kv.Value.Ticks).Append('\n');
                }
                WriteAtomic(Path.Combine(dir, "fetched_times.txt"), sb.ToString());
            }
            catch { }
        }

        /// <summary>加载拉取时间戳（无文件/损坏 = 空，不抛）。</summary>
        public void LoadFetchTimes(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return;
                var path = Path.Combine(dir, "fetched_times.txt");
                if (!File.Exists(path)) return;
                var parsed = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                foreach (var line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    long ticks;
                    if (!long.TryParse(line.Substring(eq + 1), out ticks)) continue;
                    parsed[line.Substring(0, eq)] = new DateTime(ticks);
                }
                lock (_sync)
                {
                    foreach (var kv in parsed) _lastFetch[kv.Key] = kv.Value;
                }
            }
            catch { }
        }

        // ── 读写（带锁）────────────────────────────────────────────────────

        /// <summary>按完整 (物品 uniqueName, 城市, 品质) 查一行；找不到返回 null。</summary>
        public MarketQuote Find(string itemId, string city, int quality)
        {
            string id = MarketItemKey.Normalize(itemId);
            string wantedCity = MarketCities.Normalize(city);
            lock (_sync)
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    if (row == null) continue;
                    if (row.Quality != quality) continue;
                    if (MarketCities.Normalize(row.City) != wantedCity) continue;
                    if (MarketItemKey.Normalize(row.ItemId) != id) continue;
                    return row;
                }
            }
            return null;
        }

        /// <summary>插入或更新（主键含 @附魔；sell_price_min == 0 的「无上报」行不入库）。</summary>
        public void Upsert(MarketQuote q)
        {
            if (q == null) return;
            lock (_sync) { UpsertLocked(q); }
        }

        /// <summary>批量 upsert（一次加锁；0 价行同样被数据层过滤）。</summary>
        public void UpsertBatch(IEnumerable<MarketQuote> quotes)
        {
            if (quotes == null) return;
            lock (_sync)
            {
                foreach (var q in quotes) UpsertLocked(q);
            }
        }

        private void UpsertLocked(MarketQuote q)
        {
            if (q == null) return;
            string id = MarketItemKey.Normalize(q.ItemId);
            string city = MarketCities.Normalize(q.City);
            if (id.Length == 0 || city.Length == 0) return;

            // 数据层 0 价过滤：**只丢两侧都为 0 的行**（= 完全无上报，不是价格）。
            // 单侧 0 必须保留：凯尔莱恩黑市是 NPC、只有买单 → sell_price_min=0 / buy_price_max>0，
            // 丢掉这类行会把黑市行情整行删掉，跑商「五主城 → 黑市」的腿永远算不出来。
            // 市场页仍把 sell 侧 0 渲染成「暂无挂单」空态（那是显示口径，不是持久化口径）。
            if (!HasAnyPrice(q))
            {
                _skippedNoData++;
                return;
            }

            var normalized = q.Clone();
            normalized.ItemId = id;
            normalized.City = city;

            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row == null) continue;
                if (row.Quality != normalized.Quality) continue;
                if (MarketCities.Normalize(row.City) != city) continue;
                if (MarketItemKey.Normalize(row.ItemId) != id) continue;
                _rows[i] = normalized; // 整体替换，不做原地字段写（读者拿到的旧引用保持完整）
                return;
            }
            _rows.Add(normalized);
        }

        /// <summary>按物品（完整 uniqueName，可带附魔）+ 城市查本地库快照。</summary>
        public List<MarketQuote> Query(List<string> itemIds, List<string> cities)
        {
            var result = new List<MarketQuote>();
            if (itemIds == null || cities == null) return result;

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in itemIds)
            {
                string normalized = MarketItemKey.Normalize(id);
                if (normalized.Length > 0) wanted.Add(normalized);
            }
            var wantCities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in cities)
            {
                string normalized = MarketCities.Normalize(c);
                if (normalized.Length > 0) wantCities.Add(normalized);
            }
            if (wanted.Count == 0 || wantCities.Count == 0) return result;

            lock (_sync)
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    if (row == null || !HasAnyPrice(row)) continue;
                    if (!wantCities.Contains(MarketCities.Normalize(row.City))) continue;
                    if (!wanted.Contains(MarketItemKey.Normalize(row.ItemId))) continue;
                    result.Add(row);
                }
            }
            result.Sort(delegate (MarketQuote a, MarketQuote b)
            {
                int c = string.Compare(a.ItemId, b.ItemId, StringComparison.Ordinal);
                if (c != 0) return c;
                c = string.Compare(a.City, b.City, StringComparison.Ordinal);
                if (c != 0) return c;
                return a.Quality.CompareTo(b.Quality);
            });
            return result;
        }

        /// <summary>已知物品基名集合（附魔折叠成 baseId，去重排序）。</summary>
        public List<string> KnownItemIds()
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            lock (_sync)
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    if (_rows[i] == null || !HasAnyPrice(_rows[i])) continue;
                    string baseId = BaseOf(_rows[i].ItemId);
                    if (baseId.Length > 0) set.Add(baseId);
                }
            }
            return new List<string>(set);
        }

        /// <summary>去掉 @附魔后缀的基名（规范化 + 大写）。</summary>
        public static string BaseOf(string itemId)
        {
            return MarketItemKey.BaseOf(itemId);
        }

        public static List<List<string>> Chunk(List<string> items, int size)
        {
            var chunks = new List<List<string>>();
            if (items == null || size <= 0) return chunks;
            for (int i = 0; i < items.Count; i += size)
            {
                int take = Math.Min(size, items.Count - i);
                var part = new List<string>();
                for (int j = 0; j < take; j++) part.Add(items[i + j]);
                chunks.Add(part);
            }
            return chunks;
        }

        // ── 持久化（原子写）────────────────────────────────────────────────

        /// <summary>
        /// 保存为 CSV：先写 <c>path.tmp</c>，再 Replace/Move 成正式文件。
        /// 旧实现直接 File.WriteAllText 覆盖：写到一半崩溃/断电会留下半截 CSV，
        /// 下次启动整库解析失败（历史全丢）。原子写保证「要么旧文件、要么新文件」。
        /// </summary>
        public void Save(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path 不能为空", "path");

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", Header));
            lock (_sync)
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    var q = _rows[i];
                    if (q == null || !HasAnyPrice(q)) continue; // 两侧都为 0 = 无上报，不入 CSV
                    sb.Append(q.ItemId).Append(',')
                      .Append(q.City).Append(',')
                      .Append(q.Quality).Append(',')
                      .Append(q.SellMin).Append(',')
                      .Append(Escape(q.SellMinDate)).Append(',')
                      .Append(q.BuyMax).Append(',')
                      .Append(Escape(q.BuyMaxDate)).Append('\n');
                }
            }
            WriteAtomic(path, sb.ToString());
        }

        /// <summary>
        /// 写入文本的原子实现：同目录 .tmp → 目标。目标已存在时优先 File.Replace（保留原子性），
        /// 平台/文件系统不支持（不同卷、某些网络盘）才退化为 Delete+Move；无论哪条路，结束都清掉 .tmp。
        /// </summary>
        internal static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tmp, path, null);
                        return;
                    }
                    catch (Exception)
                    {
                        // 部分文件系统不支持 Replace：退化为删除后移动（仍有 tmp 兜底，不会写坏目标）
                        try { File.Delete(path); } catch { }
                        File.Move(tmp, path);
                        return;
                    }
                }
                File.Move(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        public static MarketDb Load(string path)
        {
            var db = new MarketDb();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return db;
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Trim().Length == 0) continue;
                    var cells = lines[i].Split(',');
                    if (cells.Length < 7) continue;
                    long sellMin, buyMax;
                    int quality;
                    long.TryParse(cells[3], out sellMin);
                    long.TryParse(cells[5], out buyMax);
                    int.TryParse(cells[2], out quality);
                    // 数据层无上报过滤：仅两侧都为 0 的历史行不载入；单侧 0（黑市只有买单）必须保留
                    if (sellMin <= 0 && buyMax <= 0) { db._skippedNoData++; continue; }
                    db._rows.Add(new MarketQuote
                    {
                        ItemId = MarketItemKey.Normalize(cells[0]),
                        City = MarketCities.Normalize(cells[1]),
                        Quality = quality,
                        SellMin = sellMin,
                        SellMinDate = cells[4],
                        BuyMax = buyMax,
                        BuyMaxDate = cells[6],
                        SellHasData = true,
                        SellStale = false
                    });
                }
            }
            catch { }
            return db;
        }

        private static string Escape(string s)
        {
            return s ?? "";
        }
    }
}
