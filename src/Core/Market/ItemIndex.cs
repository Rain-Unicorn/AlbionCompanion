#nullable disable // 与仓库既有 Core 文件风格一致
using System;
using System.Collections.Generic;
using System.Globalization;
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

        /// <summary>
        /// 物品重量，单位 kg，来源 ao-bin-dumps 根目录 items.json 的节点属性 @weight
        /// （字符串，如 T4_BAG="3.4"）。formatted/items.json 完全没有重量字段，故必须双文件 join。
        /// 可空：缺失就是 null，**绝不允许当 0**——0kg 会在「每公斤净利」排序里排到最前，制造假最优。
        /// </summary>
        public double? WeightKg;

        /// <summary>schema 里的字段名（对跑商模块暴露的读取名）：等价于 WeightKg，单位 kg。</summary>
        public double? Weight { get { return WeightKg; } }

        public bool HasWeight { get { return WeightKg.HasValue; } }
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

        /// <summary>已成功拿到 weight（>0 kg）的条目数（诊断/覆盖率实测用）。</summary>
        public int WeightedCount { get; private set; }

        /// <summary>有 weight 来源行但 weight 缺失/不可解析的条目数。缺失即 null，绝不含糊当 0。</summary>
        public int MissingWeightCount { get; private set; }

        /// <summary>两份源文件都在、weight join 确实跑过（false = 只有中文名，weight 全 null）。</summary>
        public bool WeightJoined { get; private set; }

        /// <summary>weight 覆盖率（0~1），以本索引的全部条目为分母。</summary>
        public double WeightCoverageRatio
        {
            get { return Count == 0 ? 0d : (double)WeightedCount / Count; }
        }

        /// <summary>formatted 版文件名（UniqueName + 本地化名，约 24MB）。</summary>
        public const string FormattedFileName = "items.json";

        /// <summary>
        /// 原始版文件名（XML→JSON，约 17.5MB，节点带 @weight）。故意与 formatted 版分开命名，
        /// 不能同名覆盖。根目录 items.json 是唯一 weight 来源（formatted 版实测 0 条含 weight）。
        /// </summary>
        public const string RawFileName = "items-raw.json";

        /// <summary>解析 items.json（Utf8 流式，24MB 实测 ~12k 条目、6.3k baseId）。
        /// 缺文件 / 损坏 JSON / 非数组根：返回空索引（Count==0），不抛异常；
        /// 「缺失告警不静默」的 null 语义由 LoadDefault 负责。</summary>
        public static ItemIndex Load(string path)
        {
            return Load(path, null);
        }

        /// <summary>
        /// 双文件加载：formatted 版提供 UniqueName + 本地化名，原始版（root items.json）提供 @weight。
        /// 两份按 baseId join（formatted 的 UniqueName 可能带 @附魔后缀，原始版 @uniquename 是基名）。
        /// rawPath 缺失/损坏时索引照常可用，只是 weight 全 null 且 WeightJoined=false——
        /// 由 UI 显式告警，绝不静默，也绝不把缺失 weight 当 0。
        /// </summary>
        public static ItemIndex Load(string path, string rawPath)
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

            index.JoinWeights(rawPath);
            return index;
        }

        /// <summary>
        /// 从原始版 items.json 读 weight 并按 baseId join 进本索引。
        /// 结构：根对象 {"?xml":..., "items":{类目:[{... "@uniquename","@weight"...}]}}。
        /// 原始版键名是小写 u 的 @uniquename（formatted 版是 PascalCase UniqueName），
        /// weight 是字符串（单位 kg），转 double 写入；转不出来或缺失一律留 null。
        /// </summary>
        private void JoinWeights(string rawPath)
        {
            if (_byBase.Count == 0) return;
            if (string.IsNullOrEmpty(rawPath) || !File.Exists(rawPath)) return;

            try
            {
                using (var stream = File.OpenRead(rawPath))
                using (var doc = JsonDocument.Parse(stream))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
                    if (!doc.RootElement.TryGetProperty("items", out var items) ||
                        items.ValueKind != JsonValueKind.Object) return;

                    WeightJoined = true;
                    foreach (var category in items.EnumerateObject())
                    {
                        var nodes = category.Value;
                        if (nodes.ValueKind == JsonValueKind.Object)
                        {
                            ApplyWeightNode(nodes);
                        }
                        else if (nodes.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var node in nodes.EnumerateArray())
                                if (node.ValueKind == JsonValueKind.Object) ApplyWeightNode(node);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 原始版损坏：weight 全部留 null，WeightJoined=false（调用方显式告警）
                WeightJoined = false;
                WeightedCount = 0;
                MissingWeightCount = 0;
            }

            // 覆盖率计数：所有条目（含无 weight 来源的）作分母，如实反映"这个索引里多少条有重量"
            WeightedCount = 0;
            MissingWeightCount = 0;
            foreach (var kv in _byBase)
            {
                if (kv.Value.WeightKg.HasValue && kv.Value.WeightKg.Value > 0)
                    WeightedCount++;
                else
                    MissingWeightCount++;
            }
        }

        /// <summary>单个原始节点的 weight 落到对应 baseId 条目上；无法解析/≤0 一律不写（保持 null）。</summary>
        private void ApplyWeightNode(JsonElement node)
        {
            if (!node.TryGetProperty("@uniquename", out var un) || un.ValueKind != JsonValueKind.String)
                return;
            string uniqueName = un.GetString();
            if (string.IsNullOrEmpty(uniqueName)) return;

            string baseId = uniqueName.Split('@')[0];
            ItemIndexEntry entry;
            if (!_byBase.TryGetValue(baseId, out entry)) return;
            if (entry.WeightKg.HasValue) return; // 已拿到（同一 baseId 多条时首个胜出）

            double? kg = ParseWeightKg(node);
            if (kg.HasValue) entry.WeightKg = kg; // 缺失/异常保持 null，绝不当 0
        }

        /// <summary>解析 @weight（字符串，单位 kg）：只接受有限且 >0 的值，其余返回 null。</summary>
        private static double? ParseWeightKg(JsonElement node)
        {
            if (!node.TryGetProperty("@weight", out var w)) return null;
            double value;
            if (w.ValueKind == JsonValueKind.Number)
            {
                if (!w.TryGetDouble(out value)) return null;
            }
            else if (w.ValueKind == JsonValueKind.String)
            {
                string s = w.GetString();
                if (string.IsNullOrEmpty(s)) return null;
                if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    return null;
            }
            else
            {
                return null;
            }
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d) return null;
            return value;
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

        /// <summary>exe 同目录 data/items.json 与 AppData items.json 取较新者；都没有返回 null（不静默）。
        /// 同时加载同名目录下的原始版 items-raw.json（weight 来源；缺失则 weight 全 null 并告警）。</summary>
        public static ItemIndex LoadDefault()
        {
            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", FormattedFileName);
            string appDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlbionCompanion");
            string appPath = Path.Combine(appDir, FormattedFileName);

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

            // 原始版与 formatted 版同目录（构建脚本两份一起落）
            string rawPath = Path.Combine(Path.GetDirectoryName(chosen) ?? "", RawFileName);
            var index = Load(chosen, rawPath);
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

        /// <summary>
        /// 物品重量（kg）。缺失返回 null——**绝不返回 0**：跑商算「每公斤净利」时 0kg 会排到最前，
        /// 制造假最优。调用方拿到 null 必须显式处理（跳过该物品 / 标"重量未知"），不能当 0 用。
        /// </summary>
        public double? WeightKgOf(string uniqueName)
        {
            ItemIndexEntry e;
            if (!TryGetEntry(uniqueName, out e)) return null;
            return e.WeightKg;
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

        /// <summary>formatted 版（UniqueName + 本地化名），约 24MB。</summary>
        public const string FormattedFileUrl =
            "https://raw.githubusercontent.com/ao-data/ao-bin-dumps/master/formatted/items.json";

        /// <summary>根目录原始版（XML→JSON，节点带 @weight），约 17.5MB。weight 的唯一来源。</summary>
        public const string RawFileUrl =
            "https://raw.githubusercontent.com/ao-data/ao-bin-dumps/master/items.json";

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

        /// <summary>下载最新 items.json（formatted 版 + 根目录原始版）到 AppData 并写版本侧车；
        /// 成功后由调用方 ItemIndex.LoadDefault() 重载。两份都走 raw 短连接逐文件下载
        /// （沙箱教训：git clone / tarball 单流会被掐断），绝不整库 clone。</summary>
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

            string final = Path.Combine(dir, ItemIndex.FormattedFileName);
            string rawFinal = Path.Combine(dir, ItemIndex.RawFileName);

            using (var client = CreateClient(timeoutSeconds))
            {
                // 先下 weight 来源（原始版），再下 formatted 版：任一份失败都保留已有旧文件、不半替换
                DownloadTo(client, RawFileUrl, rawFinal);
                DownloadTo(client, FormattedFileUrl, final);
            }

            var index = ItemIndex.Load(final, rawFinal);
            if (index.Count == 0)
                throw new InvalidDataException("items.json 解析为空（下载损坏？），已保留文件供排查");
            if (!index.WeightJoined)
                throw new InvalidDataException(
                    "items-raw.json 缺失或无法解析：weight 全部为 null。已保留 formatted 版，请重试更新。");
            ItemIndexMeta.Save(final, version, commitDate, index.Count);
            return true;
        }

        /// <summary>下载单个文件到目标路径：先写 .tmp 再替换，下载失败不破坏已有文件。
        /// 每份文件一条独立短连接（不共用长连接）。</summary>
        private static void DownloadTo(HttpClient client, string url, string finalPath)
        {
            string tmp = finalPath + ".tmp";
            using (var resp = client.GetAsync(url).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException("下载 " + url + " 失败：HTTP " + (int)resp.StatusCode);
                using (var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var dst = File.Create(tmp))
                {
                    src.CopyTo(dst);
                }
            }
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tmp, finalPath);
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
