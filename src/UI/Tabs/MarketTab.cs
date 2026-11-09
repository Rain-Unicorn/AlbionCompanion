using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>市场行情：本地行情库 + 5 分钟自动回源刷新。</summary>
    public class MarketTab : TabBase
    {
        private readonly TextBox _items;
        private readonly Label _status;
        private readonly SimpleTable _table;
        private readonly Button _query;
        private readonly List<CheckBox> _cities = new List<CheckBox>();
        private readonly System.Windows.Forms.Timer _iconTimer;
        private readonly System.Windows.Forms.Timer _autoTimer;

        private readonly Button _catBtn;
        private readonly ComboBox _fTier;
        private readonly ComboBox _fEnchant;
        private readonly ComboBox _fQuality;

        private string _selL1 = "全部";
        private string _selL2 = "全部";
        private string _selL3 = "全部";

        private readonly float[] _weights = new float[] { 1.5f, 1.2f, 0.6f, 0.8f, 0.8f, 1.2f };
        private readonly bool[] _right = new bool[] { false, false, false, true, true, true };

        private MarketDb _db;
        private string _dbPath;
        private NameCache _cache;
        private string _cachePath;
        private readonly LinkLabel _dataVersion;
        private bool _updatingData;

        private List<string> _shownIds = new List<string>();
        private DateTime _lastRefresh;
        private bool _refreshing;
        private int _iconTicks;
        /// <summary>本轮回源的取消源：窗体关闭 / 用户重新查询时中断在途请求（MarketDataService 已接 ct）。</summary>
        private CancellationTokenSource _cts = new CancellationTokenSource();

        private static readonly string[] CityNames =
            { "Caerleon", "Bridgewatch", "Martlock", "Thetford", "Fort Sterling", "Lymhurst" };

        public MarketTab(Runtime runtime)
            : base(runtime)
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                         + @"\AlbionCompanion";
            _cachePath = System.IO.Path.Combine(dir, "itemnames.json");
            _cache = NameCache.Load(_cachePath);
            _db = Rt.Db;
            _dbPath = Rt.DbPath;

            Card(0, 0, 580, 104);
            CardText("物品 ID 或中文名（逗号分隔；中文支持模糊搜索；本地库每 5 分钟自动刷新）",
                     14, 10, 466, 18, Th.Sub, 8.5f, false);
            _items = Ui.Input(14, 32, 430, 28);
            Controls.Add(_items);

            _query = Ui.Primary("查询行情", 456, 32, 110, 30);
            _query.Click += delegate { StartQuery(); };
            Controls.Add(_query);

            _status = CardText("缓存 " + MarketDataService.Instance.CachedEntryCount
                               + " 条 · 点击查询后回源，之后每 5 分钟自动刷新",
                               14, 70, 540, 20, Th.Sub, 8f, false);

            // M3：物品数据版本号 + 手动更新入口（ao-bin-dumps items.json 本地索引）
            _dataVersion = new LinkLabel();
            _dataVersion.Bounds = new Rectangle(420, 10, 146, 18);
            _dataVersion.Font = Th.F(8f);
            _dataVersion.BackColor = System.Drawing.Color.Transparent;
            _dataVersion.ForeColor = Th.Sub;
            _dataVersion.TextAlign = ContentAlignment.MiddleRight;
            _dataVersion.LinkBehavior = LinkBehavior.HoverUnderline;
            _dataVersion.Click += delegate { OnUpdateItemData(); };
            Controls.Add(_dataVersion);
            UpdateDataVersionLabel();

            Card(0, 112, 580, 86);
            CardText("城市", 14, 118, 200, 18, Th.Text, 9.5f, true);
            for (int row = 0; row < 2; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    int index = row * 3 + col;
                    if (index >= CityNames.Length) break;
                    var check = Ui.Check(CityNames[index], 14 + col * 156, 140 + row * 24, 150, true);
                    Controls.Add(check);
                    _cities.Add(check);
                }
            }

            // 级联菜单：点类别按钮，悬停二级/三级
            Controls.Add(Ui.Caption("类别", 0, 206));
            _catBtn = Ui.Primary("类别：全部", 0, 224, 200, 26);
            _catBtn.Font = Th.F(9f);
            _catBtn.Click += delegate { OpenCategoryMenu(); };
            Controls.Add(_catBtn);

            Controls.Add(Ui.Caption("等阶", 210, 206));
            _fTier = Ui.Dropdown(new[] { "全部", "T1", "T2", "T3", "T4", "T5", "T6", "T7", "T8" },
                                 210, 224, 90, 26);
            Controls.Add(_fTier);

            Controls.Add(Ui.Caption("附魔", 310, 206));
            _fEnchant = Ui.Dropdown(new[] { "全部", "+0", "+1", "+2", "+3", "+4" },
                                    310, 224, 90, 26);
            Controls.Add(_fEnchant);

            Controls.Add(Ui.Caption("品质", 410, 206));
            _fQuality = Ui.Dropdown(new[] { "全部", "普通", "良好", "杰出", "精良", "史诗", "传说" },
                                    410, 224, 86, 26);
            Controls.Add(_fQuality);

            EventHandler onFilter = delegate { RenderRows(); };
            _fTier.SelectedIndexChanged += onFilter;
            _fEnchant.SelectedIndexChanged += onFilter;
            _fQuality.SelectedIndexChanged += onFilter;

            Heads(new string[] { "物品", "城市", "品质", "最低卖价", "最高买价", "更新" },
                  _weights, _right, 0, 258, 580, 36);
            _table = Table(_weights, _right, 0, 282, 580, 450);
            _table.EmptyText = "输入物品后，本地库秒开；后台每 5 分钟自动回源";

            _iconTimer = new System.Windows.Forms.Timer();
            _iconTimer.Interval = 700;
            _iconTimer.Tick += delegate { RefreshRowIcons(); };

            _autoTimer = new System.Windows.Forms.Timer();
            _autoTimer.Interval = 5 * 60 * 1000;
            _autoTimer.Tick += delegate { StartRefresh(true); };
            _autoTimer.Start();

            _items.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) StartQuery();
            };
        }

        private void SetStatus(string text, Color color)
        {
            _status.Text = text;
            _status.ForeColor = color;
        }

        /// <summary>M3：物品数据版本显示（缺失 = 显式告警，绝不静默——HANDOFF 已知问题 6）。
        /// M3 修正：weight 未 join（缺 items-raw.json）也属数据不完整，单独告警——跑商算每公斤净利要靠它。</summary>
        private void UpdateDataVersionLabel()
        {
            ItemIndex index = ItemIndex.Default;
            if (index == null) index = ItemIndex.LoadDefault();
            if (index == null)
            {
                _dataVersion.Text = "物品数据未加载 · 点击下载";
                _dataVersion.ForeColor = Th.Bad;
                _dataVersion.LinkColor = Th.Bad;
            }
            else if (!index.WeightJoined)
            {
                _dataVersion.Text = "物品数据 v" + (ItemIndex.DataVersion ?? "?") + " · 缺重量数据 · 点击补齐";
                _dataVersion.ForeColor = Th.Bad;
                _dataVersion.LinkColor = Th.Bad;
            }
            else
            {
                _dataVersion.Text = "物品数据 v" + (ItemIndex.DataVersion ?? "?") + " · 更新";
                _dataVersion.ForeColor = Th.Sub;
                _dataVersion.LinkColor = Th.Info;
            }
        }

        /// <summary>M3：手动更新入口——GitHub REST API 版本号 + raw 短连接下载（不用 git clone/tarball）。</summary>
        private void OnUpdateItemData()
        {
            if (_updatingData) return;
            _updatingData = true;
            SetStatus("正在下载物品数据（约 24MB，来自 ao-bin-dumps）…", Th.Info);
            System.Threading.Tasks.Task.Run(delegate
            {
                string err = null;
                try { ItemIndexUpdater.UpdateToAppData(); }
                catch (Exception ex) { err = ex.Message; }
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new MethodInvoker(delegate
                {
                    _updatingData = false;
                    if (err == null && ItemIndex.LoadDefault() != null)
                    {
                        UpdateDataVersionLabel();
                        SetStatus("物品数据已更新：" + ItemIndex.Default.Count + " 条 · 重量 "
                                  + ItemIndex.Default.WeightedCount + "/" + ItemIndex.Default.Count
                                  + "（" + (ItemIndex.Default.WeightCoverageRatio * 100).ToString("F1") + "%）"
                                  + " · 版本 v" + ItemIndex.DataVersion, Th.Good);
                    }
                    else
                    {
                        UpdateDataVersionLabel();
                        SetStatus("物品数据更新失败：" + (err ?? "解析为空"), Th.Bad);
                    }
                }));
            });
        }

        private List<string> CheckedCities()
        {
            var cities = new List<string>();
            for (int i = 0; i < _cities.Count; i++)
                if (_cities[i].Checked) cities.Add(_cities[i].Text);
            return cities;
        }

        private static string QualityLabel(int quality)
        {
            if (quality <= 0) return "普通";
            string[] names = { "", "良好", "杰出", "精良", "史诗", "传说" };
            return quality < names.Length ? names[quality] : "+" + quality;
        }

        private void OpenCategoryMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = Th.Card;
            menu.ForeColor = Th.Text;
            menu.Font = Th.F(9.5f);
            menu.ShowImageMargin = false;

            foreach (string l1 in ItemCatalog.Taxonomy.L1List())
            {
                var mi1 = new ToolStripMenuItem(l1);
                foreach (string l2 in ItemCatalog.Taxonomy.L2List(l1))
                {
                    var mi2 = new ToolStripMenuItem(l2);
                    foreach (string l3 in ItemCatalog.Taxonomy.L3List(l1, l2))
                    {
                        string c1 = l1, c2 = l2, c3 = l3;
                        var mi3 = new ToolStripMenuItem(l3);
                        mi3.Click += delegate { SetCategoryFilter(c1, c2, c3); };
                        mi2.DropDownItems.Add(mi3);
                    }
                    mi1.DropDownItems.Add(mi2);
                }
                menu.Items.Add(mi1);
            }
            menu.Show(_catBtn, new Point(0, _catBtn.Height));
        }

        private void SetCategoryFilter(string l1, string l2, string l3)
        {
            _selL1 = l1; _selL2 = l2; _selL3 = l3;
            if (l1 == "全部") { _catBtn.Text = "类别：全部"; }
            else if (l2 == "全部") { _catBtn.Text = "类别：" + l1; }
            else if (l3 == "全部") { _catBtn.Text = "类别：" + l1 + " / " + l2; }
            else { _catBtn.Text = "类别：" + l1 + " / " + l2 + " / " + l3; }

            // 筛选不联网：只改选择 → 重过滤 → 重绘（零 HTTP）。要拿新价必须显式点「查询行情」。
            RenderRows();
        }

        private bool PassesFilter(string itemId, int quality)
        {
            string baseId = MarketDb.BaseOf(itemId);

            if (_selL1 != "全部" && ItemCatalog.CategoryOf(baseId) != _selL1) return false;

            if (_selL1 != "全部" && _selL2 != "全部")
            {
                string kw = ItemCatalog.Taxonomy.KeywordFor(_selL1, _selL2);
                if (kw != null)
                {
                    if (kw.Length == 0) return false;
                    if (baseId.ToUpperInvariant().IndexOf(kw) < 0) return false;
                }
            }

            if (_selL3 != "全部")
            {
                string display;
                if (!_cache.Map.TryGetValue(baseId, out display) || string.IsNullOrEmpty(display))
                    return false;
                if (display.IndexOf(_selL3, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }

            if (_fTier.SelectedIndex > 0 &&
                ItemCatalog.TierOf(baseId) != _fTier.SelectedIndex) return false;
            if (_fEnchant.SelectedIndex > 0 &&
                ItemCatalog.EnchantOf(itemId) != _fEnchant.SelectedIndex - 1) return false;
            if (_fQuality.SelectedIndex > 0 &&
                quality != _fQuality.SelectedIndex - 1) return false;
            return true;
        }

        private void StartQuery()
        {
            var cities = CheckedCities();
            if (cities.Count == 0)
            {
                SetStatus("请至少勾选一个城市", Th.Bad);
                return;
            }
            string rawText = _items.Text.Trim();
            if (rawText.Length == 0)
            {
                SetStatus("请输入物品 ID 或中文名", Th.Bad);
                _items.Focus();
                return;
            }
            Rt.Settings.LastCities = string.Join(",", cities.ToArray());
            Rt.Save();

            SetStatus("正在解析名称并读取本地库…", Th.Info);
            StartRefresh(false, rawText);
        }

        /// <summary>后台线程：解析词 → 读本地库先渲染 → 分批回源 → 落库 → 再渲染。</summary>
        private void StartRefresh(bool automatic, string rawText)
        {
            if (_refreshing) return;
            var cities = CheckedCities();
            if (cities.Count == 0) return;

            _refreshing = true;
            _query.Enabled = !automatic;

            var worker = new Thread(delegate()
            {
                try
                {
                    // 1. 解析用户输入
                    var names = new Dictionary<string, string>();
                    var shown = new List<string>();
                    if (rawText != null && rawText.Trim().Length > 0)
                    {
                        shown = ItemCatalog.ResolveTerms(rawText, names, _cache, ItemIndex.Default);
                    }

                    // 2. 刷新范围 = 缓存里已有的物品 + 本次解析出的 id（纯内存，不联网）
                    var toRefresh = RefreshScope(shown);

                    // 3. 本地库先渲染（秒开）
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            // 搜索词有结果才限定；空结果（用户输入没匹配到）就显示空，不要兜底全部
                            if (rawText != null && rawText.Trim().Length > 0)
                                _shownIds = new List<string>(shown);
                            else
                                _shownIds = new List<string>();
                            MergeNames(names);
                            RenderRows();
                            if (shown.Count == 0 && rawText != null && rawText.Trim().Length > 0)
                                SetStatus("没找到「" + rawText.Trim() + "」，试试 ID 如 T4_AXE 或换关键词", Th.Bad);
                        }));
                    }

                    // 4. 分批回源——**唯一合法出口**：MarketDataService（限流 60rpm + 内存缓存 + 单飞去重）。
                    //    任何绕开服务层直连 AODP 的写法都会跳过限流器，顶到 429 后用户会看到「刷新一半没数据」。
                    var chunks = MarketDb.Chunk(toRefresh, 80);
                    int fetched = 0;
                    bool rateLimited = false;
                    for (int ci = 0; ci < chunks.Count; ci++)
                    {
                        if (IsDisposed) break;
                        try
                        {
                            var quotes = MarketDataService.Instance
                                .GetPricesAsync(chunks[ci], cities, MarketQueryPurpose.ManualQuery, null, _cts.Token)
                                .GetAwaiter().GetResult();
                            fetched += quotes.Count;
                            // MarketDb 降级为「只写持久化历史」（唯一读源裁定见报告）：
                            // 价格一律从 MarketDataService 内存缓存读，这里只把本轮结果归档落盘。
                            _db.UpsertBatch(quotes);
                            _db.MarkFetched(chunks[ci]);
                            _lastRefresh = DateTime.Now;
                            if (!IsDisposed && IsHandleCreated)
                            {
                                int pct = (ci + 1) * 100 / Math.Max(1, chunks.Count);
                                string msg = "回源 " + (ci + 1) + "/" + chunks.Count + " 批（" + pct
                                             + "%）· 缓存 " + MarketDataService.Instance.CachedEntryCount + " 条";
                                BeginInvoke(new MethodInvoker(delegate { SetStatus(msg, Th.Sub); }));
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            if (ex is MarketRateLimitedException ||
                                ex.Message.IndexOf("429", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                rateLimited = true;
                                break;
                            }
                        }
                    }
                    _db.Save(_dbPath);

                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            _refreshing = false;
                            _query.Enabled = true;
                            if (rateLimited)
                            {
                                SetStatus("缓存 " + MarketDataService.Instance.CachedEntryCount
                                          + " 条 · 限流稍后自动重试", Th.Sub);
                            }
                            else
                            {
                                SetStatus("缓存 " + MarketDataService.Instance.CachedEntryCount
                                          + " 条 · 刚回源 " + fetched + " 行 · "
                                          + _lastRefresh.ToString("HH:mm:ss") + " · 5 分钟后自动刷新", Th.Good);
                            }
                            RenderRows();
                            _iconTicks = 0;
                            _iconTimer.Start();
                        }));
                    }
                    else { _refreshing = false; }
                }
                catch
                {
                    _refreshing = false;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void StartRefresh(bool automatic)
        {
            StartRefresh(automatic, null);
        }

        private void MergeNames(Dictionary<string, string> names)
        {
            if (names == null) return;
            bool dirty = false;
            foreach (var kv in names)
            {
                if (!_cache.Map.ContainsKey(kv.Key)) { _cache.Map[kv.Key] = kv.Value; dirty = true; }
            }
            if (dirty) _cache.Save(_cachePath);
        }

        /// <summary>
        /// 唯一读源：MarketDataService 内存缓存。**这里不发任何请求、不改任何状态**，
        /// 只把快照克隆出来并按当前时间重算陈旧标记（缓存写入时不冻结陈旧判定）。
        /// MarketDb 在本页降级为只写归档，不再参与读。
        /// </summary>
        private List<MarketQuote> CacheRows()
        {
            var now = DateTimeOffset.UtcNow;
            var list = new List<MarketQuote>();
            foreach (var e in MarketDataService.Instance.SnapshotCache())
            {
                if (e == null || e.Quote == null) continue;
                var q = e.Quote.Clone();
                q.SellStale = MarketClient.IsSellStale(q.SellHasData, q.SellMinDate, now);
                list.Add(q);
            }
            list.Sort(delegate (MarketQuote a, MarketQuote b)
            {
                int c = string.Compare(a.ItemId, b.ItemId, StringComparison.Ordinal);
                if (c != 0) return c;
                c = string.Compare(a.City, b.City, StringComparison.Ordinal);
                if (c != 0) return c;
                return a.Quality.CompareTo(b.Quality);
            });
            return list;
        }

        /// <summary>回源范围：缓存里已有的物品 + 本次解析出的物品（去重）。不发请求，纯内存。</summary>
        private List<string> RefreshScope(List<string> resolved)
        {
            var ids = new List<string>();
            var seen = new HashSet<string>();
            foreach (var q in CacheRows())
                if (seen.Add(q.ItemId)) ids.Add(q.ItemId);
            if (resolved != null)
                foreach (string id in resolved)
                    if (seen.Add(id)) ids.Add(id);
            // 缓存空且用户没输入时，退回本地归档里的物品名（只取 id，不取价格），让首轮刷新有范围
            if (ids.Count == 0)
                foreach (string id in _db.KnownItemIds())
                    if (seen.Add(id)) ids.Add(id);
            return ids;
        }

        private void RenderRows()
        {
            var cities = CheckedCities();
            var wanted = new HashSet<string>(cities);
            var allowed = new HashSet<string>(_shownIds ?? new List<string>());
            bool scoped = _items.Text.Trim().Length > 0 && allowed.Count > 0;

            var source = CacheRows();
            var rows = new List<string[]>();
            var images = new List<Image>();
            for (int i = 0; i < source.Count; i++)
            {
                var q = source[i];
                if (wanted.Count > 0 && !wanted.Contains(q.City)) continue;
                if (scoped && !allowed.Contains(q.ItemId)) continue;
                if (!PassesFilter(q.ItemId, q.Quality)) continue;

                string display;
                if (!_cache.Map.TryGetValue(MarketDb.BaseOf(q.ItemId), out display) || string.IsNullOrEmpty(display))
                    display = q.ItemId;

                rows.Add(new string[]
                {
                    display,
                    q.City,
                    QualityLabel(q.Quality),
                    // 0 = AODP 无上报数据（不是免费），不得画成 0
                    q.SellHasData ? Th.Price(q.SellMin) : "—",
                    q.BuyMax > 0 ? Th.Price(q.BuyMax) : "—",
                    q.SellHasData ? Th.DateText(q.SellMinDate) : "暂无上报"
                });
                images.Add(ItemIcons.Get(MarketDb.BaseOf(q.ItemId), q.Quality));
            }
            _table.SetRows(rows, images);
        }

        private void RefreshRowIcons()
        {
            if (MarketDataService.Instance.CachedEntryCount == 0) { _iconTimer.Stop(); return; }
            RenderRows();
            if (++_iconTicks >= 6) _iconTimer.Stop();
        }

        public override void RefreshData()
        {
        }

        protected override void Dispose(bool disposing)
        {
            // 中断在途回源：MarketDataService 的在途请求挂在调用方 ct 上（P1-2），
            // 窗体关掉后不该再让后台线程替一个不存在的界面把队列跑完。
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            base.Dispose(disposing);
        }
    }
}
