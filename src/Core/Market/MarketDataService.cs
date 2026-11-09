#nullable disable // 与仓库既有 Core 文件风格一致（主工程存在大量遗留 CS86xx 警告，本文件不新增）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AlbionCompanion.Core
{
    /// <summary>查询用途（决定缓存 TTL 分档与限流优先级）。</summary>
    public enum MarketQueryPurpose
    {
        /// <summary>用户手动查价/比价：TTL 30 分钟；限流入队优先级最高。</summary>
        ManualQuery,

        /// <summary>估值 / 后台刷新：TTL 4 小时；优先级低于任何手动查询。</summary>
        Valuation
    }

    /// <summary>缓存 TTL 分档（M2 定案：查价/比价 30 分钟、估值 4 小时）。</summary>
    public static class MarketCacheTtl
    {
        public static readonly TimeSpan ManualQuery = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan Valuation = TimeSpan.FromHours(4);

        public static TimeSpan For(MarketQueryPurpose purpose)
        {
            return purpose == MarketQueryPurpose.Valuation ? Valuation : ManualQuery;
        }
    }

    /// <summary>
    /// 内存缓存层：键 = (uniqueName, city, quality) 三元组，TTL 是每条缓存项的属性
    /// （写入时按查询用途定档）。AODP 价格只进内存，绝不落盘（进程退出即释放）。
    /// </summary>
    public class MarketCacheEntry
    {
        public MarketQuote Quote;
        /// <summary>
        /// 写入时按查询用途定档的 TTL（30 分钟手动 / 4 小时估值），是这条数据**声明有效**的上限。
        /// 读取时必须参与判定（见 <see cref="IsFresh"/>）：只写不读会让下一个读代码的人以为 TTL 可配。
        /// </summary>
        public TimeSpan Ttl;
        public DateTimeOffset FetchedAt;
        /// <summary>false = 该键 AODP 无上报行（合成"暂无上报数据"条目，避免每轮都重打）。</summary>
        public bool HasResponse;

        /// <summary>
        /// 新鲜度 = 年龄 &lt; min(写入档 TTL, 读取方要求的 TTL)。
        /// 取 min 的理由：写入时那次拉取只承诺了「我这数据在 30 分钟内可信」，读取方再宽容（估值 4 小时档）
        /// 也不得把别人 30 分钟前拉到的价当成 4 小时有效的价——否则跑商估值会拿旧价算净利。
        /// 代价是估值侧会多一些重拉，但重拉走批量 + 限流器，成本可控；把旧价当新价的代价不可控。
        /// </summary>
        public bool IsFresh(DateTimeOffset now, TimeSpan readerTtl)
        {
            var bound = readerTtl;
            if (Ttl > TimeSpan.Zero && Ttl < bound) bound = Ttl;
            return (now - FetchedAt) < bound;
        }
    }

    public class MarketDataCache
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, MarketCacheEntry> _entries =
            new Dictionary<string, MarketCacheEntry>();

        /// <summary>
        /// 缓存键：<c>(规范 itemId, 规范 city, quality)</c>。
        ///
        /// P0 修复（缓存键读写不同源）：旧实现 Put 用 AODP 响应字段（恒大写）、Get 用请求参数，
        /// 用户输入小写 ID/城市别名时 Put 与 Get 落在两个键上 → **小写 ID 永久 miss**，
        /// 每次查询都重打 AODP（还顶限流）。现在两边都过 <see cref="MarketItemKey.Normalize"/> /
        /// <see cref="MarketCities.Normalize"/>，Put/Get/单飞/请求 URL 全部同源。
        /// </summary>
        public static string Key(string itemId, string city, int quality)
        {
            return MarketItemKey.Normalize(itemId) + "|" + MarketCities.Normalize(city) + "|" + quality;
        }

        public int Count
        {
            get { lock (_sync) { return _entries.Count; } }
        }

        public void Put(MarketCacheEntry entry)
        {
            if (entry == null || entry.Quote == null) return;
            lock (_sync)
            {
                _entries[Key(entry.Quote.ItemId, entry.Quote.City, entry.Quote.Quality)] = entry;
            }
        }

        /// <summary>取指定键的缓存项（可能为 null / 可能已过期，由调用方判 IsFresh）。</summary>
        public MarketCacheEntry Get(string itemId, string city, int quality)
        {
            lock (_sync)
            {
                MarketCacheEntry e;
                return _entries.TryGetValue(Key(itemId, city, quality), out e) ? e : null;
            }
        }

        public void Clear()
        {
            lock (_sync) { _entries.Clear(); }
        }

        /// <summary>当前缓存全部条目的快照（供只读渲染用；调用方拿到的是一份浅拷贝列表，条目对象本身只在写入时替换）。</summary>
        public List<MarketCacheEntry> Snapshot()
        {
            lock (_sync) { return new List<MarketCacheEntry>(_entries.Values); }
        }
    }

    /// <summary>
    /// 批量取数层（照搬调研报告第五章② CreatePriceBatches 形状）：
    /// 一次 GET 多物品 × 多城 × 全品质，按 URL ≤ urlLimit（默认 3000，官方上限 4096）切批。
    /// </summary>
    public static class PriceBatcher
    {
        public const int DefaultUrlLimit = 3000;
        public static readonly int[] AllQualities = { 1, 2, 3, 4, 5 };

        /// <summary>估算一批物品拼成 AODP prices URL 的总长度（含基址/路径/城市参数）。</summary>
        public static int EstimateUrlLength(IEnumerable<string> items, IReadOnlyCollection<string> cities)
        {
            string url = MarketClient.BuildPriceUrl(items.ToArray(), cities.ToArray());
            return url.Length;
        }

        /// <summary>
        /// 按 URL 长度切批：逐物品累积，超限即切；单个超长物品独占一批（物品名不可再分）。
        /// 长度按**编码后**的段计（与 BuildPriceUrl 同源），否则带 '@' / '+' / '&amp;' / '%' 的物品名会被算短，
        /// 切出实际超出 urlLimit 的批次。
        /// </summary>
        public static List<List<string>> CreatePriceBatches(IReadOnlyList<string> items,
            IReadOnlyCollection<string> cities, int urlLimit = DefaultUrlLimit)
        {
            var batches = new List<List<string>>();
            if (items == null || items.Count == 0) return batches;

            // 城市参数对每批是固定后缀，先算出批内物品可用的长度预算
            string sampleUrl = MarketClient.BuildPriceUrl(new[] { "X" }, cities != null ? cities.ToArray() : new string[0]);
            int overhead = sampleUrl.Length - 1; // 去掉占位的 "X"
            var current = new List<string>();
            int used = 0; // 当前批物品部分长度（编码后，含逗号）

            foreach (var raw in items)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string id = raw.Trim();
                if (id.Length == 0) continue;
                int cost = MarketClient.EncodeSegment(id).Length + (current.Count > 0 ? 1 : 0); // 非首批物品多一个逗号
                if (current.Count > 0 && overhead + used + cost > urlLimit)
                {
                    batches.Add(current);
                    current = new List<string>();
                    used = 0;
                    cost = MarketClient.EncodeSegment(id).Length;
                }
                current.Add(id);
                used += cost;
            }
            if (current.Count > 0) batches.Add(current);
            return batches;
        }
    }

    /// <summary>
    /// 进程级唯一限流器（M2 定案）：滚动 60 秒窗口内最多 maxPerMinute 次请求
    /// （AODP 上限 100 req/min，自留余量）。等待队列带优先级：
    /// 用户手动查询（priority=true）排在任何后台刷新之前。
    /// 时钟可注入以便单测。
    /// </summary>
    public sealed class MarketRateLimiter
    {
        /// <summary>滚动窗口长度（Prune/唤醒调度/诊断共用同一口径）。</summary>
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

        private readonly object _sync = new object();
        private readonly Queue<DateTimeOffset> _starts = new Queue<DateTimeOffset>();
        private readonly LinkedList<Waiter> _waiters = new LinkedList<Waiter>();
        private readonly int _maxPerMinute;
        private readonly Func<DateTimeOffset> _clock;

        // P0 复核修复：窗口是「滚动"时间"窗」，不是「事件窗」。旧实现只在 Acquire/Release 两个事件里 Pump，
        // 因此「60 个名额用满 → 之后不再有新请求/完成事件」时，排队者永远等不到窗口滚动（真实触发：
        // 市场页大查询 79 批，前 60 批完成后第 61 批永久挂起，UI 卡在「回源 60/79 批」只能重启）。
        // 现在排队存在时按「最早名额到期时刻」自己安排一次唤醒；唤醒回调里重算窗口后再 Pump。
        private readonly System.Threading.Timer _wakeTimer;                       // 内置定时器（默认路径）
        private readonly Func<TimeSpan, Action, IDisposable> _wakeScheduler;      // 可注入调度器（单测用）
        private IDisposable _pendingWake;                                         // 注入调度器返回的句柄

        private sealed class Waiter
        {
            public readonly TaskCompletionSource<bool> Tcs =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly bool Priority;
            public Waiter(bool priority) { Priority = priority; }
        }

        public MarketRateLimiter(int maxPerMinute = 60, Func<DateTimeOffset> clock = null,
            Func<TimeSpan, Action, IDisposable> wakeScheduler = null)
        {
            if (maxPerMinute <= 0) maxPerMinute = 1;
            _maxPerMinute = maxPerMinute;
            _clock = clock ?? (Func<DateTimeOffset>)(() => DateTimeOffset.UtcNow);
            _wakeScheduler = wakeScheduler;
            if (wakeScheduler == null)
            {
                _wakeTimer = new System.Threading.Timer(delegate (object state) { OnWake(); },
                    null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        /// <summary>取得一个请求名额。priority=true（手动查询）插队到所有后台等待者之前。</summary>
        public Task AcquireAsync(bool priority)
        {
            lock (_sync)
            {
                var now = _clock();
                Prune(now);
                Pump(now);
                if (_waiters.Count == 0 && _starts.Count < _maxPerMinute)
                {
                    _starts.Enqueue(now);
                    return Task.CompletedTask;
                }
                var w = new Waiter(priority);
                if (priority)
                {
                    // 插到第一个非优先等待者之前（已有的手动查询仍按先来后到）
                    var node = _waiters.First;
                    while (node != null && node.Value.Priority) node = node.Next;
                    if (node != null) _waiters.AddBefore(node, w); else _waiters.AddLast(w);
                }
                else
                {
                    _waiters.AddLast(w);
                }
                RescheduleWakeLocked(now);
                return w.Tcs.Task;
            }
        }

        /// <summary>请求完成后释放（归还触发权：窗口未满时唤醒队首等待者）。</summary>
        public void Release()
        {
            lock (_sync)
            {
                var now = _clock();
                Prune(now);
                Pump(now);
                RescheduleWakeLocked(now);
            }
        }

        /// <summary>窗口滚动检查点（内置 Timer 到点 / 注入调度器回调时执行）：重算窗口 → 唤醒 → 若仍排队则再排一次。</summary>
        private void OnWake()
        {
            lock (_sync)
            {
                var now = _clock();
                Prune(now);
                Pump(now);
                RescheduleWakeLocked(now);
            }
        }

        /// <summary>
        /// 让「排队」自己推进：没有新的 Acquire/Release 事件时，也必须有人把时间窗推过去。
        /// 延迟 = 窗口内最早名额的到期时刻 - now（至少 1ms）；队列清空后立刻取消，不留空转定时器。
        /// </summary>
        private void RescheduleWakeLocked(DateTimeOffset now)
        {
            if (_waiters.Count == 0 || _starts.Count < _maxPerMinute)
            {
                CancelWakeLocked();
                return;
            }

            DateTimeOffset due = _starts.Peek() + Window;
            TimeSpan delay = due - now;
            if (delay < TimeSpan.FromMilliseconds(1)) delay = TimeSpan.FromMilliseconds(1);

            if (_wakeTimer != null)
            {
                _wakeTimer.Change(delay, Timeout.InfiniteTimeSpan);
                return;
            }

            // 注入调度器（单测）：由测试控制何时触发回调，断言「不依赖外部 Release 也能醒」
            if (_pendingWake != null) { try { _pendingWake.Dispose(); } catch { } _pendingWake = null; }
            _pendingWake = _wakeScheduler(delay, OnWake);
        }

        private void CancelWakeLocked()
        {
            if (_wakeTimer != null) { _wakeTimer.Change(Timeout.Infinite, Timeout.Infinite); }
            if (_pendingWake != null) { try { _pendingWake.Dispose(); } catch { } _pendingWake = null; }
        }

        /// <summary>当前窗口内已开始的请求数（诊断/单测用）。</summary>
        public int StartedInWindow
        {
            get { lock (_sync) { Prune(_clock()); return _starts.Count; } }
        }

        private void Prune(DateTimeOffset now)
        {
            while (_starts.Count > 0 && (now - _starts.Peek()).TotalSeconds >= 60)
                _starts.Dequeue();
        }

        private void Pump(DateTimeOffset now)
        {
            while (_waiters.Count > 0 && _starts.Count < _maxPerMinute)
            {
                var w = _waiters.First.Value;
                _waiters.RemoveFirst();
                _starts.Enqueue(now);
                w.Tcs.TrySetResult(true);
            }
        }
    }

    /// <summary>
    /// 市场数据服务（M2 核心，进程单例）：
    /// ① 内存缓存层：键 = (uniqueName, city, quality)，TTL 分档 30 分钟（查价/比价）/ 4 小时（估值）；
    /// ② 批量取数层：合并请求，URL ≤ 3000 字符切批（照搬调研报告第五章②形状）；
    /// ③ 进程级唯一限流器：滚动 60 req/min，手动查询 > 后台刷新。
    /// single-flight：同一批次的并发请求去重为一个在途请求。
    /// 线程模型：async/await 从 UI 事件进来，无常驻后台线程。
    /// 只用对接标准定死的 3 个端点（prices/history/gold）；charts 端点不采纳。
    /// AODP 价格数据只进内存，绝不落盘。
    /// </summary>
    public sealed class MarketDataService
    {
        /// <summary>进程级唯一实例（UI 各入口共用，保证限流器唯一）。</summary>
        public static readonly MarketDataService Instance = new MarketDataService();

        private readonly MarketDataCache _cache = new MarketDataCache();
        private readonly MarketRateLimiter _limiter;
        private readonly Func<DateTimeOffset> _clock;
        private readonly Func<string, CancellationToken, Task<string>> _fetchUrl;
        private readonly int _timeoutSeconds;

        // single-flight：URL → 在途请求（同一批并发去重为一个）
        private readonly object _inFlightSync = new object();
        private readonly Dictionary<string, InFlightRequest> _inFlight = new Dictionary<string, InFlightRequest>();

        /// <summary>
        /// 一个在途请求 + 引用计数。**谁都不能在自己完成后就把在途项删掉**（旧实现每个 awaiter 都在
        /// 自己的 finally 里 RemoveInFlight，第一个完成的就把共享项删了，后来者只能另开一次请求，
        /// 去重形同虚设）。规则：最后一个引用者释放时才移除，且移除时校验仍是同一个项。
        /// </summary>
        private sealed class InFlightRequest
        {
            public Task<string> Task;
            public int Refs;
            /// <summary>这次在途请求自己的超时 + 创建者 ct 链接源；最后一个引用者离开时释放/取消。</summary>
            public CancellationTokenSource Cts;
        }

        /// <summary>fetchUrl / clock / limiter 可注入以便单测；默认走 MarketClient（带 429 退避）。</summary>
        public MarketDataService(
            Func<string, CancellationToken, Task<string>> fetchUrl = null,
            Func<DateTimeOffset> clock = null,
            MarketRateLimiter limiter = null,
            int timeoutSeconds = 25)
        {
            _fetchUrl = fetchUrl ?? DefaultFetch;
            _clock = clock ?? (Func<DateTimeOffset>)(() => DateTimeOffset.UtcNow);
            _limiter = limiter ?? new MarketRateLimiter(60);
            _timeoutSeconds = timeoutSeconds;
        }

        private static async Task<string> DefaultFetch(string url, CancellationToken ct)
        {
            return await MarketClient.GetWithBackoffAsync(url, 25, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 取一批物品 × 城市 × 品质的行情（默认全品质 1-5）。优先读缓存（按查询用途的 TTL 档判新鲜），
        /// 缺失的物品合并成批量请求去拉（切批 + single-flight + 限流），返回所有请求键的行
        /// （含"暂无上报数据"合成行：SellHasData=false）。
        /// </summary>
        public async Task<List<MarketQuote>> GetPricesAsync(
            IReadOnlyCollection<string> items, IReadOnlyCollection<string> cities,
            MarketQueryPurpose purpose = MarketQueryPurpose.ManualQuery,
            IReadOnlyCollection<int> qualities = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var itemList = NormalizeItems(items);
            var cityList = NormalizeCities(cities);
            var qualityList = NormalizeQualities(qualities);
            var result = new List<MarketQuote>();
            if (itemList.Count == 0 || cityList.Count == 0) return result;

            var readerTtl = MarketCacheTtl.For(purpose);
            var missingItems = FindMissingItems(itemList, cityList, qualityList, readerTtl);

            if (missingItems.Count > 0)
            {
                var batches = PriceBatcher.CreatePriceBatches(missingItems, cityList);
                var fetches = new List<Task>();
                foreach (var batch in batches)
                {
                    string url = MarketClient.BuildPriceUrl(batch.ToArray(), cityList.ToArray());
                    fetches.Add(FetchBatchIntoCacheAsync(url, batch, cityList, qualityList, purpose, ct));
                }
                await Task.WhenAll(fetches.ToArray()).ConfigureAwait(false);
            }

            // 从缓存汇总返回（读取时按当前时间重算陈旧标记）
            var now = _clock();
            foreach (var item in itemList)
            {
                foreach (var city in cityList)
                {
                    foreach (var q in qualityList)
                    {
                        var entry = _cache.Get(item, city, q);
                        if (entry == null) continue; // 拉取失败（如 429 用尽）时宁缺勿造
                        var copy = entry.Quote.Clone();
                        copy.SellStale = MarketClient.IsSellStale(copy.SellHasData, copy.SellMinDate, now);
                        result.Add(copy);
                    }
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

        public void ClearCache() { _cache.Clear(); }

        public int CachedEntryCount { get { return _cache.Count; } }

        /// <summary>
        /// 只读缓存快照：**绝不发请求**。UI 渲染（筛选联动、换城市、翻页、重绘）一律走这里，
        /// 这也是「筛选全程零 HTTP」的实现依据——渲染路径上没有 await、没有 GetPricesAsync，
        /// 物理上不可能出网。要刷新请显式调 <see cref="GetPricesAsync"/> 或 <see cref="RefreshAsync"/>。
        /// </summary>
        public List<MarketCacheEntry> SnapshotCache()
        {
            return _cache.Snapshot();
        }

        /// <summary>
        /// 只读「可展示报价」快照：**绝不发请求**。
        /// 只返回上游真有上报行（HasResponse=true）且至少一侧有价的条目——
        /// 0 价合成行（无上报）不进 UI，让 UI 直接按「该城无该行」渲染缺失态，
        /// 而不是先拿到 0 再靠显示层遮盖（数据层过滤 0 价）。缓存里的合成行仍保留用于防重复请求。
        /// </summary>
        public List<MarketCacheEntry> SnapshotPricingEntries()
        {
            var result = new List<MarketCacheEntry>();
            foreach (var entry in _cache.Snapshot())
            {
                if (entry == null || entry.Quote == null) continue;
                if (!entry.HasResponse) continue;
                if (entry.Quote.SellMin <= 0 && entry.Quote.BuyMax <= 0) continue;
                result.Add(entry);
            }
            return result;
        }

        /// <summary>后台刷新入口（估值用途：TTL 4 小时档，限流优先级最低）。</summary>
        public Task<List<MarketQuote>> RefreshAsync(IReadOnlyCollection<string> items,
            IReadOnlyCollection<string> cities, CancellationToken ct = default)
        {
            return GetPricesAsync(items, cities, MarketQueryPurpose.Valuation, null, ct);
        }

        /// <summary>找出缓存中没有任何新鲜条目的物品（AODP 一次 GET 返回整物品全品质，按物品粒度判缺）。</summary>
        private List<string> FindMissingItems(List<string> items, List<string> cities,
            List<int> qualities, TimeSpan readerTtl)
        {
            var now = _clock();
            var missing = new List<string>();
            foreach (var item in items)
            {
                bool complete = true;
                foreach (var city in cities)
                {
                    foreach (var q in qualities)
                    {
                        var e = _cache.Get(item, city, q);
                        if (e == null || !e.IsFresh(now, readerTtl)) { complete = false; break; }
                    }
                    if (!complete) break;
                }
                if (!complete) missing.Add(item);
            }
            return missing;
        }

        /// <summary>single-flight 拉一批物品并写入缓存；同一 URL 的并发调用共享一个在途请求。</summary>
        private async Task FetchBatchIntoCacheAsync(string url, List<string> batch,
            List<string> cities, List<int> qualities, MarketQueryPurpose purpose, CancellationToken ct)
        {
            // 限流优先级由在途请求的创建者决定：手动查询创建的批次按高优先级入队；
            // 后台刷新先建、手动后到的共享同一在途请求（等待 ≤ 一次请求时长，可接受）。
            //
            // 引用（lease）一直持有到**缓存写完**才释放——若在「HTTP 完成」就释放，
            // 中间那段解析 + 写缓存的窗口里来的第三个调用会看不到在途项、另发一次同 URL 请求，
            // 去重就只在毫秒级偶然生效。释放点落在写缓存之后，后来者一定命中新鲜缓存。
            var lease = AcquireInFlight(url, purpose == MarketQueryPurpose.ManualQuery, ct);
            try
            {
                string body = await AwaitLeaseAsync(lease, ct).ConfigureAwait(false);

                var now = _clock();
                var ttl = MarketCacheTtl.For(purpose);
                var parsed = MarketClient.Parse(body, now);
                var byKey = new Dictionary<string, MarketQuote>();
                foreach (var q in parsed) byKey[MarketDataCache.Key(q.ItemId, q.City, q.Quality)] = q;

                foreach (var item in batch)
                {
                    foreach (var city in cities)
                    {
                        foreach (var q in qualities)
                        {
                            MarketQuote quote;
                            if (!byKey.TryGetValue(MarketDataCache.Key(item, city, q), out quote))
                            {
                                // AODP 无该键上报行：合成"暂无上报数据"条目（短 TTL 同档），避免反复重打。
                                // HasResponse=false 表示"上游没有这一行"，与"有行但价格为 0"区分开；
                                // 展示层（SnapshotPricingEntries）据此过滤，不把 0 价当报价渲染。
                                quote = new MarketQuote
                                {
                                    ItemId = item, City = city, Quality = q,
                                    SellMin = 0, SellMinDate = "", BuyMax = 0, BuyMaxDate = "",
                                    SellHasData = false, SellStale = false,
                                    // 上游整行缺失 ≠ 字段值为 0：两者在报价卡上是「无数据」vs「无挂单」
                                    SellMinPresent = false, BuyMaxPresent = false
                                };
                            }
                            _cache.Put(new MarketCacheEntry
                            {
                                Quote = quote.Clone(), Ttl = ttl, FetchedAt = now,
                                HasResponse = byKey.ContainsKey(MarketDataCache.Key(item, city, q))
                            });
                        }
                    }
                }
            }
            finally
            {
                ReleaseInFlight(url, lease);
            }
        }

        /// <summary>一次在途请求的引用凭证：同一个 URL 的并发调用共享同一个 <see cref="InFlightRequest"/>。</summary>
        private sealed class InFlightLease
        {
            public InFlightRequest Owner;
        }

        /// <summary>
        /// 取（或创建）同一 URL 的在途请求的引用。引用计数保证「第一个完成者不会把在途项删掉」：
        /// 只有最后一个引用者释放时才移除，且移除前校验字典里仍是同一个实例（防误删后来者新建的项）。
        /// 取消语义：在途请求的令牌 = <c>CreateLinkedTokenSource(创建者 ct)</c>（P1-2 修正），
        /// 所以创建者取消会真的中断那次 HTTP；后到的 awaiter 各自取消只放弃等待，不牵连别人。
        /// </summary>
        private InFlightLease AcquireInFlight(string url, bool priority, CancellationToken ct)
        {
            lock (_inFlightSync)
            {
                InFlightRequest req;
                if (!_inFlight.TryGetValue(url, out req))
                {
                    req = new InFlightRequest { Refs = 0 };
                    // 在途请求自己的超时 + 创建者 ct 的链接（不再自建一个无视调用方 ct 的源）
                    req.Cts = CancelAfterLinked(ct, TimeSpan.FromSeconds(_timeoutSeconds));
                    req.Task = FetchWithLimiterAsync(url, priority, req.Cts.Token);
                    _inFlight[url] = req;
                }
                req.Refs++;
                return new InFlightLease { Owner = req };
            }
        }

        private static async Task<string> AwaitLeaseAsync(InFlightLease lease, CancellationToken ct)
        {
            var task = lease.Owner.Task;
            if (!ct.CanBeCanceled) return await task.ConfigureAwait(false);

            // 自己和别人共享的那次请求并存：自己取消只是不再等，不把全体 awaiter 拉下水
            var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(delegate { cancelTcs.TrySetResult(true); }))
            {
                var completed = await Task.WhenAny(task, cancelTcs.Task).ConfigureAwait(false);
                if (completed != task) ct.ThrowIfCancellationRequested();
            }
            return await task.ConfigureAwait(false);
        }

        /// <summary>CreateLinkedTokenSource + 超时（无传入 ct 时等价于纯超时）。</summary>
        internal static CancellationTokenSource CancelAfterLinked(CancellationToken ct, TimeSpan timeout)
        {
            var cts = ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : new CancellationTokenSource();
            cts.CancelAfter(timeout);
            return cts;
        }

        private void ReleaseInFlight(string url, InFlightLease lease)
        {
            var req = lease == null ? null : lease.Owner;
            if (req == null) return;
            bool last;
            lock (_inFlightSync)
            {
                req.Refs--;
                last = req.Refs <= 0;
                if (last)
                {
                    InFlightRequest current;
                    if (_inFlight.TryGetValue(url, out current) && ReferenceEquals(current, req))
                        _inFlight.Remove(url);
                }
            }
            if (!last) return; // 还有别的 awaiter 在用，保留在途项供其共享同一结果
            // 最后一个引用者走了：没人要结果了，未完成就中断那次 HTTP（不让它在后台空转到超时）
            var cts = req.Cts;
            if (cts == null) return;
            if (!req.Task.IsCompleted)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                req.Task.ContinueWith(delegate { cts.Dispose(); }, TaskScheduler.Default);
            }
            else
            {
                cts.Dispose();
            }
        }

        /// <summary>当前在途请求数（诊断 / 单测用：证明去重不是被第一个完成者删掉的假去重）。</summary>
        public int InFlightCount
        {
            get { lock (_inFlightSync) { return _inFlight.Count; } }
        }

        /// <summary>限流器管住每次实际 HTTP 请求；失败向上抛（由调用方决定停止本轮）。
        /// ct 必须接进来（旧实现自建 CancellationTokenSource 把调用方 ct 丢了，取消按钮无效）。</summary>
        private async Task<string> FetchWithLimiterAsync(string url, bool priority, CancellationToken ct)
        {
            // 先判取消再抢名额：已取消的调用不该白占一个限流名额（名额是 60rpm 的稀缺资源）
            ct.ThrowIfCancellationRequested();
            await _limiter.AcquireAsync(priority).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                return await _fetchUrl(url, ct).ConfigureAwait(false);
            }
            finally
            {
                _limiter.Release();
            }
        }

        private static List<string> NormalizeItems(IReadOnlyCollection<string> items)
        {
            var list = new List<string>();
            if (items == null) return list;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in items)
            {
                // 与缓存键同源：Trim + 大写 + 保留 @附魔（小写 id 不再永久 miss）
                string id = MarketItemKey.Normalize(raw);
                if (id.Length == 0 || !seen.Add(id)) continue;
                list.Add(id);
            }
            return list;
        }

        private static List<string> NormalizeCities(IReadOnlyCollection<string> cities)
        {
            var list = new List<string>();
            if (cities == null) return list;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in cities)
            {
                // 与缓存键同源：Trim + 别名归一（"fort sterling"/"凯城"/"黑市" → 规范名）
                string c = MarketCities.Normalize(raw);
                if (c.Length == 0 || !seen.Add(c)) continue;
                list.Add(c);
            }
            return list;
        }

        private static List<int> NormalizeQualities(IReadOnlyCollection<int> qualities)
        {
            if (qualities == null || qualities.Count == 0)
                return new List<int>(PriceBatcher.AllQualities); // 不传 = 全品质（AODP 默认行为）
            var set = new SortedSet<int>();
            foreach (var q in qualities)
                if (q >= 1 && q <= 5) set.Add(q);
            if (set.Count == 0) return new List<int>(PriceBatcher.AllQualities);
            return new List<int>(set);
        }
    }
}
