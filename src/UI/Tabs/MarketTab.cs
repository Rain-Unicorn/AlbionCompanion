using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AlbionCompanion.Core;
using AlbionCompanion.UI.Controls;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>
    /// 市场行情（M4 接线，按用户定稿设计规范）：
    ///   页头（MARKET QUERY / 刷新 / 查询计数）→ 亚服时效条 → 三级联动筛选卡（自绘 MarketTreePanel）
    ///   → 阶级/附魔 chip（在筛选卡三级列内）+ 品质 / 城市筛选 → 六城报价卡。
    ///
    /// 硬约束：
    /// ① 渲染路径零 HTTP：重绘/换筛选/切城市只读 MarketDataService 内存缓存快照；
    ///    取数唯一出口是「刷新」按钮/5 分钟自动刷新里的 MarketDataService.GetPricesAsync。
    /// ② 市场页只查价：原始报价 + 时间戳 + 缺失标记；不出现税后/差价/利润/推荐（归跑商页）。
    /// ③ 买卖方向不用颜色区分（列位 + ↓↑ 符号）；颜色只表达数据时效。
    /// ④ 相对时间统一由 MarketClock（单调时钟）+ QuoteTableModel 函数产出。
    /// </summary>
    public class MarketTab : TabBase
    {
        private readonly TextBox _search;
        private readonly Button _refresh;
        private readonly Label _status;
        private readonly Label _queryCount;
        private readonly Label _selection;
        private readonly LinkLabel _dataVersion;
        private readonly Panel _freshStrip;
        private readonly MarketTreePanel _tree;
        private readonly ComboBox _fQuality;
        private readonly List<CheckBox> _cityChecks = new List<CheckBox>();
        private readonly List<string> _cityNames = new List<string>();
        private readonly QuoteTable _card;

        private readonly System.Windows.Forms.Timer _autoTimer;

        private readonly MarketDb _db;
        private readonly string _dbPath;
        private readonly NameCache _cache;
        private readonly string _cachePath;
        private ItemTaxonomy _taxonomy;

        private string _searchText = "";
        private bool _searchNoMatch;
        private List<string> _searchIds = new List<string>();
        private List<string> _rawIds = new List<string>();

        private bool _refreshing;
        private bool _updatingData;
        private int _querySeq;
        private DateTime _lastRefresh;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _suppressFilters;

        public MarketTab(Runtime runtime)
            : base(runtime)
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                         + @"\AlbionCompanion";
            _cachePath = System.IO.Path.Combine(dir, "itemnames.json");
            _cache = NameCache.Load(_cachePath);
            _db = Rt.Db;
            _dbPath = Rt.DbPath;

            // ── 页头（0..56）──
            CardText("MARKET QUERY", 14, 4, 160, 14, Th.Sub, 7.5f, false);
            CardText("市场行情", 14, 16, 200, 28, Th.Text, 13f, true);

            _dataVersion = new LinkLabel();
            _dataVersion.Bounds = new Rectangle(300, 5, 230, 16);
            _dataVersion.Font = Th.F(8f);
            _dataVersion.BackColor = Color.Transparent;
            _dataVersion.ForeColor = Th.Sub;
            _dataVersion.TextAlign = ContentAlignment.MiddleRight;
            _dataVersion.LinkBehavior = LinkBehavior.HoverUnderline;
            _dataVersion.Click += delegate { OnUpdateItemData(); };
            Controls.Add(_dataVersion);

            _queryCount = CardText("", 300, 25, 150, 16, Th.Sub, 8f, false);
            _queryCount.TextAlign = ContentAlignment.MiddleRight;

            _refresh = Ui.Primary("刷新", 530, 16, 76, 26);
            _refresh.Click += delegate { StartQuery(false); };
            Controls.Add(_refresh);

            _status = CardText("缓存 " + MarketDataService.Instance.CachedEntryCount
                               + " 条 · 选择分类后点「刷新」回源", 14, 40, 592, 15, Th.Sub, 8f, false);

            // 搜索框（叠在筛选卡卡头空白区）：命中的物品会导航到三级树并直接查询；
            // 无命中显示空表+提示（绝不回落到全量缓存）
            _search = Ui.Input(200, 6, 300, 24);
            _search.PlaceholderText = "物品 ID / 中文名";
            _search.KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) StartQuery(false);
            };

            // ── 亚服时效条（60..94）──
            _freshStrip = new Panel();
            _freshStrip.Location = new Point(0, 60);
            _freshStrip.Size = new Size(620, 34);
            _freshStrip.BackColor = Th.Bg;
            _freshStrip.Paint += delegate (object sender, PaintEventArgs e) { DrawFreshStrip(e.Graphics); };
            Controls.Add(_freshStrip);

            // ── 三级联动筛选卡（100..400）：自绘面板，三列分区 + 悬停迟滞 + 键盘 ──
            _tree = new MarketTreePanel();
            _tree.Location = new Point(0, 100);
            _tree.Size = new Size(620, 300);
            _tree.SelectionChanged += delegate { RenderCard(); };
            _tree.Controls.Add(_search);
            Controls.Add(_tree);

            // ── 品质 / 城市筛选（404..446）──
            CardText("品质", 14, 404, 120, 14, Th.Sub, 8f, false);
            _fQuality = Ui.Dropdown(MarketQuality.DropdownItems(), 14, 420, 130, 24);
            _fQuality.SelectedIndexChanged += delegate { if (!_suppressFilters) RenderCard(); };
            Controls.Add(_fQuality);

            CardText("城市（可多选，含黑市）", 160, 404, 280, 14, Th.Sub, 8f, false);
            string[] shortNames = { "凯城", "桥望", "马丁", "塞福", "斯特", "林堡", "黑市" };
            for (int i = 0; i < MarketCities.All.Length; i++)
            {
                var check = Ui.Check(i < shortNames.Length ? shortNames[i] : MarketCities.All[i],
                                     160 + i * 62, 422, 60, true);
                check.CheckedChanged += delegate { if (!_suppressFilters) RenderCard(); };
                Controls.Add(check);
                _cityChecks.Add(check);
                _cityNames.Add(MarketCities.All[i]);
            }

            _selection = CardText("", 450, 404, 160, 16, Th.Sub, 7.5f, false);
            _selection.TextAlign = ContentAlignment.MiddleRight;

            // ── 六城报价卡（452..700；勾选黑市时 726）──
            _card = new QuoteTable();
            _card.Location = new Point(0, 452);
            _card.Size = new Size(QuoteTable.CardWidth, QuoteTable.CardHeight);
            _card.RetryRequested += delegate { StartQuery(false); };
            Controls.Add(_card);

            _autoTimer = new System.Windows.Forms.Timer();
            _autoTimer.Interval = 5 * 60 * 1000;
            _autoTimer.Tick += delegate { if (!_refreshing) StartQuery(true); };
            _autoTimer.Start();

            RebuildTaxonomy();
            UpdateDataVersionLabel();
            RenderCard();
        }

        // ── 数据版本 / 物品数据 ───────────────────────────────────────────

        private void RebuildTaxonomy()
        {
            ItemIndex index = ItemIndex.Default;
            if (index == null) index = ItemIndex.LoadDefault();
            _taxonomy = ItemTaxonomy.Build(index);
            _tree.SetTaxonomy(_taxonomy);
            if (index == null)
                SetStatus("物品数据未加载：分类树不可用（仍可直接粘贴物品 ID 查询）· 点击右上角下载", Th.Bad);
        }

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
                        RebuildTaxonomy();
                        UpdateDataVersionLabel();
                        SetStatus("物品数据已更新：" + ItemIndex.Default.Count + " 条 · 重量 "
                                  + ItemIndex.Default.WeightedCount + "/" + ItemIndex.Default.Count
                                  + " · 版本 v" + ItemIndex.DataVersion, Th.Good);
                        RenderCard();
                    }
                    else
                    {
                        UpdateDataVersionLabel();
                        SetStatus("物品数据更新失败：" + (err ?? "解析为空"), Th.Bad);
                    }
                }));
            });
        }

        private void SetStatus(string text, Color color)
        {
            _status.Text = text;
            _status.ForeColor = color;
        }

        // ── 亚服时效条（6×6 heal 点 + 数据源 + 更新于 + 时效胶囊）──────────

        private void DrawFreshStrip(Graphics g)
        {
            g.Clear(Th.Bg);
            Th.FillRound(g, new Rectangle(0, 2, 620, 30), 8, Th.Card);

            for (int i = 0; i < 6; i++)
            {
                using (var br = new SolidBrush(Th.Good))
                    g.FillEllipse(br, 14 + i * 11, 14, 6, 6);
            }

            DateTimeOffset now = MarketClock.UtcNow;
            DateTimeOffset? latest = null;
            foreach (var entry in MarketDataService.Instance.SnapshotCache())
            {
                if (entry == null) continue;
                if (!latest.HasValue || entry.FetchedAt > latest.Value) latest = entry.FetchedAt;
            }

            TextRenderer.DrawText(g, "亚服数据", Th.FB(9f), new Point(88, 8), Th.Text,
                TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "AODP · east", Th.F(8f), new Point(158, 9), Th.Sub,
                TextFormatFlags.NoPadding);

            string updateText;
            QuoteFreshness tier = QuoteFreshness.Expired;
            if (latest.HasValue)
            {
                var age = now - latest.Value;
                if (age < TimeSpan.Zero) age = TimeSpan.Zero;
                tier = QuoteTableModel.Classify(age);
                updateText = "更新于 " + latest.Value.ToLocalTime().ToString("HH:mm") + " · "
                             + QuoteTableModel.CellTimeText(latest, now);
            }
            else
            {
                updateText = "尚未回源";
            }
            TextRenderer.DrawText(g, updateText, Th.F(8f), new Rectangle(248, 8, 220, 16), Th.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.EndEllipsis);

            string capsule = tier == QuoteFreshness.Fresh ? "可用"
                             : tier == QuoteFreshness.Aging ? "偏旧" : "已过期";
            Color capBg = tier == QuoteFreshness.Fresh
                ? Color.FromArgb(0x16, 0x28, 0x27)   // heal 12% 压成实色
                : Color.FromArgb(0x2A, 0x24, 0x19);  // warn 12% 压成实色
            Color capFg = tier == QuoteFreshness.Fresh ? Th.Good : Th.Accent;
            var capRect = new Rectangle(620 - 16 - 96, 7, 96, 22);
            Th.FillRound(g, capRect, 11, capBg);
            TextRenderer.DrawText(g, capsule, Th.FB(8.5f), capRect, capFg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // ── 搜索 ───────────────────────────────────────────────────────────

        private void ApplySearch(string text)
        {
            _searchText = text ?? "";
            _searchNoMatch = false;
            _rawIds = new List<string>();
            _searchIds = new List<string>();
            if (_searchText.Trim().Length == 0) return;

            var names = new Dictionary<string, string>();
            _searchIds = ItemCatalog.ResolveTerms(_searchText, names, _cache, ItemIndex.Default);
            MergeNames(names);

            var state = MarketSearchScope.Of(_searchText, _searchIds);
            if (state == MarketSearchState.NoMatches) { _searchNoMatch = true; return; }
            if (state == MarketSearchState.NoQuery) return;

            for (int i = 0; i < _searchIds.Count; i++)
            {
                var located = MarketItemClassifier.Locate(_taxonomy, _searchIds[i]);
                if (!located.Found) continue;
                if (_tree.Navigate(located.Category.Key, located.SubCategory.Key,
                                   located.Family.Family, located.Tier, located.Enchant))
                    return;
            }

            // 命中项全部无分类元数据：直查 + 关键词回退标注（不臆造分类）
            _rawIds = new List<string>(_searchIds);
            _tree.ClearAll();
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

        // ── 查询目标 ───────────────────────────────────────────────────────

        private List<string> CheckedCities()
        {
            var cities = new List<string>();
            for (int i = 0; i < _cityChecks.Count; i++)
                if (_cityChecks[i].Checked) cities.Add(_cityNames[i]);
            return cities;
        }

        private int SelectedQuality()
        {
            return MarketQuality.FromDropdownIndex(_fQuality.SelectedIndex);
        }

        private List<int> SelectedQualities()
        {
            int q = SelectedQuality();
            var list = new List<int>();
            if (q <= 0) list.AddRange(MarketSelection.AllQualities);
            else list.Add(q);
            return list;
        }

        /// <summary>当前筛选的完整状态（面包屑/查价键共用）。</summary>
        private MarketSelection BuildSelection()
        {
            var sel = new MarketSelection();
            if (!string.IsNullOrEmpty(_tree.CategoryKey)) sel.CategoryKey = _tree.CategoryKey;
            if (!string.IsNullOrEmpty(_tree.SubCategoryKey)) sel.SubCategoryKey = _tree.SubCategoryKey;
            if (_tree.Family != null) sel.Family = _tree.Family.Family;
            sel.Tier = _tree.Tier;
            sel.Enchant = _tree.Enchant;
            sel.Qualities.Clear();
            sel.Qualities.AddRange(SelectedQualities());
            return sel;
        }

        /// <summary>
        /// 当前要查的 uniqueName + 品质（走 MarketQueryKey/MarketQueryPlanner，
        /// 与筛选产物同源；组合不存在时返回空键 → 不发请求）。
        /// 搜索命中但无分类元数据的物品走 _rawIds 直查（回退标注，不臆造分类）。
        /// </summary>
        private List<string> CurrentTargets(out List<int> qualities)
        {
            qualities = SelectedQualities();
            if (_rawIds.Count > 0) return new List<string>(_rawIds);

            var key = MarketQueryPlanner.Build(_taxonomy, BuildSelection());
            if (key.IsEmpty) return null;
            qualities = new List<int>(key.Qualities);
            return new List<string>(key.UniqueNames);
        }

        // ── 渲染（只读快照，零 HTTP）───────────────────────────────────────

        private void RenderCard()
        {
            _freshStrip.Invalidate();
            DateTimeOffset now = MarketClock.UtcNow;
            var cities = CheckedCities();

            if (_searchNoMatch)
            {
                _card.SetMessage("没找到「" + _searchText + "」",
                    "试试 ID（如 T4_AXE / T5_2H_BOW@2）或中文关键词；不会显示无关缓存", false);
                _selection.Text = "搜索无结果";
                return;
            }

            List<int> qualities;
            var targets = CurrentTargets(out qualities);
            if (targets == null || targets.Count == 0)
            {
                _card.SetNotSelected();
                _selection.Text = "未选物品";
                return;
            }

            if (cities.Count == 0)
            {
                _card.SetMessage("请至少勾选一个城市", "城市筛选在筛选卡下方", false);
                _selection.Text = "";
                return;
            }

            var entries = MarketDataService.Instance.SnapshotPricingEntries();
            var quotes = CollectQuotes(entries, targets, qualities, cities, now);
            if (quotes.Count == 0)
            {
                // 选满但缓存里没有该组合 →「尚未查询」，不冒充「无挂单」
                _card.SetNotQueried();
                _selection.Text = "未查询";
                return;
            }

            int qualityUsed;
            var rows = QuoteTableModel.Build(quotes, cities, now, out qualityUsed);
            if (rows.Count == 0) { _card.SetNoData(); return; }

            _card.SetHeadRight(cities.Count + " 城 · 亚服");
            _card.SetData(rows, now);
            _selection.Text = "品质 " + MarketQuality.LabelOf(qualityUsed);
        }

        /// <summary>从缓存快照挑「目标物品 × 所选品质 × 所选城市」，克隆并按 now 重算陈旧标记。</summary>
        private static List<MarketQuote> CollectQuotes(List<MarketCacheEntry> entries, List<string> targets,
            List<int> qualities, List<string> cities, DateTimeOffset now)
        {
            var quotes = new List<MarketQuote>();
            if (entries == null || targets == null) return quotes;
            var targetSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in targets) targetSet.Add(MarketItemKey.Normalize(t));
            var qualitySet = new HashSet<int>(qualities ?? new List<int>());
            var citySet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in cities) citySet.Add(MarketCities.Normalize(c));

            foreach (var entry in entries)
            {
                if (entry == null || entry.Quote == null) continue;
                var q = entry.Quote;
                if (!targetSet.Contains(MarketItemKey.Normalize(q.ItemId))) continue;
                if (!qualitySet.Contains(q.Quality)) continue;
                if (!citySet.Contains(MarketCities.Normalize(q.City))) continue;
                var copy = q.Clone();
                copy.SellStale = MarketClient.IsSellStale(copy.SellHasData, copy.SellMinDate, now);
                quotes.Add(copy);
            }
            return quotes;
        }

        // ── 查询（唯一取数出口：MarketDataService）──────────────────────────

        private void StartQuery(bool automatic)
        {
            if (_refreshing) return;

            string searchText = _search.Text.Trim();
            ApplySearch(searchText);
            if (_searchNoMatch)
            {
                RenderCard();
                SetStatus("没找到「" + searchText + "」，试试 ID 如 T4_AXE 或换关键词（不会显示无关缓存）", Th.Bad);
                return;
            }

            var cities = CheckedCities();
            if (cities.Count == 0)
            {
                SetStatus("请至少勾选一个城市", Th.Bad);
                RenderCard();
                return;
            }

            List<int> qualities;
            var targets = CurrentTargets(out qualities);
            if (targets == null || targets.Count == 0)
            {
                SetStatus("请先在筛选卡选满一级 / 二级 / 三级物品与阶级，或输入物品 ID", Th.Bad);
                RenderCard();
                return;
            }

            Rt.Settings.LastCities = string.Join(",", cities.ToArray());
            Rt.Save();

            _querySeq++;
            UpdateQueryCountLabel();
            _refreshing = true;
            _refresh.Enabled = !automatic;
            SetStatus("正在回源 " + targets.Count + " 个物品 × " + cities.Count + " 城…", Th.Info);

            var targetCopy = new List<string>(targets);
            var qualityCopy = new List<int>(qualities);
            var cityCopy = new List<string>(cities);
            var ct = _cts.Token;

            var worker = new Thread(delegate ()
            {
                try
                {
                    // 唯一合法出口：MarketDataService（限流 60rpm + 内存缓存 + 单飞去重）
                    var quotes = MarketDataService.Instance
                        .GetPricesAsync(targetCopy, cityCopy, MarketQueryPurpose.ManualQuery, qualityCopy, ct)
                        .GetAwaiter().GetResult();

                    // MarketDb 只写持久化历史（读价一律走内存缓存）
                    _db.UpsertBatch(quotes);
                    string saveErr = null;
                    try { _db.Save(_dbPath); }
                    catch (Exception ex) { saveErr = ex.Message; }
                    _lastRefresh = DateTime.Now;

                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _refreshing = false;
                        _refresh.Enabled = true;
                        SetStatus("缓存 " + MarketDataService.Instance.CachedEntryCount + " 条 · 本轮回源 "
                                  + quotes.Count + " 行 · " + _lastRefresh.ToString("HH:mm:ss")
                                  + (saveErr != null ? " · 本地库保存失败：" + saveErr : "")
                                  + " · 5 分钟后自动刷新", saveErr != null ? Th.Bad : Th.Good);
                        RenderCard();
                    }));
                }
                catch (OperationCanceledException)
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _refreshing = false;
                        _refresh.Enabled = true;
                        SetStatus("查询已取消", Th.Sub);
                    }));
                }
                catch (Exception ex)
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _refreshing = false;
                        _refresh.Enabled = true;
                        bool rateLimited = ex is MarketRateLimitedException
                            || ex.Message.IndexOf("429", StringComparison.OrdinalIgnoreCase) >= 0;
                        // M7/M8：失败但有旧数据时保留旧卡 + 顶部 warn 条；无旧数据由卡自身空态兜底
                        RenderCard();
                        if (MarketDataService.Instance.CachedEntryCount > 0)
                            _card.SetFailedWithOldData(_lastRefresh == default(DateTime)
                                ? "缓存数据"
                                : _lastRefresh.ToString("HH:mm"));
                        SetStatus((rateLimited ? "AODP 限流：" : "查询失败：") + ex.Message, Th.Bad);
                    }));
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void UpdateQueryCountLabel()
        {
            _queryCount.Text = _querySeq <= 0 ? "" : "第 " + _querySeq + " 次查询";
        }

        public override void RefreshData()
        {
            RenderCard();
        }

        protected override void Dispose(bool disposing)
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 三级联动筛选器（设计规范 §四 / §八.1）：自绘单控件，三列按 x 分区，
    /// 不用三个 ListBox 叠加（无 hover、滚动条吃宽度）。
    ///
    /// 交互：悬停一级 240ms 迟滞后展开二级；点一级立即选中并清空二级/三级；
    /// 点二级清空三级；点三级物品后在该列底部显示阶级 T chip + 附魔 .N chip（双段式）。
    /// 键盘：↑↓ 在当前列移动、←→ 切列、Esc 逐级回退。三列滚动独立（_scroll[3]）。
    /// 上行切换顺序固定「清空 → 重算可见项 → Invalidate」（规范 §八.3）。
    /// </summary>
    internal sealed class MarketTreePanel : Control
    {
        private const int HeadH = 36;
        private const int ColHeadH = 22;
        private const int PathH = 28;
        private const int RowH = 28;
        private const int ChipsAreaH = 64;
        private static readonly int[] ColX = { 16, 172, 356 };
        private static readonly int[] ColW = { 148, 176, 248 };
        private static readonly string[] ColTitles = { "一级分类", "二级分类", "三级 · 物品" };

        private readonly Font _headFont = Th.FB(9.5f);
        private readonly Font _kickerFont = Th.F(7f);
        private readonly Font _colHeadFont = Th.F(8f);
        private readonly Font _rowFont = Th.F(9f);
        private readonly Font _chipFont = Th.F(8f);
        private readonly Font _pathFont = Th.F(8f);
        private readonly System.Windows.Forms.Timer _hoverTimer;

        private List<ItemCategory> _cats = new List<ItemCategory>();
        private List<ItemSubCategory> _subs = new List<ItemSubCategory>();
        private List<ItemFamily> _fams = new List<ItemFamily>();
        private List<int> _tiers = new List<int>();
        private List<int> _enchants = new List<int>();

        private int _cat = -1, _sub = -1, _fam = -1;
        private int _tier, _enchant;
        private int _focusCol;
        private int _hoverCol = -1, _hoverRow = -1;
        private int _hoverPendingRow = -1;
        private readonly int[] _scroll = new int[3];

        // 预计算实色（GDI+ 无 alpha）
        private static readonly Color HoverBg = Color.FromArgb(0x15, 0x1B, 0x21);
        private static readonly Color SelBg = Color.FromArgb(0x26, 0x26, 0x21);
        private static readonly Color ChipSelBg = Color.FromArgb(0x2A, 0x24, 0x19);

        public event EventHandler SelectionChanged;

        public MarketTreePanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.Selectable, true);
            BackColor = Th.Bg;
            Cursor = Cursors.Hand;
            TabStop = true;

            _hoverTimer = new System.Windows.Forms.Timer();
            _hoverTimer.Interval = 240; // 悬停迟滞（规范：240ms）
            _hoverTimer.Tick += delegate { OnHoverTick(); };
        }

        // ── 公共读取 ──────────────────────────────────────────────────────

        public string CategoryKey { get { return _cat >= 0 && _cat < _cats.Count ? _cats[_cat].Key : null; } }
        public string SubCategoryKey { get { return _sub >= 0 && _sub < _subs.Count ? _subs[_sub].Key : null; } }
        public ItemFamily Family { get { return _fam >= 0 && _fam < _fams.Count ? _fams[_fam] : null; } }
        public string FamilyKey { get { return Family == null ? null : Family.Family; } }
        public int Tier { get { return _tier; } }
        public int Enchant { get { return _enchant; } }
        public bool IsComplete { get { return Family != null && _tier > 0; } }

        public void SetTaxonomy(ItemTaxonomy tax)
        {
            _cats = tax == null ? new List<ItemCategory>() : new List<ItemCategory>(tax.Categories);
            ClearInternal();
            Invalidate();
        }

        /// <summary>搜索定位：按 key 展开三列并选中族/阶级/附魔。返回 false = 三元组不在树里。</summary>
        public bool Navigate(string categoryKey, string subKey, string familyKey, int tier, int enchant)
        {
            int cat = _cats.FindIndex(delegate (ItemCategory c) { return c.Key == categoryKey; });
            if (cat < 0) return false;
            SelectCategory(cat, false);
            int sub = _subs.FindIndex(delegate (ItemSubCategory s) { return s.Key == subKey; });
            if (sub < 0) return false;
            SelectSubCategory(sub, false);
            int fam = _fams.FindIndex(delegate (ItemFamily f) { return f.Family == familyKey; });
            if (fam < 0) return false;
            SelectFamily(fam, false);
            int tierIdx = _tiers.IndexOf(tier);
            if (tierIdx >= 0) SetTier(tier, false);
            int enchIdx = _enchants.IndexOf(enchant);
            if (enchIdx >= 0) SetEnchant(enchant, false);
            EnsureVisible();
            RaiseChanged();
            return true;
        }

        public void ClearAll()
        {
            ClearInternal();
            RaiseChanged();
        }

        private void ClearInternal()
        {
            _subs = new List<ItemSubCategory>();
            _fams = new List<ItemFamily>();
            _tiers = new List<int>();
            _enchants = new List<int>();
            _cat = _sub = _fam = -1;
            _tier = _enchant = 0;
            _scroll[0] = _scroll[1] = _scroll[2] = 0;
            _hoverPendingRow = -1;
            if (_hoverTimer != null) _hoverTimer.Stop();
        }

        // ── 选择逻辑（切换上级必须清空下级，再重算，再重绘）────────────────

        private void SelectCategory(int idx, bool raise)
        {
            if (idx < 0 || idx >= _cats.Count || idx == _cat) return;
            _cat = idx;
            _sub = -1;
            _fam = -1;
            _tier = _enchant = 0;
            _subs = new List<ItemSubCategory>(_cats[idx].SubCategories);
            _fams = new List<ItemFamily>();
            _tiers = new List<int>();
            _enchants = new List<int>();
            _scroll[1] = _scroll[2] = 0;
            if (raise) RaiseChanged();
            Invalidate();
        }

        private void SelectSubCategory(int idx, bool raise)
        {
            if (idx < 0 || idx >= _subs.Count || idx == _sub) return;
            _sub = idx;
            _fam = -1;
            _tier = _enchant = 0;
            _fams = new List<ItemFamily>(_subs[idx].Families);
            _tiers = new List<int>();
            _enchants = new List<int>();
            _scroll[2] = 0;
            if (raise) RaiseChanged();
            Invalidate();
        }

        private void SelectFamily(int idx, bool raise)
        {
            if (idx < 0 || idx >= _fams.Count || idx == _fam) return;
            _fam = idx;
            _tiers = _fams[idx].Tiers();
            _tier = _tiers.Count > 0 ? _tiers[0] : 0;
            RebuildEnchants();
            if (raise) RaiseChanged();
            Invalidate();
        }

        private void SetTier(int tier, bool raise)
        {
            if (_tier == tier) return;
            _tier = tier;
            RebuildEnchants();
            if (raise) RaiseChanged();
            Invalidate();
        }

        private void SetEnchant(int enchant, bool raise)
        {
            if (_enchant == enchant) return;
            _enchant = enchant;
            if (raise) RaiseChanged();
            Invalidate();
        }

        private void RebuildEnchants()
        {
            var fam = Family;
            _enchants = fam == null || _tier <= 0 ? new List<int>() : fam.Enchants(_tier);
            if (_enchants.Count == 0) _enchants.Add(0);
            _enchant = _enchants.Contains(0) ? 0 : _enchants[0];
        }

        private void EnsureVisible()
        {
            ScrollToShow(0, _cat);
            ScrollToShow(1, _sub);
            ScrollToShow(2, _fam);
        }

        private void ScrollToShow(int col, int idx)
        {
            if (idx < 0) return;
            int visible = VisibleRows(col);
            if (idx < _scroll[col]) _scroll[col] = idx;
            else if (idx >= _scroll[col] + visible) _scroll[col] = idx - visible + 1;
            ClampScroll(col);
        }

        private void RaiseChanged()
        {
            var handler = SelectionChanged;
            if (handler != null) handler(this, EventArgs.Empty);
            Invalidate();
        }

        // ── 布局 ──────────────────────────────────────────────────────────

        private int BodyTop { get { return HeadH + ColHeadH; } }
        private int BodyBottom { get { return Height - PathH; } }
        private int BodyH { get { return Math.Max(0, BodyBottom - BodyTop); } }

        private int ListRowsCol2()
        {
            int h = Family == null ? BodyH : BodyH - ChipsAreaH;
            return Math.Max(1, h / RowH);
        }

        private int VisibleRows(int col)
        {
            if (col == 2) return ListRowsCol2();
            return Math.Max(1, BodyH / RowH);
        }

        private int CountOf(int col)
        {
            return col == 0 ? _cats.Count : col == 1 ? _subs.Count : _fams.Count;
        }

        private void ClampScroll(int col)
        {
            int max = Math.Max(0, CountOf(col) - VisibleRows(col));
            if (_scroll[col] < 0) _scroll[col] = 0;
            if (_scroll[col] > max) _scroll[col] = max;
        }

        // ── 绘制 ──────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Th.Bg);

            Th.FillRound(g, new Rectangle(0, 0, Width, Height), 10, Th.Card);
            Th.StrokeRound(g, new Rectangle(0, 0, Width - 1, Height - 1), 10, Th.Line);

            DrawHead(g);
            DrawColumnHeads(g);
            DrawColumns(g);
            DrawPathBar(g);
        }

        private void DrawHead(Graphics g)
        {
            TextRenderer.DrawText(g, "筛选物品", _headFont, new Point(16, 9), Th.Text,
                TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "ITEM FILTER", _kickerFont, new Point(82, 13), Th.Sub,
                TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "重置", _colHeadFont, ResetRect(), Th.Accent,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private Rectangle ResetRect()
        {
            return new Rectangle(Width - 16 - 48, 8, 48, 20);
        }

        private void DrawColumnHeads(Graphics g)
        {
            for (int c = 0; c < 3; c++)
            {
                var rect = new Rectangle(ColX[c], HeadH, ColW[c], ColHeadH);
                TextRenderer.DrawText(g, ColTitles[c], _colHeadFont, rect,
                    c == _focusCol ? Th.Text : Th.Sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (c > 0)
                {
                    using (var pen = new Pen(Th.Line))
                        g.DrawLine(pen, ColX[c] - 4, HeadH - 4, ColX[c] - 4, BodyBottom);
                }
            }
        }

        private void DrawColumns(Graphics g)
        {
            DrawRows(g, 0, _cats.Count);
            DrawRows(g, 1, _subs.Count);
            DrawRows(g, 2, _fams.Count);
            if (Family != null) DrawChips(g);
            DrawScrollThumbs(g);
        }

        private void DrawPlaceholder(Graphics g, int col, string text)
        {
            var rect = new Rectangle(ColX[col] + 8, BodyTop + 6, ColW[col] - 16, 20);
            TextRenderer.DrawText(g, text, _colHeadFont, rect, Th.Sub,
                TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }

        private void DrawRows(Graphics g, int col, int count)
        {
            if (count == 0)
            {
                if (col == 1 && _cat < 0) DrawPlaceholder(g, col, "↑ 请先选择左侧分类");
                else if (col == 2 && _sub < 0) DrawPlaceholder(g, col, "↑ 请先选择左侧分类");
                return;
            }

            int visible = VisibleRows(col);
            ClampScroll(col);

            for (int i = _scroll[col]; i < count && i < _scroll[col] + visible; i++)
            {
                int y = BodyTop + (i - _scroll[col]) * RowH;
                var rect = new Rectangle(ColX[col], y, ColW[col], RowH);
                bool selected = col == 0 ? i == _cat : col == 1 ? i == _sub : i == _fam;
                bool hover = _hoverCol == col && _hoverRow == i;
                if (selected || hover)
                {
                    using (var br = new SolidBrush(selected ? SelBg : HoverBg))
                        g.FillRectangle(br, rect);
                }

                string label = LabelOf(col, i);
                var textRect = new Rectangle(rect.X + 8, rect.Y, rect.Width - 24, RowH);
                TextRenderer.DrawText(g, label, _rowFont, textRect,
                    selected ? Th.Accent : Th.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                    | TextFormatFlags.EndEllipsis);
                if (selected)
                {
                    TextRenderer.DrawText(g, "›", _rowFont, new Rectangle(rect.Right - 18, rect.Y, 12, RowH),
                        Th.Accent, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }
        }

        private string LabelOf(int col, int i)
        {
            if (col == 0) return _cats[i].DisplayName;
            if (col == 1) return _subs[i].DisplayName;
            var fam = _fams[i];
            return string.IsNullOrEmpty(fam.DisplayName) ? fam.Family : fam.DisplayName;
        }

        private void DrawChips(Graphics g)
        {
            int col = 2;
            int top = BodyBottom - ChipsAreaH;
            using (var pen = new Pen(Th.Line))
                g.DrawLine(pen, ColX[col], top, ColX[col] + ColW[col], top);

            int x = ColX[col] + 8;
            int y = top + 8;
            TextRenderer.DrawText(g, "阶级", _chipFont, new Rectangle(x, y, 30, 20), Th.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += 34;
            for (int i = 0; i < _tiers.Count; i++)
            {
                DrawChip(g, new Rectangle(x, y, 32, 20), "T" + _tiers[i], _tiers[i] == _tier);
                x += 36;
            }

            x = ColX[col] + 8;
            y = top + 32;
            TextRenderer.DrawText(g, "附魔", _chipFont, new Rectangle(x, y, 30, 20), Th.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += 34;
            for (int i = 0; i < _enchants.Count; i++)
            {
                DrawChip(g, new Rectangle(x, y, 30, 20), "." + _enchants[i], _enchants[i] == _enchant);
                x += 34;
            }
        }

        private void DrawChip(Graphics g, Rectangle r, string text, bool selected)
        {
            if (selected) Th.FillRound(g, r, 4, ChipSelBg);
            else Th.StrokeRound(g, r, 4, Th.Line);
            TextRenderer.DrawText(g, text, _chipFont, r,
                selected ? Th.Accent : Th.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawScrollThumbs(Graphics g)
        {
            for (int c = 0; c < 3; c++)
            {
                int count = CountOf(c);
                int visible = VisibleRows(c);
                if (count <= visible) continue;
                int trackTop = BodyTop + 2;
                int trackH = (c == 2 && Family != null ? BodyH - ChipsAreaH : BodyH) - 4;
                if (trackH <= 0) continue;
                int thumbH = Math.Max(20, trackH * visible / count);
                int maxScroll = count - visible;
                int thumbY = trackTop + (int)((trackH - thumbH) * ((float)_scroll[c] / maxScroll));
                using (var br = new SolidBrush(Th.CardHi))
                    g.FillRectangle(br, ColX[c] + ColW[c] - 4, thumbY, 2, thumbH);
            }
        }

        private void DrawPathBar(Graphics g)
        {
            int y = Height - PathH;
            using (var pen = new Pen(Th.Line))
                g.DrawLine(pen, 16, y, Width - 16, y);

            string path = "全部";
            if (_cat >= 0 && _cat < _cats.Count)
            {
                path = _cats[_cat].DisplayName;
                if (_sub >= 0 && _sub < _subs.Count)
                {
                    path += " › " + _subs[_sub].DisplayName;
                    if (Family != null)
                    {
                        path += " › " + LabelOf(2, _fam);
                        if (_tier > 0) path += " › T" + _tier + " · ." + _enchant;
                    }
                }
            }
            TextRenderer.DrawText(g, path, _pathFont, new Rectangle(16, y + 1, 480, PathH),
                Family != null ? Th.Accent : Th.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, "Esc 逐级回退", _chipFont, new Rectangle(Width - 16 - 110, y + 1, 110, PathH),
                Th.Sub, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // ── 命中检测 / 交互 ───────────────────────────────────────────────

        private int ColumnAt(int x)
        {
            for (int c = 0; c < 3; c++)
                if (x >= ColX[c] && x < ColX[c] + ColW[c]) return c;
            return -1;
        }

        private int RowAt(int col, int y)
        {
            if (col < 0 || y < BodyTop || y >= BodyBottom) return -1;
            if (col == 2 && Family != null && y >= BodyBottom - ChipsAreaH) return -1;
            int row = (y - BodyTop) / RowH + _scroll[col];
            return row >= 0 && row < CountOf(col) ? row : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int col = ColumnAt(e.X);
            int row = RowAt(col, e.Y);
            if (col != _hoverCol || row != _hoverRow)
            {
                _hoverCol = col;
                _hoverRow = row;
                Invalidate();
                if (col == 0 && row >= 0 && row != _cat)
                {
                    _hoverPendingRow = row;
                    _hoverTimer.Stop();
                    _hoverTimer.Start();
                }
                else
                {
                    _hoverTimer.Stop();
                    _hoverPendingRow = -1;
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverCol = _hoverRow = -1;
            _hoverPendingRow = -1;
            _hoverTimer.Stop();
            Invalidate();
        }

        private void OnHoverTick()
        {
            _hoverTimer.Stop();
            if (_hoverPendingRow >= 0 && _hoverCol == 0)
            {
                SelectCategory(_hoverPendingRow, true);
                _hoverPendingRow = -1;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (ResetRect().Contains(e.Location))
            {
                ClearAll();
                return;
            }

            int col = ColumnAt(e.X);
            if (col < 0) return;
            _focusCol = col;

            if (col == 2 && Family != null && e.Y >= BodyBottom - ChipsAreaH)
            {
                HandleChipClick(e.Location);
                return;
            }
            int row = RowAt(col, e.Y);
            if (row < 0) return;
            if (col == 0) SelectCategory(row, true);
            else if (col == 1) SelectSubCategory(row, true);
            else SelectFamily(row, true);
        }

        private void HandleChipClick(Point p)
        {
            int col = 2;
            int top = BodyBottom - ChipsAreaH;
            int x = ColX[col] + 8 + 34;
            int y = top + 8;
            for (int i = 0; i < _tiers.Count; i++)
            {
                if (new Rectangle(x, y, 32, 20).Contains(p)) { SetTier(_tiers[i], true); return; }
                x += 36;
            }
            x = ColX[col] + 8 + 34;
            y = top + 32;
            for (int i = 0; i < _enchants.Count; i++)
            {
                if (new Rectangle(x, y, 30, 20).Contains(p)) { SetEnchant(_enchants[i], true); return; }
                x += 34;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int col = ColumnAt(e.X);
            if (col < 0) return;
            _scroll[col] += e.Delta > 0 ? -1 : 1;
            ClampScroll(col);
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Up || keyData == Keys.Down
                || keyData == Keys.Left || keyData == Keys.Right
                || keyData == Keys.Escape || keyData == Keys.Enter)
                return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Left:
                    _focusCol = Math.Max(0, _focusCol - 1);
                    Invalidate();
                    e.Handled = true;
                    break;
                case Keys.Right:
                    _focusCol = Math.Min(2, _focusCol + 1);
                    Invalidate();
                    e.Handled = true;
                    break;
                case Keys.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Keys.Down:
                    MoveSelection(1);
                    e.Handled = true;
                    break;
                case Keys.Enter:
                    ActivateCurrent();
                    e.Handled = true;
                    break;
                case Keys.Escape:
                    EscapeOneLevel();
                    e.Handled = true;
                    break;
            }
        }

        private void EscapeOneLevel()
        {
            if (_fam >= 0)
            {
                _fam = -1;
                _tiers = new List<int>();
                _enchants = new List<int>();
                _tier = _enchant = 0;
                RaiseChanged();
            }
            else if (_sub >= 0)
            {
                _sub = -1;
                _fams = new List<ItemFamily>();
                RaiseChanged();
            }
            else if (_cat >= 0)
            {
                ClearAll();
            }
        }

        private void MoveSelection(int delta)
        {
            int count = CountOf(_focusCol);
            if (count == 0) return;
            if (_focusCol == 0)
            {
                int next = _cat < 0 ? 0 : Math.Max(0, Math.Min(count - 1, _cat + delta));
                SelectCategory(next, true);
            }
            else if (_focusCol == 1)
            {
                if (_cat < 0) return;
                int next = _sub < 0 ? 0 : Math.Max(0, Math.Min(count - 1, _sub + delta));
                SelectSubCategory(next, true);
            }
            else
            {
                if (_sub < 0) return;
                int next = _fam < 0 ? 0 : Math.Max(0, Math.Min(count - 1, _fam + delta));
                SelectFamily(next, true);
            }
            EnsureVisible();
        }

        private void ActivateCurrent()
        {
            if (_focusCol == 0 && _cat >= 0 && _subs.Count > 0) SelectSubCategory(0, true);
            else if (_focusCol == 1 && _sub >= 0 && _fams.Count > 0) SelectFamily(0, true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_hoverTimer != null) { _hoverTimer.Stop(); _hoverTimer.Dispose(); }
                _headFont.Dispose();
                _kickerFont.Dispose();
                _colHeadFont.Dispose();
                _rowFont.Dispose();
                _chipFont.Dispose();
                _pathFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
