using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AlbionCompanion.Core
{
    /// <summary>本地行情库：CSV 落盘，按 (物品,城市,品质) 主键 upsert。</summary>
    public class MarketDb
    {
        public List<MarketQuote> Rows = new List<MarketQuote>();
        private static readonly string[] Header =
            new[] { "item_id", "city", "quality", "sell_min", "sell_min_date", "buy_max", "buy_max_date" };

        // 每个物品最后成功拉取时间（按 baseId）
        private readonly Dictionary<string, DateTime> _lastFetch = new Dictionary<string, DateTime>();
        private const int StaleMinutes = 120; // 2 小时内不重复拉

        /// <summary>返回超过 StaleMinutes 未拉取的物品列表。</summary>
        public List<string> GetStaleItems(List<string> allItems)
        {
            var result = new List<string>();
            foreach (var id in allItems)
            {
                string baseId = BaseOf(id);
                DateTime t;
                if (!_lastFetch.TryGetValue(baseId, out t) || (DateTime.Now - t).TotalMinutes >= StaleMinutes)
                    result.Add(baseId);
            }
            return result;
        }

        /// <summary>标记一批物品已在某时间成功拉取。</summary>
        public void MarkFetched(IEnumerable<string> itemIds)
        {
            DateTime now = DateTime.Now;
            foreach (var id in itemIds)
            {
                string baseId = BaseOf(id);
                _lastFetch[baseId] = now;
            }
        }

        /// <summary>保存拉取时间戳到单独文件。</summary>
        public void SaveFetchTimes(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "fetched_times.txt");
                var sb = new StringBuilder();
                foreach (var kv in _lastFetch)
                    sb.Append(kv.Key).Append('=').Append(kv.Value.Ticks).Append('\n');
                File.WriteAllText(path, sb.ToString());
            }
            catch { }
        }

        /// <summary>加载拉取时间戳。</summary>
        public void LoadFetchTimes(string dir)
        {
            try
            {
                var path = Path.Combine(dir, "fetched_times.txt");
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    long ticks;
                    if (long.TryParse(line.Substring(eq + 1), out ticks))
                        _lastFetch[line.Substring(0, eq)] = new DateTime(ticks);
                }
            }
            catch { }
        }

        public MarketQuote Find(string itemId, string city, int quality)
        {
            string baseId = (itemId ?? "").Split('@')[0];
            for (int i = 0; i < Rows.Count; i++)
            {
                if (Rows[i].Quality == quality && Rows[i].City == city &&
                    BaseOf(Rows[i].ItemId) == baseId)
                    return Rows[i];
            }
            return null;
        }

        public void Upsert(MarketQuote q)
        {
            if (q == null || string.IsNullOrEmpty(q.ItemId) || string.IsNullOrEmpty(q.City)) return;
            string baseId = BaseOf(q.ItemId);
            for (int i = 0; i < Rows.Count; i++)
            {
                if (Rows[i].Quality == q.Quality && Rows[i].City == q.City &&
                    BaseOf(Rows[i].ItemId) == baseId)
                {
                    Rows[i] = q;
                    return;
                }
            }
            Rows.Add(q);
        }

        public void UpsertBatch(IEnumerable<MarketQuote> quotes)
        {
            if (quotes == null) return;
            foreach (var q in quotes) Upsert(q);
        }

        /// <summary>按物品 + 城市查本地库。</summary>
        public List<MarketQuote> Query(List<string> itemIds, List<string> cities)
        {
            var wanted = new HashSet<string>();
            foreach (var id in itemIds) wanted.Add(BaseOf(id));
            var wantCities = new HashSet<string>();
            foreach (var c in cities) wantCities.Add(c);

            var result = new List<MarketQuote>();
            for (int i = 0; i < Rows.Count; i++)
            {
                if (wantCities.Contains(Rows[i].City) && wanted.Contains(BaseOf(Rows[i].ItemId)))
                    result.Add(Rows[i]);
            }
            result.Sort(delegate(MarketQuote a, MarketQuote b)
            {
                int c = string.Compare(a.ItemId, b.ItemId, StringComparison.Ordinal);
                if (c != 0) return c;
                c = string.Compare(a.City, b.City, StringComparison.Ordinal);
                if (c != 0) return c;
                return a.Quality.CompareTo(b.Quality);
            });
            return result;
        }

        public List<string> KnownItemIds()
        {
            var set = new SortedSet<string>();
            for (int i = 0; i < Rows.Count; i++) set.Add(BaseOf(Rows[i].ItemId));
            return new List<string>(set);
        }

        public static string BaseOf(string itemId)
        {
            return (itemId ?? "").Split('@')[0];
        }

        public static List<List<string>> Chunk(List<string> items, int size)
        {
            var chunks = new List<List<string>>();
            if (items == null) return chunks;
            for (int i = 0; i < items.Count; i += size)
            {
                int take = Math.Min(size, items.Count - i);
                var part = new List<string>();
                for (int j = 0; j < take; j++) part.Add(items[i + j]);
                chunks.Add(part);
            }
            return chunks;
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", Header));
            for (int i = 0; i < Rows.Count; i++)
            {
                var q = Rows[i];
                sb.Append(q.ItemId).Append(',')
                  .Append(q.City).Append(',')
                  .Append(q.Quality).Append(',')
                  .Append(q.SellMin).Append(',')
                  .Append(Escape(q.SellMinDate)).Append(',')
                  .Append(q.BuyMax).Append(',')
                  .Append(Escape(q.BuyMaxDate)).Append('\n');
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public static MarketDb Load(string path)
        {
            var db = new MarketDb();
            try
            {
                if (!File.Exists(path)) return db;
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
                    db.Rows.Add(new MarketQuote
                    {
                        ItemId = cells[0],
                        City = cells[1],
                        Quality = quality,
                        SellMin = sellMin,
                        SellMinDate = cells[4],
                        BuyMax = buyMax,
                        BuyMaxDate = cells[6]
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
