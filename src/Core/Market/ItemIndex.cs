#nullable disable // 与仓库既有 Core 文件风格一致
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AlbionCompanion.Core
{
    public class ItemIndexEntry
    {
        public string UniqueName; // 原始条目名（可能带 @附魔后缀）
        public string BaseId;     // 去掉 @ 后缀
        public string NameZh;     // LocalizedNames["ZH-CN"]
        public string NameEn;     // LocalizedNames["EN-US"]（ZH 缺失时兜底显示）
    }

    /// <summary>
    /// 物品元数据本地索引（M3 定案）：ao-bin-dumps 的 formatted/items.json 是唯一物品元数据来源，
    /// 替换 ItemCatalog 对 albion-online-data.com/items 的 HTML 抓取（未声明接口随时会断）。
    /// 分类树/中文名/uniqueName/tier/附魔全部来自本地数据 → 三级关联筛选离线可用。
    /// 磁盘上只落 items.json（静态索引）+ items-meta.json（版本侧车）；AODP 价格不在此列、绝不落盘。
    /// </summary>
    public class ItemIndex
    {
        /// <summary>进程级默认索引：Program.cs 启动时 LoadDefault()；为 null = 数据缺失（UI 必须显式告警，不许静默）。</summary>
        public static ItemIndex Default;
        /// <summary>当前索引来自哪个文件（诊断/告警用）。</summary>
        public static string LoadedFromPath;
        /// <summary>数据版本号（meta 侧车的 commit sha 前 8 位；无 meta 时用文件修改时间）。</summary>
        public static string DataVersion;

        private readonly Dictionary<string, ItemIndexEntry> _byBase =
            new Dictionary<string, ItemIndexEntry>(StringComparer.Ordinal);

        public int Count { get { return _byBase.Count; } }

        /// <summary>解析 items.json（Utf8 流式，24MB 实测 ~12k 条目、6.3k baseId）。
        /// 缺文件 / 损坏 JSON / 非数组根：返回空索引（Count==0），不抛异常；
        /// 「缺失告警不静默」的 null 语义由 LoadDefault 负责。</summary>
        public static ItemIndex Load(string path)
        {
            var index = new ItemIndex();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return index;

            try
            {
                using (var stream = File.OpenRead(path))
                using (var doc = JsonDocument.Parse(stream))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return index;
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        if (!item.TryGetProperty("UniqueName", out var un)) continue;
                        string uniqueName = un.ValueKind == JsonValueKind.String ? un.GetString() : null;
                        if (string.IsNullOrEmpty(uniqueName)) continue;

                        var entry = new ItemIndexEntry
                        {
                            UniqueName = uniqueName,
                            BaseId = uniqueName.Split('@')[0],
                            NameZh = ReadLocalized(item, "ZH-CN"),
                            NameEn = ReadLocalized(item, "EN-US")
                        };
                        // 同一 baseId 只留一条（附魔变体中文名一致，实测 0 冲突；首个胜出）
                        if (!index._byBase.ContainsKey(entry.BaseId)) index._byBase[entry.BaseId] = entry;
                    }
                }
            }
            catch (Exception)
            {
                // 解析失败视为数据缺失：返回空索引，由 LoadDefault/UI 告警链路兜底
                return new ItemIndex();
            }
            return index;
        }

        private static string ReadLocalized(JsonElement item, string lang)
        {
            if (item.TryGetProperty("LocalizedNames", out var names) &&
                names.ValueKind == JsonValueKind.Object &&
                names.TryGetProperty(lang, out var v) &&
                v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
            return null;
        }

        /// <summary>exe 同目录 data/items.json 与 AppData items.json 取较新者；都没有返回 null（不静默）。</summary>
        public static ItemIndex LoadDefault()
        {
            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "items.json");
            string appDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlbionCompanion");
            string appPath = Path.Combine(appDir, "items.json");

            string chosen = null;
            if (File.Exists(exePath) && File.Exists(appPath))
                chosen = File.GetLastWriteTimeUtc(exePath) >= File.GetLastWriteTimeUtc(appPath) ? exePath : appPath;
            else if (File.Exists(exePath)) chosen = exePath;
            else if (File.Exists(appPath)) chosen = appPath;

            if (chosen == null)
            {
                Default = null;
                LoadedFromPath = null;
                DataVersion = null;
                return null; // 调用方（UI 状态栏）必须显式告警，绝不静默
            }

            var index = Load(chosen);
            if (index.Count == 0)
            {
                Default = null;
                LoadedFromPath = chosen;
                DataVersion = null;
                return null; // 文件在但解析为空 = 数据损坏，同样按缺失告警
            }
            Default = index;
            LoadedFromPath = chosen;
            DataVersion = ItemIndexMeta.LoadVersion(chosen) ??
                           File.GetLastWriteTimeUtc(chosen).ToString("yyyy-MM-dd");
            return index;
        }

        public bool TryGetEntry(string uniqueName, out ItemIndexEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(uniqueName)) return false;
            return _byBase.TryGetValue(uniqueName.Split('@')[0], out entry);
        }

        /// <summary>中文名（ZH-CN 缺失时 EN-US 兜底；都没有返回 null）。</summary>
        public string DisplayNameOf(string uniqueName)
        {
            ItemIndexEntry e;
            return TryGetEntry(uniqueName, out e) ? (e.NameZh ?? e.NameEn) : null;
        }

        /// <summary>离线搜索：中文名或 uniqueName 子串匹配（大小写不敏感），返回 baseId 列表。</summary>
        public List<string> SearchBaseIds(string query, int limit)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(query) || limit <= 0) return result;
            string needle = query.Trim();
            if (needle.Length == 0) return result;

            bool idLike = ItemCatalog.LooksLikeItemId(needle);
            foreach (var kv in _byBase)
            {
                bool hit;
                if (idLike)
                    hit = kv.Key.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                else
                {
                    string name = kv.Value.NameZh ?? kv.Value.NameEn;
                    hit = name != null &&
                          name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                if (hit)
                {
                    result.Add(kv.Key);
                    if (result.Count >= limit) break;
                }
            }
            return result;
        }

        /// <summary>可交易物品（T 前缀）baseId 全集——替代旧版对 /items 页逐关键词 HTML 扫描。</summary>
        public List<string> TieredBaseIds()
        {
            var result = new List<string>();
            foreach (var kv in _byBase)
                if (kv.Key.StartsWith("T", StringComparison.Ordinal))
                    result.Add(kv.Key);
            result.Sort(StringComparer.Ordinal);
            return result;
        }
    }

    /// <summary>items-meta.json 侧车：记录数据版本（ao-bin-dumps commit sha）与下载时间。</summary>
    public static class ItemIndexMeta
    {
        public const string FileName = "items-meta.json";

        public static string MetaPathFor(string itemsPath)
        {
            return Path.Combine(Path.GetDirectoryName(itemsPath ?? "") ?? "", FileName);
        }

        public static void Save(string itemsPath, string version, string commitDate, int count)
        {
            try
            {
                var obj = new Dictionary<string, object>
                {
                    { "version", version ?? "" },
                    { "commitDate", commitDate ?? "" },
                    { "count", count },
                    { "downloadedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }
                };
                File.WriteAllText(MetaPathFor(itemsPath),
                    JsonSerializer.Serialize(obj), System.Text.Encoding.UTF8);
            }
            catch { }
        }

        public static string LoadVersion(string itemsPath)
        {
            try
            {
                string p = MetaPathFor(itemsPath);
                if (!File.Exists(p)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.TryGetProperty("version", out var v) &&
                    v.ValueKind == JsonValueKind.String)
                {
                    string s = v.GetString();
                    return string.IsNullOrEmpty(s) ? null : s;
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// 物品数据更新器：走 GitHub REST API（版本号）+ raw.githubusercontent 短连接下载
    /// （调研报告附录教训：git clone / tarball 单流会被掐断，逐文件短连接可靠）。
    /// </summary>
    public static class ItemIndexUpdater
    {
        public const string CommitApiUrl =
            "https://api.github.com/repos/ao-data/ao-bin-dumps/commits?path=formatted/items.json&per_page=1";
        public const string RawFileUrl =
            "https://raw.githubusercontent.com/ao-data/ao-bin-dumps/master/formatted/items.json";

        /// <summary>最新版本号（commit sha 前 8 位 + 提交日期）；取不到返回 null。</summary>
        public static string FetchLatestVersion(int timeoutSeconds = 20)
        {
            using (var client = CreateClient(timeoutSeconds))
            using (var resp = client.GetAsync(CommitApiUrl).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                    return null;
                var commit = doc.RootElement[0];
                string sha = commit.TryGetProperty("sha", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() : null;
                string date = null;
                if (commit.TryGetProperty("commit", out var c) &&
                    c.TryGetProperty("committer", out var cm) &&
                    cm.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String)
                    date = d.GetString();
                if (string.IsNullOrEmpty(sha)) return null;
                return (sha.Length > 8 ? sha.Substring(0, 8) : sha) + (date != null ? "（" + date + "）" : "");
            }
        }

        /// <summary>下载最新 items.json 到 AppData 并写版本侧车；成功后由调用方 ItemIndex.LoadDefault() 重载。</summary>
        public static bool UpdateToAppData(int timeoutSeconds = 300)
        {
            // 先取版本号（GitHub API 限流失败不阻塞下载，版本回退到文件时间）
            string version = null, commitDate = null;
            try
            {
                var latest = FetchLatestVersion(20);
                if (latest != null)
                {
                    int cut = latest.IndexOf('（');
                    version = cut > 0 ? latest.Substring(0, cut) : latest;
                    if (cut > 0 && latest.EndsWith("）"))
                        commitDate = latest.Substring(cut + 1, latest.Length - cut - 2);
                }
            }
            catch { }

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlbionCompanion");
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, "items.json.tmp");

            using (var client = CreateClient(timeoutSeconds))
            using (var resp = client.GetAsync(RawFileUrl).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException("下载 items.json 失败：HTTP " + (int)resp.StatusCode);
                using (var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var dst = File.Create(tmp))
                {
                    src.CopyTo(dst);
                }
            }

            string final = Path.Combine(dir, "items.json");
            if (File.Exists(final)) File.Delete(final);
            File.Move(tmp, final);

            var index = ItemIndex.Load(final);
            if (index.Count == 0)
                throw new InvalidDataException("items.json 解析为空（下载损坏？），已保留文件供排查");
            ItemIndexMeta.Save(final, version, commitDate, index.Count);
            return true;
        }

        private static HttpClient CreateClient(int timeoutSeconds)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                         | System.Net.DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AlbionCompanion/1.0");
            return client;
        }
    }
}
