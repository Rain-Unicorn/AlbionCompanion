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

        private List<string> _shownIds = new List<string>();
        private DateTime _lastRefresh;
        private bool _refreshing;
        private int _iconTicks;

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

            _status = CardText("本地库 " + _db.Rows.Count + " 条 · 点击查询后自动入库，之后每 5 分钟刷新",
                               14, 70, 540, 20, Th.Sub, 8f, false);

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

            // 选了具体三级物品，自动拿它当搜索词立即查询
            if (l3 != "全部" && l3 != null)
            {
                _items.Text = l3;
                _selL3 = "全部"; // 搜索词已经限定了物品，不再拿显示名二次过滤
                StartQuery();
                return;
            }
            // 选了二级子类（不是"全部"），用二级名当搜索词查询
            if (l2 != "全部" && l2 != null)
            {
                _items.Text = l2;
                _selL3 = "全部";
                StartQuery();
                return;
            }
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
                        shown = ItemCatalog.ResolveTerms(rawText, names, _cache, 20);
                    }

                    // 2. 先把本地库里现有的全部物品作为刷新范围；首次查询时也含新解析出的 id
                    var toRefresh = new List<string>(_db.KnownItemIds());
                    foreach (string id in shown)
                        if (!toRefresh.Contains(id)) toRefresh.Add(id);

                    // 3. 本地库先渲染（秒开）
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            // 搜索词有结果才限定；空结果（用户输入没匹配到）就显示空，不要兜底全部
                            if (rawText != null && rawText.Trim().Length > 0)
                                _shownIds = new List<string>(shown);
                            else
                                _shownIds = new List<string>(_db.KnownItemIds());
                            MergeNames(names);
                            RenderRows();
                            if (shown.Count == 0 && rawText != null && rawText.Trim().Length > 0)
                                SetStatus("没找到「" + rawText.Trim() + "」，试试 ID 如 T4_AXE 或换关键词", Th.Bad);
                        }));
                    }

                    // 4. 分批回源
                    var chunks = MarketDb.Chunk(toRefresh, 80);
                    int fetched = 0;
                    bool rateLimited = false;
                    foreach (var chunk in chunks)
                    {
                        if (IsDisposed) break;
                        try
                        {
                            var quotes = MarketClient.Fetch(chunk.ToArray(), cities.ToArray(), 25);
                            _db.UpsertBatch(quotes);
                            fetched += quotes.Count;
                            _lastRefresh = DateTime.Now;
                        }
                        catch (Exception ex)
                        {
                            if (ex.Message.IndexOf("429", StringComparison.OrdinalIgnoreCase) >= 0)
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
                                SetStatus("本地库 " + _db.Rows.Count + " 条 · 稍后自动重试", Th.Sub);
                            }
                            else
                            {
                                SetStatus("本地库 " + _db.Rows.Count + " 条 · 刚更新 " + fetched +
                                          " 行 · " + _lastRefresh.ToString("HH:mm:ss") + " · 5 分钟后自动刷新", Th.Good);
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

        private void RenderRows()
        {
            var cities = CheckedCities();
            var source = _items.Text.Trim().Length > 0
                ? _db.Query(_shownIds, cities)
                : _db.Query(_db.KnownItemIds(), cities);

            var rows = new List<string[]>();
            var images = new List<Image>();
            int shown = 0;
            for (int i = 0; i < source.Count; i++)
            {
                var q = source[i];
                if (!PassesFilter(q.ItemId, q.Quality)) continue;
                shown++;

                string display;
                if (!_cache.Map.TryGetValue(MarketDb.BaseOf(q.ItemId), out display) || string.IsNullOrEmpty(display))
                    display = q.ItemId;

                rows.Add(new string[]
                {
                    display,
                    q.City,
                    QualityLabel(q.Quality),
                    Th.Price(q.SellMin),
                    Th.Price(q.BuyMax),
                    Th.DateText(q.SellMinDate)
                });
                images.Add(ItemIcons.Get(MarketDb.BaseOf(q.ItemId), q.Quality));
            }
            _table.SetRows(rows, images);
        }

        private void RefreshRowIcons()
        {
            if (_db.Rows.Count == 0) { _iconTimer.Stop(); return; }
            RenderRows();
            if (++_iconTicks >= 6) _iconTimer.Stop();
        }

        public override void RefreshData()
        {
        }
    }
}
