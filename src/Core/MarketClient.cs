using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlbionCompanion.Core
{
    public class MarketQuote
    {
        public string ItemId;
        public string City;
        public int Quality;
        public long SellMin;
        public string SellMinDate;
        public long BuyMax;
        public string BuyMaxDate;
    }

    public static class MarketClient
    {
        private const string BaseUrl = "https://www.albion-online-data.com/api/v2/stats/prices/";

        static MarketClient()
        {
            // .NET Framework 默认协商 TLS 1.0，该 API 要求 TLS 1.2，否则报"未能创建 SSL/TLS 安全通道"
            try
            {
                ServicePointManager.SecurityProtocol =
                    (SecurityProtocolType)3072 /* Tls12 */ |
                    (SecurityProtocolType)768 /* Tls11  */ |
                    SecurityProtocolType.Tls;
            }
            catch { }
        }


        public static string BuildPriceUrl(string[] items, string[] locations)
        {
            var cleanItems = new List<string>();
            if (items != null)
            {
                foreach (var raw in items)
                {
                    if (raw == null) continue;
                    string trimmed = raw.Trim().Replace(" ", "%20");
                    if (trimmed.Length > 0) cleanItems.Add(trimmed);
                }
            }
            var cleanCities = new List<string>();
            if (locations != null)
            {
                foreach (var raw in locations)
                {
                    if (raw == null) continue;
                    string trimmed = raw.Trim().Replace(" ", "%20");
                    if (trimmed.Length > 0) cleanCities.Add(trimmed);
                }
            }
            return BaseUrl + string.Join(",", cleanItems)
                   + "?locations=" + string.Join(",", cleanCities);
            // 不传 qualities，默认返回全部品质 1-5
        }

        public static List<MarketQuote> Parse(string json)
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
                if (sellMin <= 0 && buyMax <= 0) continue;

                var q = new MarketQuote();
                q.ItemId = ReadStringElem(row, "item_id");
                q.City = ReadStringElem(row, "city");
                q.Quality = (int)ReadLongElem(row, "quality");
                q.SellMin = sellMin;
                q.SellMinDate = ReadStringElem(row, "sell_price_min_date");
                q.BuyMax = buyMax;
                q.BuyMaxDate = ReadStringElem(row, "buy_price_max_date");
                result.Add(q);
            }
            return result;
        }

        public static List<MarketQuote> Fetch(string[] items, string[] locations, int timeoutSeconds)
        {
            string url = BuildPriceUrl(items, locations);
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = timeoutSeconds * 1000;
            request.ReadWriteTimeout = timeoutSeconds * 1000;
            request.UserAgent = "AlbionCompanion/1.0";
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                return Parse(reader.ReadToEnd());
            }
        }

        /// <summary>从 wanlink.tech 单物品查询（含品质 1-5、黑市、流动性）。</summary>
        public static List<MarketQuote> FetchWanlink(string itemId, int timeoutSeconds)
        {
            var result = new List<MarketQuote>();
            if (string.IsNullOrEmpty(itemId)) return result;
            string url = "https://wanlink.tech/albion-api/prices?item_id=" + itemId +
                         "&server=east&quality=0&refresh=false";
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = timeoutSeconds * 1000;
                request.ReadWriteTimeout = timeoutSeconds * 1000;
                request.UserAgent = "AlbionCompanion/1.0";
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                {
                    using var doc = JsonDocument.Parse(reader.ReadToEnd());
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return result;
                    if (!root.TryGetProperty("prices", out var pricesElem)) return result;
                    if (pricesElem.ValueKind != JsonValueKind.Array) return result;
                    foreach (var row in pricesElem.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Object) continue;
                        long sellMin = ReadLongElem(row, "sell_price_min");
                        long buyMax = ReadLongElem(row, "buy_price_max");
                        if (sellMin <= 0 && buyMax <= 0) continue;
                        var q = new MarketQuote();
                        q.ItemId = itemId;
                        q.City = ReadStringElem(row, "city");
                        q.Quality = (int)ReadLongElem(row, "quality");
                        q.SellMin = sellMin;
                        q.BuyMax = buyMax;
                        if (row.TryGetProperty("sell_price_min_hours_ago", out var haElem) && haElem.ValueKind == JsonValueKind.Number)
                            q.SellMinDate = haElem.GetDouble() + "h ago";
                        if (row.TryGetProperty("buy_price_max_hours_ago", out var haElem2) && haElem2.ValueKind == JsonValueKind.Number)
                            q.BuyMaxDate = haElem2.GetDouble() + "h ago";
                        result.Add(q);
                    }
                }
            }
            catch { }
            return result;
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
