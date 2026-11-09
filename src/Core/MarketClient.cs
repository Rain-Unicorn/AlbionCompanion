using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlbionCompanion.Core
{
    /// <summary>AODP 分服基址（对接标准约定 1：禁止 www——实测 www 与 west 逐字节一致）。</summary>
    public static class AodpServers
    {
        public const string West = "https://west.albion-online-data.com/api/v2/";
        public const string East = "https://east.albion-online-data.com/api/v2/";
        public const string Europe = "https://europe.albion-online-data.com/api/v2/";
    }

    /// <summary>429 退避已用尽仍被限流时抛出；消息含 "429" 供旧调用方按关键字识别。</summary>
    public class MarketRateLimitedException : Exception
    {
        public MarketRateLimitedException(int retries, string url)
            : base("AODP 限流(429)：" + retries + " 次退避重试后仍被限流，停止本轮：" + url)
        {
        }
    }

    /// <summary>
    /// 429 退避策略（调研报告第五章③ / 市场定案 M1）：
    /// 有 RateLimit-Reset（epoch 秒）→ 睡到该时刻再重试（封禁期内继续打会重计封禁）；
    /// 无该头 → 指数退避 30/60/120s。最多重试 3 次，绝不裸重试。
    /// 纯函数，可单测。
    /// </summary>
    public static class RateLimitBackoff
    {
        public const int MaxRetries = 3;

        /// <summary>无 RateLimit-Reset 头时的指数退避表（第 1/2/3 次重试前等待）。</summary>
        public static readonly TimeSpan[] ExponentialDelays =
        {
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(120)
        };

        /// <summary>Reset 等待的上下限保护：下限 1s（时钟偏移），上限 15min（异常头不至于卡死）。</summary>
        public static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

        /// <param name="attempt">从 0 开始的第几次重试</param>
        /// <param name="rateLimitResetEpoch">429 响应的 RateLimit-Reset 头原始值（epoch 秒），null/无法解析走指数退避</param>
        public static TimeSpan ComputeDelay(int attempt, string rateLimitResetEpoch, DateTimeOffset now)
        {
            long epoch;
            if (!string.IsNullOrEmpty(rateLimitResetEpoch) && long.TryParse(rateLimitResetEpoch, out epoch))
            {
                var delay = DateTimeOffset.FromUnixTimeSeconds(epoch) - now;
                if (delay < MinDelay) delay = MinDelay;
                if (delay > MaxDelay) delay = MaxDelay;
                return delay;
            }
            int idx = attempt;
            if (idx < 0) idx = 0;
            if (idx >= ExponentialDelays.Length) idx = ExponentialDelays.Length - 1;
            return ExponentialDelays[idx];
        }
    }

    public class MarketQuote
    {
        public string ItemId;
        public string City;
        public int Quality;
        public long SellMin;
        public string SellMinDate;
        public long BuyMax;
        public string BuyMaxDate;

        // M2 定案：0 价 = 无上报数据（不是免费），Parse 层标记，不得进套利卖价；buy 侧 >0 保留给收货参考。
        public bool SellHasData; // sell_price_min > 0
        public bool SellStale;   // sell_price_min_date 距今 > 24h（陈旧无数据；读取时按当前时间重算）

        // 接口契约（设计规范 §八.7）：必须能区分「字段缺失」与「字段值为 0」。
        // AODP 正常返回时字段恒在、0 表示「该城无挂单」；字段缺失则按「无数据」渲染，不混为一谈。
        public bool SellMinPresent = true;
        public bool BuyMaxPresent = true;

        public MarketQuote Clone()
        {
            return new MarketQuote
            {
                ItemId = ItemId, City = City, Quality = Quality,
                SellMin = SellMin, SellMinDate = SellMinDate,
                BuyMax = BuyMax, BuyMaxDate = BuyMaxDate,
                SellHasData = SellHasData, SellStale = SellStale,
                SellMinPresent = SellMinPresent, BuyMaxPresent = BuyMaxPresent
            };
        }
    }

    /// <summary>
    /// AODP 行情客户端。M1 定案：
    /// ① 基址默认锁 east（用户指令"所有数据取亚服"），字段可配保留切换能力，无多服 UI；
    /// ② HttpClient 单例 + AutomaticDecompression（不设 Accept-Encoding 会被文档限到 1 req/5min）；
    /// ③ 429 退避读 RateLimit-Reset，无头则 30/60/120s 指数退避，最多 3 次。
    /// </summary>
    public static class MarketClient
    {
        /// <summary>当前 AODP 基址。默认亚服 east；改字段即可整体切服，不影响已拼好的 URL。</summary>
        public static string ServerBaseUrl = AodpServers.East;

        private const string PricesPath = "stats/prices/";

        /// <summary>进程级唯一 HttpClient：连接复用 + 自动 gzip/deflate/brotli 解压（自动带 Accept-Encoding 头）。</summary>
        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip
                                         | DecompressionMethods.Deflate
                                         | DecompressionMethods.Brotli
            };
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AlbionCompanion/1.0");
            return client;
        }

        /// <summary>
        /// 单个 URL 段（物品名 / 城市名）的编码。AODP 的 item_id 与 city 都在 URL 段里，
        /// 必须按 RFC 3986 完整转义：空格 → %20、'+' → %2B、'&amp;' → %26、'%' → %25、'\'' → %27、别名词里的 '@' → %40。
        /// 旧实现只 Replace(" ", "%20")，物品名里出现上面任一字符时拼出的 URL 会与
        /// PriceBatcher.EstimateUrlLength 的估算不一致 → 估算偏小 → 切出实际超长的批次（AODP 4096 上限）。
        /// 把编码收敛到这一个函数，BuildPriceUrl 与切批估算同源，估算 = 实际。
        /// （实测：east 端点对 T4_BAG@1 与 T4_BAG%401 都返回 200，编码与非编码等价，故统一走转义更稳。）
        /// </summary>
        public static string EncodeSegment(string raw)
        {
            if (raw == null) return "";
            string trimmed = raw.Trim();
            if (trimmed.Length == 0) return "";
            return Uri.EscapeDataString(trimmed);
        }

        public static string BuildPriceUrl(string[] items, string[] locations)
        {
            var cleanItems = new List<string>();
            if (items != null)
            {
                foreach (var raw in items)
                {
                    string encoded = EncodeSegment(raw);
                    if (encoded.Length > 0) cleanItems.Add(encoded);
                }
            }
            var cleanCities = new List<string>();
            if (locations != null)
            {
                foreach (var raw in locations)
                {
                    string encoded = EncodeSegment(raw);
                    if (encoded.Length > 0) cleanCities.Add(encoded);
                }
            }
            return ServerBaseUrl + PricesPath + string.Join(",", cleanItems)
                   + "?locations=" + string.Join(",", cleanCities);
            // 不传 qualities，默认返回全部品质 1-5（实测行为，调研报告 2.2）
        }

        public static List<MarketQuote> Parse(string json)
        {
            return Parse(json, DateTimeOffset.UtcNow);
        }

        /// <summary>sell_price_min_date 距今超过此值 → 陈旧无数据（M2 定案：24h）。</summary>
        public static readonly TimeSpan SellStaleAge = TimeSpan.FromHours(24);

        /// <summary>解析 AODP 时间戳；空串与 0001-01-01 哨兵（无数据）返回 null。</summary>
        public static DateTimeOffset? TryParsePriceDate(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            DateTimeOffset v;
            if (!DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out v))
                return null;
            if (v.UtcDateTime.Year < 2000) return null; // 0001-01-01T00:00:00 = 无上报哨兵
            return v;
        }

        /// <summary>解析并标记：sell_price_min=0 → SellHasData=false（无数据，不得进套利卖价）；
        /// sell_price_min_date 距 now > 24h → SellStale。now 可注入以便单测。</summary>
        public static List<MarketQuote> Parse(string json, DateTimeOffset now)
        {
            var result = new List<MarketQuote>();
            if (string.IsNullOrEmpty(json) || json.Trim().Length == 0) return result;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return result;

            foreach (var row in root.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;

                long sellMin = ReadLongElem(row, "sell_price_min");
                long buyMax = ReadLongElem(row, "buy_price_max");
                if (sellMin <= 0 && buyMax <= 0) continue; // 全零行：无任何上报，不入缓存不入套利

                var q = new MarketQuote();
                q.ItemId = ReadStringElem(row, "item_id");
                q.City = ReadStringElem(row, "city");
                q.Quality = (int)ReadLongElem(row, "quality");
                // 字段存在性单独记录：price=0 且字段在 → 「无挂单」；字段缺失 → 「无数据」
                q.SellMinPresent = row.TryGetProperty("sell_price_min", out _);
                q.BuyMaxPresent = row.TryGetProperty("buy_price_max", out _);
                q.SellMin = sellMin;
                q.SellMinDate = ReadStringElem(row, "sell_price_min_date");
                q.BuyMax = buyMax;
                q.BuyMaxDate = ReadStringElem(row, "buy_price_max_date");
                q.SellHasData = sellMin > 0;
                q.SellStale = IsSellStale(q.SellHasData, q.SellMinDate, now);
                result.Add(q);
            }
            return result;
        }

        /// <summary>陈旧判定（读取时可按当前时间重算，缓存中数据年龄会增长）。</summary>
        public static bool IsSellStale(bool sellHasData, string sellMinDate, DateTimeOffset now)
        {
            if (!sellHasData) return false;
            var date = TryParsePriceDate(sellMinDate);
            return date.HasValue && (now - date.Value) > SellStaleAge;
        }

        /// <summary>同步入口（旧调用方：MarketTab / AutoUpdateTab 后台线程）。</summary>
        public static List<MarketQuote> Fetch(string[] items, string[] locations, int timeoutSeconds)
        {
            return FetchAsync(items, locations, timeoutSeconds).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 异步取价：429 时按 RateLimitBackoff 退避后重试（最多 3 次），
        /// 仍 429 抛 MarketRateLimitedException（调用方停止本轮，绝不裸重试）。
        /// </summary>
        public static async Task<List<MarketQuote>> FetchAsync(string[] items, string[] locations,
                                                                int timeoutSeconds, CancellationToken ct = default)
        {
            string url = BuildPriceUrl(items, locations);
            string body = await GetWithBackoffAsync(url, timeoutSeconds, ct);
            return Parse(body);
        }

        /// <summary>带 429 退避的 GET（批量取数层与金价/历史共用）。</summary>
        public static async Task<string> GetWithBackoffAsync(string url, int timeoutSeconds, CancellationToken ct = default)
        {
            for (int attempt = 0; ; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using var resp = await Http.GetAsync(url, cts.Token).ConfigureAwait(false);

                if ((int)resp.StatusCode == 429)
                {
                    if (attempt >= RateLimitBackoff.MaxRetries)
                        throw new MarketRateLimitedException(RateLimitBackoff.MaxRetries, url);

                    string reset = null;
                    if (resp.Headers != null &&
                        resp.Headers.TryGetValues("RateLimit-Reset", out var values))
                    {
                        foreach (var v in values) { reset = v; break; }
                    }
                    var delay = RateLimitBackoff.ComputeDelay(attempt, reset, DateTimeOffset.UtcNow);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    throw new HttpRequestException("AODP HTTP " + (int)resp.StatusCode + "：" + url +
                                                   ErrorContext(content));
                }
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 非 2xx 响应体的上下文片段。旧写法 <c>content.Length &gt; 200 ? "" : " " + content</c> 把判断写反了：
        /// 响应体一长反而整段丢掉，而 AODP 的 429 说明（"Throttled, be nice to the server… Throttle resets at …"）
        /// 正是排查限流唯一的信息来源。这里改为：有内容就带上，超过 200 字符截断并标出截断，永不静默丢弃。
        /// </summary>
        public static string ErrorContext(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            const int keep = 200;
            if (content.Length <= keep) return " " + content;
            return " " + content.Substring(0, keep) + "…(响应体共 " + content.Length + " 字符，已截断)";
        }

        private static long ReadLongElem(JsonElement elem, string key)
        {
            if (!elem.TryGetProperty(key, out var v)) return 0;
            try
            {
                if (v.ValueKind == JsonValueKind.Number)
                {
                    if (v.TryGetInt64(out long l)) return l;
                    if (v.TryGetDouble(out double d)) return (long)d;
                    if (v.TryGetInt32(out int i)) return i;
                }
                if (v.ValueKind == JsonValueKind.String)
                {
                    long.TryParse(v.GetString(), out long p);
                    return p;
                }
            }
            catch { }
            return 0L;
        }

        private static string ReadStringElem(JsonElement elem, string key)
        {
            if (!elem.TryGetProperty(key, out var v)) return "";
            return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        }
    }
}
