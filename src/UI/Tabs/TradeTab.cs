using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AlbionCompanion.Core;
using AlbionCompanion.Core.Trade;
using AlbionCompanion.UI.Controls;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>
    /// 跑商页（规范 §2–§5 重建）：一屏三段递进 —— 差价榜 / 装载方案 / 路线导航。
    /// 与市场页边界（规范 §0）：跑商是决策页，必须含税、含负重、必须两城成一条腿；
    /// 数据只读 <see cref="Runtime.Db"/> 快照 + 本地物品元数据，**不新增任何回源**。
    /// 会员档切换（F2P 8% / Premium 4%）必须同时重算榜单纯利、装载合计、路线收益三处。
    /// </summary>
    public class TradeTab : TabBase
    {
        // ── 布局（内容区 620 宽；规范 §2：概要 112 / pill 30 / 路线档 30 / 内容 / 底栏 40）──
        private const int SummaryTop = 0;
        private const int SummaryHeight = 112;
        private const int ViewPillTop = 120;
        private const int ViewPillHeight = 30;
        private const int ModeChipTop = 158;
        private const int ModeChipHeight = 30;
        private const int ContentTop = 196;
        private const int BottomBarHeight = 44;

        private readonly TradePills _viewPills;
        private readonly TradePills _modePills;
        private readonly TradePills _memberPills;
        private readonly TradeBoardPanel _boardPanel;
        private readonly TradeLoadPanel _loadPanel;
        private readonly TradeNavPanel _navPanel;
        private readonly Timer _timer;
        private readonly Timer _saveTimer;

        private readonly Font _kicker = Th.F(7.5f);
        private readonly Font _title = Th.FB(15f);
        private readonly Font _big = Th.FB(20f);
        private readonly Font _hint = Th.F(8f);
        private readonly Font _metaFont = Th.F(8f);
        private readonly Font _bar = Th.F(8f);

        private readonly TradeSettings _settings;
        private readonly string _settingsPath;

        private ITradeItemMeta _meta;
        private TradeBoard _boardData;
        private LoadPlan _loadPlan;
        private TradeRouteMode _mode;
        private string _selectedDest = "";
        private TradeLeg _selectedLeg;
        private List<NavigationRoute> _candidates = new List<NavigationRoute>();
        private TradeSort _sort = TradeSort.PerKg;
        private int _view;
        private bool _weightDirty;

        public TradeTab(Runtime runtime)
            : base(runtime)
        {
            _settingsPath = TradeSettings.DefaultPath();
            _settings = TradeSettings.Load(_settingsPath);
            _mode = (TradeRouteMode)Math.Max(0, Math.Min(2, _settings.RouteMode));

            Card(0, SummaryTop, 620, SummaryHeight);

            _memberPills = new TradePills();
            _memberPills.SetItems(new[] { "F2P 8%", "Premium 4%" }, _settings.Premium ? 1 : 0);
            _memberPills.SelectedChanged += delegate { OnMemberChanged(); };
            Controls.Add(_memberPills);

            _viewPills = new TradePills();
            _viewPills.SetItems(new[] { "差价榜", "装载方案", "路线导航" }, 0);
            _viewPills.SelectedChanged += delegate { SetView(_viewPills.SelectedIndex); };
            Controls.Add(_viewPills);

            _modePills = new TradePills();
            _modePills.SetItems(new[] { "① 主城→黑市", "② 五王城互跑", "③ 布雷西里恩" }, (int)_mode);
            _modePills.SelectedChanged += delegate { OnModeChanged(_modePills.SelectedIndex); };
            Controls.Add(_modePills);

            _boardPanel = new TradeBoardPanel();
            _boardPanel.Sort = _sort;
            _boardPanel.LegClicked += SelectLeg;
            _boardPanel.SortRequested += delegate (TradeSort sort)
            {
                _sort = sort;
                _boardPanel.SetSort(sort);
                Invalidate();
            };
            _boardPanel.DestinationRequested += delegate (string city) { OnDestinationChanged(city); };
            Controls.Add(_boardPanel);

            _loadPanel = new TradeLoadPanel();
            _loadPanel.WeightChanged += OnWeightChanged;
            _loadPanel.GoBoardRequested += delegate { _viewPills.SetSelected(0, true); };
            Controls.Add(_loadPanel);

            _navPanel = new TradeNavPanel();
            _navPanel.RouteRequested += ActivateRoute;
            Controls.Add(_navPanel);

            NavigationState.Shared.Changed += OnNavigationChanged;

            _timer = new Timer();
            _timer.Interval = 60 * 1000;
            _timer.Tick += delegate { ComputeAll(); };
            _timer.Start();

            // 负重输入防抖落盘（不在每次键入都写文件）
            _saveTimer = new Timer();
            _saveTimer.Interval = 800;
            _saveTimer.Tick += delegate
            {
                _saveTimer.Stop();
                if (_weightDirty)
                {
                    _weightDirty = false;
                    _settings.Save(_settingsPath);
                }
            };

            LayoutContent();
            SetView(0);
            ComputeAll();
        }

        private void LayoutContent()
        {
            int width = Math.Max(480, Width);
            _memberPills.Bounds = new Rectangle(width - 186, 12, 170, 28);
            _viewPills.Bounds = new Rectangle(0, ViewPillTop, Math.Min(320, width - 180), ViewPillHeight);
            _modePills.Bounds = new Rectangle(0, ModeChipTop, Math.Min(520, width - 60), ModeChipHeight);

            int contentHeight = Math.Max(180, Height - ContentTop - BottomBarHeight);
            _boardPanel.Bounds = new Rectangle(0, ContentTop, width, contentHeight);
            _loadPanel.Bounds = new Rectangle(0, ContentTop, width, contentHeight);
            _navPanel.Bounds = new Rectangle(0, ContentTop, width, contentHeight);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutContent();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // ── 概要卡（规范 §2：kicker + 标题 + 会员档开关 + 大数字 + meta）──
            TextRenderer.DrawText(g, "TRADE ROUTER", _kicker, new Point(16, 10), Th.Sub,
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "跑商", _title, new Point(16, 24), Th.Text,
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "决策页：先看差价，再算装载，最后选路线", _hint,
                new Point(62, 32), Th.Sub, TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, BigValue(), _big, new Rectangle(16, 54, 380, 30), Th.Accent,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, BigHint(), _hint, new Rectangle(16, 88, 400, 18), Th.Sub,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            // 会员档说明 + 服务器标识（规范 §2：头部固定、三视图可见）
            string memberText = _settings.Premium ? "销售税 4% · 其余参数同 F2P" : "销售税 8% · 挂单费不进净利（吃单交易）";
            TextRenderer.DrawText(g, memberText, _metaFont, new Rectangle(414, 46, 190, 18), Th.Sub,
                TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            // 视图 pill 行右端：服务器 · 亚服
            TextRenderer.DrawText(g, TradeCities.ServerLabel(), _metaFont,
                new Rectangle(Math.Max(330, Width - 186), ViewPillTop + 7, 170, 18), Th.Sub,
                TextFormatFlags.Right | TextFormatFlags.NoPrefix);

            // ── 底部汇总条（规范 §2：最旧报价时间 + 口径提示，≥60 分钟转 warn）──
            int barY = Height - BottomBarHeight;
            using (var pen = new Pen(Th.Line)) g.DrawLine(pen, 0, barY, Width, barY);
            string oldest = _boardData == null || _boardData.OldestQuoteAgeMinutes < 0
                ? "最旧报价：无有效时间戳"
                : "最旧报价 " + _boardData.OldestQuoteAgeMinutes + " 分前";
            bool warn = _boardData != null && _boardData.OldestQuoteAgeMinutes >= 60;
            TextRenderer.DrawText(g, oldest, _bar,
                new Rectangle(0, barY + 8, 200, 18), warn ? Th.Accent : Th.Sub,
                TextFormatFlags.NoPrefix);
            string caliber = "口径：单件净利 = 卖入×(1−" + (int)Math.Round(_settings.SellTax * 100)
                             + "%) − 吃单 · 0 报价=无上报 · 只读本地库";
            TextRenderer.DrawText(g, caliber, _bar,
                new Rectangle(206, barY + 8, Math.Max(80, Width - 210), 18), Th.Sub,
                TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        private string BigValue()
        {
            if (_view == 1 && _settings.WeightLimitKg > 0d && _loadPlan != null)
                return TradeFormat.Silver(_loadPlan.TotalProfit) + " 银";
            if (_view == 2 && NavigationState.Shared.IsActive)
                return TradeFormat.Silver(NavigationState.Shared.EstimatedProfit) + " 银";
            if (_boardData != null)
            {
                var top = _boardData.PositiveSorted(TradeSort.PerKg, _settings.WeightLimitKg);
                if (top.Count > 0)
                {
                    var best = top[0];
                    if (best.PerKg.HasValue)
                        return TradeFormat.Silver((long)Math.Round(best.PerKg.Value)) + " 银/kg";
                    return TradeFormat.SilverSigned(best.NetPerUnit) + " 银/件";
                }
            }
            return "—";
        }

        private string BigHint()
        {
            switch (_view)
            {
                case 1:
                    if (_settings.WeightLimitKg <= 0d) return "先填最大负重，再算这一趟装什么、装多少";
                    if (_loadPlan == null) return "装载方案";
                    return "装载合计 · 已用 " + TradeFormat.Kg(_loadPlan.UsedKg) + " / "
                           + TradeFormat.Kg(_loadPlan.WeightLimitKg) + " KG · "
                           + _loadPlan.SkuCount + " SKU / " + _loadPlan.TotalPieces + " 件";
                case 2:
                    return NavigationState.Shared.IsActive
                        ? "剩余预估收益 · " + NavigationState.Shared.ProgressText
                        : "从差价榜选一条腿，再选候选路线开始导航";
                default:
                    return "最高每 kg 净利 · 本地行情库（只读，不新增回源）";
            }
        }

        // ── 数据计算 ─────────────────────────────────────────────────────

        private List<MarketQuote> SnapshotDb()
        {
            var list = new List<MarketQuote>();
            try
            {
                // MarketDb.Rows 已是线程安全副本快照（dev-market 已加锁）；这里再拷一层防止调用方误改
                var rows = Rt.Db != null ? Rt.Db.Rows : null;
                if (rows != null)
                {
                    for (int i = 0; i < rows.Count; i++)
                        if (rows[i] != null) list.Add(rows[i]);
                }
            }
            catch
            {
                // 极端并发下读失败：返回空快照，UI 显示空态而不是崩溃
            }
            return list;
        }

        /// <summary>三处统一重算入口（会员档/路线档/目的城/负重/定时刷新都走这里）。</summary>
        private void ComputeAll()
        {
            _meta = new TradeItemMeta(ItemIndex.Default);
            var rows = SnapshotDb();
            double tax = _settings.SellTax;
            var now = DateTimeOffset.UtcNow;

            if (_mode == TradeRouteMode.RoyalLoop)
            {
                if (!IsRoyalCity(_selectedDest))
                {
                    string remembered = _settings.LastDestination;
                    _selectedDest = IsRoyalCity(remembered)
                        ? remembered
                        : (TradeEngine.FindBestDestination(rows, tax, now) ?? "");
                }
            }
            else
            {
                _selectedDest = "";
            }

            _boardData = TradeEngine.Build(rows, _mode, _selectedDest, _meta,
                NullDepthProvider.Instance, tax, now);

            _boardPanel.NameOf = NameOf;
            _boardPanel.WeightLimitKg = _settings.WeightLimitKg;
            _boardPanel.SetMode(_mode, _selectedDest);
            _boardPanel.SetWeight(_settings.WeightLimitKg);
            _boardPanel.SetData(_boardData);

            _loadPlan = TradeEngine.BuildLoad(_boardData.Legs, _settings.WeightLimitKg);
            _loadPanel.NameOf = NameOf;
            _loadPanel.SetPlan(_loadPlan, _settings.WeightLimitKg > 0d);
            _loadPanel.SetWeightText(_settings.WeightLimitKg > 0d
                ? TradeFormat.Kg(_settings.WeightLimitKg) : "");

            if (_selectedLeg != null)
                _selectedLeg = FindLeg(_selectedLeg);
            if (_selectedLeg == null)
            {
                _candidates = new List<NavigationRoute>();
                if (NavigationState.Shared.IsActive) NavigationState.Shared.Deactivate();
            }
            else if (_candidates.Count == 0)
            {
                _candidates = TradeRouteCatalog.Candidates(_selectedLeg.OriginCity, _selectedLeg.DestCity);
            }
            _navPanel.SetLeg(_selectedLeg, _candidates, _settings.WeightLimitKg);

            UpdateNavigationEstimates();
            Invalidate();
        }

        private TradeLeg FindLeg(TradeLeg previous)
        {
            if (previous == null || _boardData == null) return null;
            for (int i = 0; i < _boardData.Legs.Count; i++)
            {
                var leg = _boardData.Legs[i];
                if (leg.ItemId == previous.ItemId && leg.Quality == previous.Quality
                    && leg.OriginCity == previous.OriginCity && leg.DestCity == previous.DestCity)
                    return leg;
            }
            return null;
        }

        private void UpdateNavigationEstimates()
        {
            if (!NavigationState.Shared.IsActive) return;
            long profit = 0L;
            int pieces = 0;
            double weight = 0d;
            if (_selectedLeg != null)
            {
                pieces = (int)TradeEngine.PiecesFor(_selectedLeg, _settings.WeightLimitKg);
                profit = _selectedLeg.NetPerUnit * pieces;
                if (_selectedLeg.WeightKg.HasValue) weight = pieces * _selectedLeg.WeightKg.Value;
            }
            NavigationState.Shared.UpdateEstimates(profit, pieces, weight);
        }

        private string NameOf(string itemId)
        {
            if (_meta != null)
            {
                string name = _meta.DisplayNameOf(itemId);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            return itemId ?? "";
        }

        private static bool IsRoyalCity(string city)
        {
            return !string.IsNullOrEmpty(city) && Array.IndexOf(TradeCities.Royal, city) >= 0;
        }

        // ── 交互 ─────────────────────────────────────────────────────────

        private void SetView(int index)
        {
            _view = Math.Max(0, Math.Min(2, index));
            _boardPanel.Visible = _view == 0;
            _loadPanel.Visible = _view == 1;
            _navPanel.Visible = _view == 2;
            if (_viewPills.SelectedIndex != _view) _viewPills.SetSelected(_view, false);
            Invalidate();
        }

        private void OnModeChanged(int index)
        {
            _mode = (TradeRouteMode)Math.Max(0, Math.Min(2, index));
            _settings.RouteMode = (int)_mode;
            _selectedDest = "";
            _selectedLeg = null;
            _candidates = new List<NavigationRoute>();
            if (NavigationState.Shared.IsActive) NavigationState.Shared.Deactivate();
            _settings.Save(_settingsPath);
            ComputeAll();
        }

        private void OnDestinationChanged(string city)
        {
            if (!IsRoyalCity(city)) return;
            _selectedDest = city;
            _settings.LastDestination = city;
            _settings.Save(_settingsPath);
            ComputeAll();
        }

        private void OnMemberChanged()
        {
            _settings.Premium = _memberPills.SelectedIndex == 1;
            _settings.Save(_settingsPath);
            // 规范 §6 S2：会员档切换必须重算三处（榜单纯利 / 装载合计 / 路线收益）
            ComputeAll();
        }

        private void OnWeightChanged(string text)
        {
            double kg = TradeSettings.ParseWeightText(text);
            _settings.WeightLimitKg = kg;
            _weightDirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();

            // 装载方案 + 榜单单趟列 + 路线收益三处同步（单趟列依赖负重）
            if (_boardData != null)
            {
                _loadPlan = TradeEngine.BuildLoad(_boardData.Legs, kg);
                _loadPanel.SetPlan(_loadPlan, kg > 0d);
                _boardPanel.SetWeight(kg);
            }
            UpdateNavigationEstimates();
            Invalidate();
        }

        private void SelectLeg(TradeLeg leg)
        {
            if (leg == null) return;
            _selectedLeg = leg;
            _candidates = TradeRouteCatalog.Candidates(leg.OriginCity, leg.DestCity);
            _navPanel.SetLeg(leg, _candidates, _settings.WeightLimitKg);
            _viewPills.SetSelected(2, true);
        }

        private void ActivateRoute(NavigationRoute route)
        {
            if (route == null) return;
            int pieces = _selectedLeg != null
                ? (int)TradeEngine.PiecesFor(_selectedLeg, _settings.WeightLimitKg) : 0;
            long profit = _selectedLeg != null ? _selectedLeg.NetPerUnit * pieces : 0L;
            double weight = _selectedLeg != null && _selectedLeg.WeightKg.HasValue
                ? pieces * _selectedLeg.WeightKg.Value : 0d;
            NavigationState.Shared.Activate(route, profit, pieces, weight);
        }

        private void OnNavigationChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            _navPanel.Invalidate();
            Invalidate();
        }

        public override void RefreshData()
        {
            ComputeAll();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { NavigationState.Shared.Changed -= OnNavigationChanged; } catch (ObjectDisposedException) { }
                _timer.Stop();
                _timer.Dispose();
                _saveTimer.Stop();
                _saveTimer.Dispose();
                _kicker.Dispose();
                _title.Dispose();
                _big.Dispose();
                _hint.Dispose();
                _metaFont.Dispose();
                _bar.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
