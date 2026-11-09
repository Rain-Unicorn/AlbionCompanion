#nullable disable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AlbionCompanion.Core;
using AlbionCompanion.Core.Trade;

namespace AlbionCompanion.UI.Controls
{
    /// <summary>跑商页专用配色（规范 §1：唯一的两个方向色通道，不借色）。</summary>
    internal static class TradeColors
    {
        /// <summary>cost 买侧：吃单成本 / 进货支出（#e0954a，对比度 7.49:1）。</summary>
        public static readonly Color Cost = Color.FromArgb(0xE0, 0x95, 0x4A);

        /// <summary>income 卖侧：卖入所得（#4fc3c7，对比度 8.71:1）。</summary>
        public static readonly Color Income = Color.FromArgb(0x4F, 0xC3, 0xC7);

        /// <summary>红区底（GDI+ 无 alpha，预计算成实色）。</summary>
        public static readonly Color RedZoneBg = Color.FromArgb(0x28, 0x1C, 0x1E);

        /// <summary>warn 芯片底（12% 压成实色）。</summary>
        public static readonly Color WarnBg = Color.FromArgb(0x2A, 0x24, 0x19);

        public static readonly Color RowBg = Color.FromArgb(0x17, 0x1A, 0x20);
        public static readonly Color RowAlt = Color.FromArgb(0x1B, 0x1F, 0x26);
        public static readonly Color RowHover = Color.FromArgb(0x22, 0x27, 0x30);

        public static Color RiskColor(string riskLevel)
        {
            if (riskLevel == "高危") return Th.Bad;
            if (riskLevel == "中危") return Th.Accent;
            return Th.Good;
        }
    }

    /// <summary>跑商页的格式化助手（Consolas 只用于数值，中文名一律雅黑，规范 §8.4）。</summary>
    internal static class TradeFormat
    {
        public static readonly Font NumFont = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);

        public static string Silver(long value)
        {
            return value.ToString("N0");
        }

        public static string SilverSigned(long value)
        {
            if (value > 0) return "+" + value.ToString("N0");
            return value.ToString("N0");
        }

        public static string Kg(double value)
        {
            return value.ToString("0.#");
        }

        public static string TierChip(string itemId)
        {
            int tier = ItemCatalog.TierOf(itemId);
            return tier > 0 ? "T" + tier : "";
        }

        /// <summary>品质 1..5 = 普通/良好/杰出/精良/大师（与市场页修正后的口径一致）。</summary>
        public static string QualityName(int quality)
        {
            switch (quality)
            {
                case 1: return "普通";
                case 2: return "良好";
                case 3: return "杰出";
                case 4: return "精良";
                case 5: return "大师";
                default: return "";
            }
        }

        public static string ItemLabel(string itemId, string displayName)
        {
            if (!string.IsNullOrEmpty(displayName)) return displayName;
            return itemId ?? "";
        }
    }

    /// <summary>
    /// 通用分段 pill（视图切换 / 排序 / 路线档 / 会员档 / 快捷负重）。
    /// 自绘圆角药丸，选中用品牌金实底，未选中用 ink 卡底 + 1px 描边。
    /// </summary>
    public class TradePills : Control
    {
        public string[] Items = new string[0];
        public int SelectedIndex;
        public int Gap = 6;
        public bool UseAccent = true;

        public event EventHandler SelectedChanged;

        public TradePills()
        {
            DoubleBuffered = true;
            BackColor = Th.Bg;
            Height = 30;
            Font = Th.F(9f);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        }

        public void SetItems(string[] items, int selected)
        {
            Items = items ?? new string[0];
            if (selected < 0 || selected >= Items.Length) selected = 0;
            SelectedIndex = selected;
            Invalidate();
        }

        public void SetSelected(int index, bool raise)
        {
            if (index < 0 || index >= Items.Length) return;
            if (SelectedIndex == index) return;
            SelectedIndex = index;
            Invalidate();
            if (raise && SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            if (Items.Length == 0) return;

            int total = 0;
            for (int i = 0; i < Items.Length; i++) total += Items[i].Length;
            int x = 0;
            int available = Width - Gap * (Items.Length - 1);
            for (int i = 0; i < Items.Length; i++)
            {
                int w = total > 0
                    ? Math.Max(44, (int)(available * ((double)Items[i].Length / total)))
                    : available / Items.Length;
                if (i == Items.Length - 1) w = Width - x;
                var rect = new Rectangle(x, 0, Math.Max(20, w), Height - 1);
                bool selected = i == SelectedIndex;
                if (selected)
                {
                    Th.FillRound(g, rect, 7, UseAccent ? Th.Accent : Th.CardHi);
                    if (!UseAccent) Th.StrokeRound(g, rect, 7, Th.Accent);
                }
                else
                {
                    using (var br = new SolidBrush(Th.CardHi)) g.FillRectangle(br, rect);
                    Th.StrokeRound(g, rect, 7, Th.Line);
                }
                TextRenderer.DrawText(g, Items[i], Font, rect,
                    selected ? Color.FromArgb(26, 21, 10) : Th.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                x += w + Gap;
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (Items.Length == 0) return;
            int total = 0;
            for (int i = 0; i < Items.Length; i++) total += Items[i].Length;
            int x = 0;
            int available = Width - Gap * (Items.Length - 1);
            for (int i = 0; i < Items.Length; i++)
            {
                int w = total > 0
                    ? Math.Max(44, (int)(available * ((double)Items[i].Length / total)))
                    : available / Items.Length;
                if (i == Items.Length - 1) w = Width - x;
                if (e.X >= x && e.X <= x + w)
                {
                    SetSelected(i, true);
                    return;
                }
                x += w + Gap;
            }
        }
    }

    /// <summary>
    /// 差价榜（规范 §3）：排序 pill + 8 列表头 + 两段式行 + 负利润折叠计数 + 缺上报数据单列区。
    /// 0 报价由计算层过滤后进 Missing，本控件永不画 0 银。
    /// </summary>
    public class TradeBoardPanel : Control
    {
        private const int RowHeight = 40;
        private const int Pad = 16;
        private const int MissingAreaHeight = 92;

        private static readonly string[] HeadersNormal =
            { "物品", "出发城", "吃单成本", "卖入所得", "单件净利", "每kg", "深度", "单趟" };
        private static readonly string[] HeadersLoop =
            { "物品", "路线", "吃单成本", "卖入所得", "单件净利", "每kg", "深度", "单趟" };
        private static readonly int[] ColumnWidths = { 132, 86, 62, 62, 62, 58, 46, 80 };

        public TradeBoard Board;
        public TradeRouteMode Mode = TradeRouteMode.BlackMarket;
        public TradeSort Sort = TradeSort.PerKg;
        public double WeightLimitKg;
        public string SelectedDest = "";
        public bool ShowNegative;
        public Func<string, string> NameOf;

        public event Action<TradeLeg> LegClicked;
        public event Action<TradeSort> SortRequested;
        public event Action<string> DestinationRequested;

        private readonly List<TradeLeg> _display = new List<TradeLeg>();
        private readonly List<Rectangle> _rowRects = new List<Rectangle>();
        private readonly List<TradeLeg> _rowLegs = new List<TradeLeg>();
        private readonly List<Rectangle> _sortRects = new List<Rectangle>();
        private readonly List<Rectangle> _destRects = new List<Rectangle>();
        private Rectangle _foldRect;
        private int _scrollY;
        private int _maxScroll;
        private int _rowsTop;
        private int _rowsBottom;
        private int _tableHeaderY;

        private readonly Font _kicker = Th.F(7.5f);
        private readonly Font _title = Th.FB(13f);
        private readonly Font _head = Th.F(8f);
        private readonly Font _rowFont = Th.F(9f);
        private readonly Font _small = Th.F(8f);
        private readonly Font _chip = Th.F(7.5f);

        public TradeBoardPanel()
        {
            DoubleBuffered = true;
            BackColor = Th.Bg;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        }

        public void SetData(TradeBoard board)
        {
            Board = board;
            _scrollY = 0;
            RebuildDisplay();
            Invalidate();
        }

        public void SetSort(TradeSort sort)
        {
            Sort = sort;
            _scrollY = 0;
            RebuildDisplay();
            Invalidate();
        }

        public void SetWeight(double weightLimitKg)
        {
            WeightLimitKg = weightLimitKg;
            RebuildDisplay();
        }

        public void SetMode(TradeRouteMode mode, string selectedDest)
        {
            Mode = mode;
            SelectedDest = selectedDest ?? "";
            RebuildDisplay();
            Invalidate();
        }

        private void RebuildDisplay()
        {
            _display.Clear();
            if (Board == null)
            {
                _maxScroll = 0;
                return;
            }
            var positives = Board.PositiveSorted(Sort, WeightLimitKg);
            _display.AddRange(positives);
            if (ShowNegative) _display.AddRange(Board.NegativeSorted());

            int headerOffset = Mode == TradeRouteMode.RoyalLoop ? 34 : 0;
            _tableHeaderY = 52 + headerOffset;
            _rowsTop = _tableHeaderY + 22;
            int missingArea = Board.MissingTotal > 0 ? MissingAreaHeight : 0;
            int foldReserve = Board.NegativeCount > 0 ? RowHeight : 0; // 折叠行固定占位，避免压到缺报区
            _rowsBottom = Height - Pad - missingArea - foldReserve;
            int contentHeight = _display.Count * RowHeight;
            int visible = Math.Max(0, _rowsBottom - _rowsTop);
            _maxScroll = Math.Max(0, contentHeight - visible);
            if (_scrollY > _maxScroll) _scrollY = _maxScroll;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RebuildDisplay();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scrollY = Math.Max(0, Math.Min(_maxScroll, _scrollY - e.Delta / 2));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Th.Bg);
            Th.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), 10, Th.Card);
            Th.StrokeRound(g, new Rectangle(0, 0, Width - 1, Height - 1), 10, Th.Line);

            TextRenderer.DrawText(g, "SPREAD BOARD", _kicker, new Point(Pad, 10), Th.Sub,
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "差价榜", _title, new Point(Pad, 24), Th.Text,
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "按每 kg 净利降序，排序只改顺序、不改筛选", _small,
                new Point(Pad + 68, 29), Th.Sub, TextFormatFlags.NoPrefix);

            DrawSortPills(g);
            if (Mode == TradeRouteMode.RoyalLoop) DrawDestinationPills(g);
            DrawTableHeader(g);
            DrawRows(g);
            DrawMissingSection(g);
        }

        private void DrawSortPills(Graphics g)
        {
            _sortRects.Clear();
            string[] labels = { "每kg净利", "单件净利", "单趟利润" };
            int w = 76;
            int x = Width - Pad - (labels.Length * w + (labels.Length - 1) * 6);
            for (int i = 0; i < labels.Length; i++)
            {
                var rect = new Rectangle(x + i * (w + 6), 16, w, 26);
                _sortRects.Add(rect);
                bool selected = (int)Sort == i;
                if (selected) Th.FillRound(g, rect, 7, Th.Accent);
                else
                {
                    using (var br = new SolidBrush(Th.CardHi)) g.FillRectangle(br, rect);
                    Th.StrokeRound(g, rect, 7, Th.Line);
                }
                TextRenderer.DrawText(g, labels[i], _chip, rect,
                    selected ? Color.FromArgb(26, 21, 10) : Th.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPrefix);
            }
        }

        private void DrawDestinationPills(Graphics g)
        {
            _destRects.Clear();
            TextRenderer.DrawText(g, "目的城", _small, new Point(Pad, 62), Th.Sub,
                TextFormatFlags.NoPrefix);
            int x = Pad + 52;
            for (int i = 0; i < TradeCities.Royal.Length; i++)
            {
                string city = TradeCities.Royal[i];
                string label = TradeCities.Zh(city);
                var rect = new Rectangle(x, 58, 60, 24);
                _destRects.Add(rect);
                bool selected = string.Equals(city, SelectedDest, StringComparison.OrdinalIgnoreCase);
                if (selected) Th.FillRound(g, rect, 6, Th.CardHi);
                Th.StrokeRound(g, rect, 6, selected ? Th.Accent : Th.Line);
                TextRenderer.DrawText(g, label, _chip, rect, selected ? Th.Accent : Th.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPrefix);
                x += 66;
            }
        }

        private void DrawTableHeader(Graphics g)
        {
            string[] headers = Mode == TradeRouteMode.RoyalLoop ? HeadersLoop : HeadersNormal;
            int x = Pad;
            for (int i = 0; i < headers.Length; i++)
            {
                var rect = new Rectangle(x, _tableHeaderY, ColumnWidths[i], 20);
                TextRenderer.DrawText(g, headers[i], _head, rect, Th.Sub,
                    (i >= 2 ? TextFormatFlags.Right : TextFormatFlags.Left)
                    | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                x += ColumnWidths[i];
            }
            using (var pen = new Pen(Th.Line))
                g.DrawLine(pen, Pad, _tableHeaderY + 20, Width - Pad, _tableHeaderY + 20);
        }

        private void DrawRows(Graphics g)
        {
            _rowRects.Clear();
            _rowLegs.Clear();
            int top = _rowsTop;
            int bottom = _rowsBottom;
            if (Board == null)
            {
                DrawEmpty(g, "本地库还没有行情数据", "先去市场页查询几个物品，跑商页会自动算出可跑的腿");
                return;
            }
            if (_display.Count == 0 && Board.NegativeCount == 0)
            {
                if (Board.MissingTotal > 0)
                    DrawEmpty(g, "没有可计算的腿", "当前档位的报价全为 0/过期，见下方「缺上报数据」");
                else
                    DrawEmpty(g, "本地库还没有行情数据", "先去市场页查询物品（含黑市城市），跑商页会自动算差价");
                return;
            }

            g.SetClip(new Rectangle(Pad, top, Width - Pad * 2, Math.Max(0, bottom - top)));
            for (int i = 0; i < _display.Count; i++)
            {
                int y = top + i * RowHeight - _scrollY;
                if (y + RowHeight < top || y > bottom) continue;
                DrawRow(g, _display[i], i, y, false);
            }
            g.ResetClip();

            if (Board.NegativeCount > 0)
            {
                // 折叠行固定在行区下沿（已预留 RowHeight），不随滚动、不压缺报区
                int y = _rowsBottom;
                var rect = new Rectangle(Pad, y, Width - Pad * 2, RowHeight);
                _foldRect = rect;
                bool hover = rect.Contains(PointToClient(Cursor.Position));
                using (var br = new SolidBrush(hover ? TradeColors.RowHover : TradeColors.RowAlt))
                    g.FillRectangle(br, rect);
                string text = ShowNegative
                    ? "▾ 收起 " + Board.NegativeCount + " 条税后为负的腿"
                    : "▸ " + Board.NegativeCount + " 条腿税后为负 · 已折叠（点击展开）";
                TextRenderer.DrawText(g, text, _small,
                    new Rectangle(rect.X + 12, rect.Y, rect.Width - 24, rect.Height),
                    Th.Accent,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private void DrawRow(Graphics g, TradeLeg leg, int index, int y, bool negative)
        {
            var rect = new Rectangle(Pad, y, Width - Pad * 2, RowHeight);
            _rowRects.Add(rect);
            _rowLegs.Add(leg);

            bool hover = rect.Contains(PointToClient(Cursor.Position));
            using (var br = new SolidBrush(hover ? TradeColors.RowHover : (index % 2 == 0 ? TradeColors.RowBg : TradeColors.RowAlt)))
                g.FillRectangle(br, rect);
            using (var strip = new SolidBrush(leg.NetPerUnit >= 0 ? Th.Good : Th.Bad))
                g.FillRectangle(strip, new Rectangle(rect.X, rect.Y + 8, 3, RowHeight - 16));

            int x = rect.X + 10;
            // 上段：T 阶芯片 + 物品名 + 出发城/路线
            string tier = TradeFormat.TierChip(leg.ItemId);
            if (tier.Length > 0)
            {
                var chipRect = new Rectangle(x, y + 6, 30, 16);
                using (var br = new SolidBrush(Th.CardHi)) g.FillRectangle(br, chipRect);
                TextRenderer.DrawText(g, tier, _chip, chipRect, Th.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                x += 36;
            }
            string name = TradeFormat.ItemLabel(leg.ItemId, leg.DisplayName);
            string quality = leg.Quality > 1 ? " · " + TradeFormat.QualityName(leg.Quality) : "";
            TextRenderer.DrawText(g, name + quality, _rowFont,
                new Rectangle(x, y + 2, ColumnWidths[0] - (x - rect.X) + 20, 20), Th.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                | TextFormatFlags.EndEllipsis);

            int colOriginX = rect.X + ColumnWidths[0];
            string routeText = Mode == TradeRouteMode.RoyalLoop
                ? TradeCities.Zh(leg.OriginCity) + "→" + TradeCities.Zh(leg.DestCity)
                : TradeCities.Zh(leg.OriginCity);
            TextRenderer.DrawText(g, routeText, _small,
                new Rectangle(colOriginX, y + 2, ColumnWidths[1], 20), Th.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                | TextFormatFlags.EndEllipsis);

            // 下段：数值列（cost 橙 / income 青 / 净利 heal-dmg / 每kg / 深度 / 单趟）
            int colX = rect.X + ColumnWidths[0] + ColumnWidths[1];
            DrawRight(g, TradeFormat.Silver(leg.BuyUnit), colX, y + 20, ColumnWidths[2], TradeColors.Cost);
            colX += ColumnWidths[2];
            DrawRight(g, TradeFormat.Silver(leg.SellNet), colX, y + 20, ColumnWidths[3], TradeColors.Income);
            colX += ColumnWidths[3];
            DrawRight(g, TradeFormat.SilverSigned(leg.NetPerUnit), colX, y + 20, ColumnWidths[4],
                leg.NetPerUnit >= 0 ? Th.Good : Th.Bad);
            colX += ColumnWidths[4];
            DrawRight(g, leg.PerKg.HasValue ? TradeFormat.SilverSigned((long)Math.Round(leg.PerKg.Value)) : "—",
                colX, y + 20, ColumnWidths[5], leg.PerKg.HasValue ? Th.Text : Th.Sub);
            colX += ColumnWidths[5];
            DrawRight(g, leg.DepthKnown ? leg.Depth.ToString("N0") : "—",
                colX, y + 20, ColumnWidths[6], leg.DepthKnown ? Th.Text : Th.Sub);
            colX += ColumnWidths[6];
            // 深度未知时单趟不可算（不拿负重件数冒充承接量），显示 —；深度已知才给 net×件数
            long tripProfit = (leg.DepthKnown && WeightLimitKg > 0d)
                ? leg.NetPerUnit * TradeEngine.PiecesFor(leg, WeightLimitKg) : 0L;
            DrawRight(g, tripProfit > 0L ? TradeFormat.Silver(tripProfit) : "—",
                colX, y + 20, ColumnWidths[7], tripProfit > 0L ? Th.Accent : Th.Sub);
        }

        private void DrawRight(Graphics g, string text, int x, int y, int w, Color color)
        {
            TextRenderer.DrawText(g, text, TradeFormat.NumFont,
                new Rectangle(x, y, w, 18), color,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private void DrawEmpty(Graphics g, string title, string detail)
        {
            int cy = (_rowsTop + _rowsBottom) / 2 - 16;
            TextRenderer.DrawText(g, title, Th.FB(10.5f),
                new Rectangle(Pad, cy, Width - Pad * 2, 22), Th.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, detail, _small,
                new Rectangle(Pad, cy + 26, Width - Pad * 2, 18), Th.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }

        private void DrawMissingSection(Graphics g)
        {
            if (Board == null || Board.MissingTotal == 0) return;
            int y0 = Height - Pad - MissingAreaHeight + 4;
            using (var pen = new Pen(Th.Line)) g.DrawLine(pen, Pad, y0, Width - Pad, y0);

            var chipRect = new Rectangle(Pad, y0 + 8, 120, 20);
            using (var br = new SolidBrush(TradeColors.WarnBg)) g.FillRectangle(br, chipRect);
            TextRenderer.DrawText(g, "缺上报数据（" + Board.MissingTotal + "）", _chip, chipRect, Th.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "0 报价不是 0 银，不参与排序与负利润折叠", _small,
                new Point(Pad + 130, y0 + 10), Th.Sub, TextFormatFlags.NoPrefix);

            int lines = 0;
            for (int i = 0; i < Board.Missing.Count && lines < 2; i++, lines++)
            {
                var m = Board.Missing[i];
                string name = NameOf != null ? NameOf(m.ItemId) : m.ItemId;
                string text = TradeFormat.ItemLabel(m.ItemId, name) + " · " + TradeCities.Zh(m.OriginCity)
                              + " → " + TradeCities.Zh(m.DestCity) + " · " + m.ReasonText;
                TextRenderer.DrawText(g, text, _small,
                    new Rectangle(Pad, y0 + 32 + lines * 20, Width - Pad * 2, 18), Th.Sub,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            if (Board.MissingTotal > Board.Missing.Count)
            {
                TextRenderer.DrawText(g, "…另有 " + (Board.MissingTotal - Board.Missing.Count) + " 条未列出",
                    _small, new Point(Pad, y0 + 72), Th.Sub, TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int i = 0; i < _sortRects.Count; i++)
            {
                if (_sortRects[i].Contains(e.Location))
                {
                    var sort = (TradeSort)i;
                    if (SortRequested != null) SortRequested(sort);
                    return;
                }
            }
            for (int i = 0; i < _destRects.Count; i++)
            {
                if (_destRects[i].Contains(e.Location))
                {
                    if (DestinationRequested != null) DestinationRequested(TradeCities.Royal[i]);
                    return;
                }
            }
            if (Board != null && Board.NegativeCount > 0 && _foldRect.Contains(e.Location))
            {
                ShowNegative = !ShowNegative;
                RebuildDisplay();
                Invalidate();
                return;
            }
            for (int i = 0; i < _rowRects.Count; i++)
            {
                if (_rowRects[i].Contains(e.Location))
                {
                    if (LegClicked != null) LegClicked(_rowLegs[i]);
                    return;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _kicker.Dispose();
                _title.Dispose();
                _head.Dispose();
                _rowFont.Dispose();
                _small.Dispose();
                _chip.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 装载方案（规范 §4）：负重输入 + 40/150/200 chip + 记忆；未填走空态（绝不编默认值）；
    /// 贪心装填结果 + 已用/合计/密度汇总条 + 负重/深度两种受限分开文案 + ≥95% warn。
    /// </summary>
    public class TradeLoadPanel : Control
    {
        private const int RowHeight = 30;
        private const int Pad = 16;

        private readonly TextBox _box;
        private readonly Font _numFont = new Font("Consolas", 10.5f);
        private readonly Font _title = Th.FB(13f);
        private readonly Font _kicker = Th.F(7.5f);
        private readonly Font _small = Th.F(8f);
        private readonly Font _head = Th.F(8f);
        private readonly Font _rowFont = Th.F(9f);
        private readonly Font _big = Th.FB(12f);

        private readonly List<Rectangle> _chipRects = new List<Rectangle>();
        private Rectangle _goBoardRect;
        private bool _suppress;

        public LoadPlan Plan;
        public bool WeightSet;
        public string WeightText = "";
        public Func<string, string> NameOf;

        public event Action<string> WeightChanged;
        public event Action GoBoardRequested;

        public TradeLoadPanel()
        {
            DoubleBuffered = true;
            BackColor = Th.Bg;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);

            _box = new TextBox();
            _box.BorderStyle = BorderStyle.FixedSingle;
            _box.BackColor = Th.CardHi;
            _box.ForeColor = Th.Text;
            _box.Font = _numFont;
            _box.Location = new Point(96, 50);
            _box.Size = new Size(96, 28);
            _box.TextChanged += delegate
            {
                if (_suppress) return;
                if (WeightChanged != null) WeightChanged(_box.Text);
            };
            Controls.Add(_box);
        }

        /// <summary>程序化回填负重（不触发 WeightChanged，不抢焦点/光标）。</summary>
        public void SetWeightText(string text)
        {
            _suppress = true;
            if (_box.Text != (text ?? "")) _box.Text = text ?? "";
            _suppress = false;
            WeightText = text ?? "";
        }

        public void SetPlan(LoadPlan plan, bool weightSet)
        {
            Plan = plan;
            WeightSet = weightSet;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Th.Bg);
            Th.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), 10, Th.Card);
            Th.StrokeRound(g, new Rectangle(0, 0, Width - 1, Height - 1), 10, Th.Line);

            TextRenderer.DrawText(g, "LOAD PLAN", _kicker, new Point(Pad, 10), Th.Sub,
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "装载方案", _title, new Point(Pad, 24), Th.Text,
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "负重上限", _small, new Point(Pad, 58), Th.Sub,
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "KG", _small, new Point(198, 58), Th.Sub, TextFormatFlags.NoPrefix);

            // chip：40 / 150 / 200（快捷填充；记忆上次输入由 TradeTab 落盘）
            _chipRects.Clear();
            int[] presets = { 40, 150, 200 };
            int cx = 232;
            for (int i = 0; i < presets.Length; i++)
            {
                var rect = new Rectangle(cx, 50, 50, 28);
                _chipRects.Add(rect);
                bool selected = WeightSet && Math.Abs(EvalWeight(_box.Text) - presets[i]) < 0.001;
                if (selected) Th.FillRound(g, rect, 6, Th.CardHi);
                Th.StrokeRound(g, rect, 6, selected ? Th.Accent : Th.Line);
                TextRenderer.DrawText(g, presets[i] + "", _small, rect,
                    selected ? Th.Accent : Th.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                cx += 56;
            }
            TextRenderer.DrawText(g, "记忆上次输入", _small, new Point(cx + 4, 58), Th.Sub,
                TextFormatFlags.NoPrefix);

            if (!WeightSet)
            {
                DrawEmptyState(g);
                return;
            }

            DrawSummary(g);
            DrawLines(g);
        }

        private void DrawEmptyState(Graphics g)
        {
            int cy = 190;
            TextRenderer.DrawText(g, "先填最大负重", _big,
                new Rectangle(Pad, cy, Width - Pad * 2, 24), Th.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "装载方案不编默认负重；填了才会按每 kg 净利贪心装填",
                _small, new Rectangle(Pad, cy + 30, Width - Pad * 2, 18), Th.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            _goBoardRect = new Rectangle(Width / 2 - 70, cy + 64, 140, 28);
            Th.StrokeRound(g, _goBoardRect, 7, Th.Accent);
            TextRenderer.DrawText(g, "去差价榜看腿", _small, _goBoardRect, Th.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private void DrawSummary(Graphics g)
        {
            if (Plan == null) return;
            int y = 92;
            string usedText = "已用 " + TradeFormat.Kg(Plan.UsedKg) + " / " + TradeFormat.Kg(Plan.WeightLimitKg) + " KG";
            TextRenderer.DrawText(g, usedText, _big, new Point(Pad, y), Th.Text,
                TextFormatFlags.NoPrefix);

            bool nearFull = Plan.WeightLimitKg > 0 && Plan.UsedKg / Plan.WeightLimitKg >= 0.95d;
            TextRenderer.DrawText(g, "剩 " + TradeFormat.Kg(Plan.RemainingKg) + " KG", _small,
                new Point(Pad + 250, y + 4), nearFull ? Th.Accent : Th.Sub, TextFormatFlags.NoPrefix);

            // 4px 进度条：GDI+ 圆角会退化，直接 FillRectangle（规范 §8.6）
            var track = new Rectangle(Pad, y + 24, Width - Pad * 2, 4);
            using (var br = new SolidBrush(Color.FromArgb(34, 38, 46))) g.FillRectangle(br, track);
            double ratio = Plan.WeightLimitKg > 0 ? Plan.UsedKg / Plan.WeightLimitKg : 0d;
            if (ratio > 1d) ratio = 1d;
            int fillW = (int)(track.Width * ratio);
            if (fillW > 0)
            {
                Color fillColor = nearFull ? Th.Accent : Th.Good;
                using (var br = new SolidBrush(fillColor))
                    g.FillRectangle(br, new Rectangle(track.X, track.Y, fillW, track.Height));
            }

            string total = "合计 " + TradeFormat.Silver(Plan.TotalProfit) + " 银 · 密度 "
                           + TradeFormat.Silver((long)Math.Round(Plan.DensityPerKg)) + " 银/kg · "
                           + Plan.SkuCount + " SKU / " + Plan.TotalPieces + " 件";
            TextRenderer.DrawText(g, total, _big, new Point(Pad, y + 34), Th.Accent,
                TextFormatFlags.NoPrefix);
        }

        private void DrawLines(Graphics g)
        {
            if (Plan == null) return;
            int headerY = 164;
            int x = Pad;
            TextRenderer.DrawText(g, "物品", _head, new Rectangle(x, headerY, 300, 18), Th.Sub,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "件数", _head, new Rectangle(x + 330, headerY, 60, 18), Th.Sub,
                TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "重量", _head, new Rectangle(x + 400, headerY, 70, 18), Th.Sub,
                TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "小计", _head, new Rectangle(x + 480, headerY, 84, 18), Th.Sub,
                TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            using (var pen = new Pen(Th.Line))
                g.DrawLine(pen, Pad, headerY + 18, Width - Pad, headerY + 18);

            int rowsTop = headerY + 24;
            int maxRows = Math.Max(0, (Height - rowsTop - 84) / RowHeight);
            for (int i = 0; i < Plan.Lines.Count && i < maxRows; i++)
            {
                var line = Plan.Lines[i];
                int y = rowsTop + i * RowHeight;
                var rect = new Rectangle(Pad, y, Width - Pad * 2, RowHeight);
                using (var br = new SolidBrush(i % 2 == 0 ? TradeColors.RowBg : TradeColors.RowAlt))
                    g.FillRectangle(br, rect);
                string name = TradeFormat.ItemLabel(line.Leg.ItemId, line.Leg.DisplayName);
                TextRenderer.DrawText(g, name, _rowFont, new Rectangle(Pad + 8, y, 300, RowHeight), Th.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                    | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, "×" + line.Pieces, TradeFormat.NumFont,
                    new Rectangle(Pad + 320, y, 60, RowHeight), Th.Text,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, TradeFormat.Kg(line.WeightKg) + " KG", _small,
                    new Rectangle(Pad + 390, y, 70, RowHeight), Th.Sub,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, TradeFormat.Silver(line.Profit), TradeFormat.NumFont,
                    new Rectangle(Pad + 470, y, 94, RowHeight), Th.Accent,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            // 两种受限分开说清（规范 §4）
            string note = LimitNote();
            TextRenderer.DrawText(g, note, _small,
                new Rectangle(Pad, Height - 64, Width - Pad * 2, 18),
                Plan.WeightCapped || Plan.DepthCapped || Plan.HasUnknownWeight ? Th.Accent : Th.Good,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, "已装 " + Plan.SkuCount + " SKU / " + Plan.TotalPieces + " 件 · 方案只读本地库，不回源",
                _small, new Rectangle(Pad, Height - 40, Width - Pad * 2, 18), Th.Sub,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }

        private string LimitNote()
        {
            if (Plan == null || Plan.Lines.Count == 0)
            {
                if (Plan != null && Plan.HasUnknownWeight) return "盈利腿重量未知（缺 items.json 元数据），不猜负重装填";
                return "没有可装入的盈利腿";
            }
            var parts = new List<string>();
            if (Plan.DepthCapped) parts.Add("深度到顶：部分项受挂单承接量限制");
            if (Plan.WeightCapped) parts.Add("负重到顶：剩余 " + TradeFormat.Kg(Plan.RemainingKg) + " KG 装不下其它物品");
            if (Plan.HasUnknownWeight) parts.Add("有盈利腿重量未知，未装填");
            if (parts.Count == 0) return "该档盈利腿已全部装入（未受负重/深度限制）";
            return string.Join(" · ", parts.ToArray());
        }

        private static double EvalWeight(string text)
        {
            return TradeSettings.ParseWeightText(text);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int i = 0; i < _chipRects.Count; i++)
            {
                if (!_chipRects[i].Contains(e.Location)) continue;
                string[] presets = { "40", "150", "200" };
                SetWeightText(presets[i]);
                if (WeightChanged != null) WeightChanged(presets[i]);
                return;
            }
            if (!WeightSet && _goBoardRect.Contains(e.Location))
            {
                if (GoBoardRequested != null) GoBoardRequested();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _numFont.Dispose();
                _title.Dispose();
                _kicker.Dispose();
                _small.Dispose();
                _head.Dispose();
                _rowFont.Dispose();
                _big.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 路线导航（规范 §5）：候选路线 4 条（风险三段并列、不折进收益）+ 导航卡
    /// （当前段/8 向罗盘/进度/剩余收益/步骤/下一步）+ 红区三处同时变红。
    /// 导航状态来自 <see cref="NavigationState.Shared"/>（与悬浮条共用单一状态源）。
    /// </summary>
    public class TradeNavPanel : Control
    {
        private const int Pad = 16;

        private readonly Font _kicker = Th.F(7.5f);
        private readonly Font _title = Th.FB(13f);
        private readonly Font _small = Th.F(8f);
        private readonly Font _head = Th.F(8f);
        private readonly Font _rowFont = Th.F(9f);
        private readonly Font _chip = Th.F(7.5f);
        private readonly Font _big = Th.FB(12f);

        private readonly List<Rectangle> _candidateRects = new List<Rectangle>();
        private readonly List<NavigationRoute> _candidateData = new List<NavigationRoute>();
        private Rectangle _nextRect;
        private Rectangle _prevRect;
        private readonly Rectangle[,] _compass = new Rectangle[3, 3];

        public TradeLeg SelectedLeg;
        public double WeightLimitKg;
        public List<NavigationRoute> Candidates = new List<NavigationRoute>();
        public Func<string, string> NameOf;

        public event Action<NavigationRoute> RouteRequested;

        public TradeNavPanel()
        {
            DoubleBuffered = true;
            BackColor = Th.Bg;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        }

        public void SetLeg(TradeLeg leg, List<NavigationRoute> candidates, double weightLimitKg)
        {
            SelectedLeg = leg;
            Candidates = candidates ?? new List<NavigationRoute>();
            WeightLimitKg = weightLimitKg;
            Invalidate();
        }

        public void SetWeight(double weightLimitKg)
        {
            WeightLimitKg = weightLimitKg;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Th.Bg);
            Th.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), 10, Th.Card);
            Th.StrokeRound(g, new Rectangle(0, 0, Width - 1, Height - 1), 10, Th.Line);

            TextRenderer.DrawText(g, "ROUTE NAV", _kicker, new Point(Pad, 10), Th.Sub,
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "路线导航", _title, new Point(Pad, 24), Th.Text,
                TextFormatFlags.NoPrefix);

            if (SelectedLeg == null)
            {
                int cy = Height / 2 - 24;
                TextRenderer.DrawText(g, "还没选路线", Th.FB(11f),
                    new Rectangle(Pad, cy, Width - Pad * 2, 24), Th.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "先在差价榜点一条腿，这里会给出 4 条候选路线与导航卡",
                    _small, new Rectangle(Pad, cy + 30, Width - Pad * 2, 18), Th.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            // 当前腿摘要
            string name = TradeFormat.ItemLabel(SelectedLeg.ItemId, SelectedLeg.DisplayName);
            string summary = name + " · " + TradeCities.Zh(SelectedLeg.OriginCity) + " → "
                             + TradeCities.Zh(SelectedLeg.DestCity) + " · 单件净利 "
                             + TradeFormat.SilverSigned(SelectedLeg.NetPerUnit) + " 银";
            TextRenderer.DrawText(g, summary, _small, new Point(Pad, 48), Th.Sub,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            TextRenderer.DrawText(g, "候选路线（风险不折进收益）", _head, new Point(Pad, 68), Th.Sub,
                TextFormatFlags.NoPrefix);

            _candidateRects.Clear();
            _candidateData.Clear();
            int top = 88;
            for (int i = 0; i < Candidates.Count && i < 4; i++)
            {
                var route = Candidates[i];
                var rect = new Rectangle(Pad, top + i * 54, Width - Pad * 2, 50);
                _candidateRects.Add(rect);
                _candidateData.Add(route);

                bool hover = rect.Contains(PointToClient(Cursor.Position));
                using (var br = new SolidBrush(hover ? TradeColors.RowHover : (i % 2 == 0 ? TradeColors.RowBg : TradeColors.RowAlt)))
                    g.FillRectangle(br, rect);
                using (var strip = new SolidBrush(TradeColors.RiskColor(route.RiskLevel)))
                    g.FillRectangle(strip, new Rectangle(rect.X, rect.Y + 6, 3, rect.Height - 12));

                TextRenderer.DrawText(g, route.Title + " · " + route.FormLabel, _rowFont,
                    new Rectangle(rect.X + 12, rect.Y + 4, 300, 20), Th.Text,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, route.RiskNote, _small,
                    new Rectangle(rect.X + 12, rect.Y + 24, 340, 18), Th.Sub,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

                // 风险三段并列：等级芯片 + 红区段数 + 后果
                var chipRect = new Rectangle(rect.Right - 150, rect.Y + 6, 62, 20);
                using (var br = new SolidBrush(route.RedSegments > 0 ? TradeColors.RedZoneBg : TradeColors.WarnBg))
                    g.FillRectangle(br, chipRect);
                TextRenderer.DrawText(g, route.RiskLevel + " · " + route.RedSegments + " 段红区", _chip, chipRect,
                    TradeColors.RiskColor(route.RiskLevel),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "耗时约 " + route.Minutes + " 分 · " + route.Steps.Count + " 段",
                    _small, new Rectangle(rect.Right - 150, rect.Y + 28, 138, 18), Th.Sub,
                    TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }

            DrawNavigation(g);
        }

        private void DrawNavigation(Graphics g)
        {
            var nav = NavigationState.Shared;
            int top = 312;
            if (!nav.IsActive)
            {
                var boxRect = new Rectangle(Pad, top, Width - Pad * 2, 110);
                using (var br = new SolidBrush(Th.CardHi)) g.FillRectangle(br, boxRect);
                Th.StrokeRound(g, boxRect, 8, Th.Line);
                TextRenderer.DrawText(g, "选一条候选路线开始导航", _small,
                    new Rectangle(boxRect.X, boxRect.Y + 20, boxRect.Width, 20), Th.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "导航激活后，悬浮条会同步显示当前段与下一步",
                    _small, new Rectangle(boxRect.X, boxRect.Y + 44, boxRect.Width, 20), Th.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                _nextRect = Rectangle.Empty;
                _prevRect = Rectangle.Empty;
                return;
            }

            bool red = nav.IsCurrentRed;
            var card = new Rectangle(Pad, top, Width - Pad * 2, 180);
            using (var br = new SolidBrush(red ? TradeColors.RedZoneBg : Th.CardHi))
                g.FillRectangle(br, card);
            Th.StrokeRound(g, card, 8, red ? Th.Bad : Th.Line);
            if (red)
                using (var br = new SolidBrush(Th.Bad))
                    g.FillRectangle(br, new Rectangle(card.X, card.Y + 6, 3, card.Height - 12));

            TextRenderer.DrawText(g, "导航中 · " + nav.Title, _big,
                new Rectangle(card.X + 12, card.Y + 8, card.Width - 200, 22),
                red ? Th.Bad : Th.Text, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, nav.FormLabel ?? "", _chip,
                new Rectangle(card.Right - 110, card.Y + 8, 98, 20), Th.Accent,
                TextFormatFlags.Right | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, "当前段", _small, new Point(card.X + 12, card.Y + 30), Th.Sub,
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, nav.CurrentText, _rowFont,
                new Rectangle(card.X + 12, card.Y + 46, card.Width - 200, 20),
                red ? Th.Bad : Th.Text, TextFormatFlags.Left | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, nav.ProgressText, _small, new Point(card.X + 12, card.Y + 66), Th.Sub,
                TextFormatFlags.NoPrefix);
            DrawProgressBar(g, new Rectangle(card.X + 112, card.Y + 72, 220, 4), nav);

            TextRenderer.DrawText(g, "剩余预估收益 " + TradeFormat.Silver(nav.EstimatedProfit) + " 银 · "
                                  + nav.Pieces + " 件 · " + TradeFormat.Kg(nav.WeightKg) + " KG",
                _rowFont, new Rectangle(card.X + 12, card.Y + 84, card.Width - 24, 20), Th.Accent,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix);

            if (red)
            {
                TextRenderer.DrawText(g, "⚠ 红区 · " + (nav.RiskNote ?? "此段为红区，被击倒将失去全部货物"),
                    _small, new Rectangle(card.X + 12, card.Y + 106, card.Width - 24, 18), Th.Bad,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }

            // 下一步提示
            TextRenderer.DrawText(g, nav.NextHint, _small,
                new Rectangle(card.X + 12, card.Y + 128, card.Width - 200, 18), red ? Th.Bad : Th.Sub,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            _prevRect = new Rectangle(card.X + 12, card.Y + 148, 76, 24);
            _nextRect = new Rectangle(card.X + 96, card.Y + 148, 90, 24);
            DrawButton(g, _prevRect, "上一步", nav.HasPrev);
            DrawButton(g, _nextRect, nav.Arrived ? "已完成" : "下一步", nav.HasNext);

            DrawCompass(g, new Rectangle(card.Right - 170, card.Y + 36, 150, 132), nav);
        }

        private void DrawProgressBar(Graphics g, Rectangle track, NavigationState nav)
        {
            using (var br = new SolidBrush(Color.FromArgb(34, 38, 46))) g.FillRectangle(br, track);
            int total = Math.Max(1, nav.StepCount);
            int done = nav.Arrived ? total : nav.StepIndex;
            int fillW = (int)(track.Width * ((double)done / total));
            if (fillW > 0)
                using (var br = new SolidBrush(nav.IsCurrentRed ? Th.Bad : Th.Good))
                    g.FillRectangle(br, new Rectangle(track.X, track.Y, fillW, track.Height));
            for (int i = 1; i < total; i++)
            {
                int x = track.X + (int)(track.Width * ((double)i / total));
                using (var pen = new Pen(Th.Card)) g.DrawLine(pen, x, track.Y, x, track.Y + track.Height);
            }
        }

        private void DrawButton(Graphics g, Rectangle rect, string text, bool enabled)
        {
            if (enabled) Th.StrokeRound(g, rect, 6, Th.Accent);
            else Th.StrokeRound(g, rect, 6, Th.Line);
            TextRenderer.DrawText(g, text, _chip, rect, enabled ? Th.Accent : Th.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        /// <summary>八向罗盘 3×3（规范 §5：命中 accent 实底）。</summary>
        private void DrawCompass(Graphics g, Rectangle area, NavigationState nav)
        {
            string[,] dirs = { { "NW", "N", "NE" }, { "W", "", "E" }, { "SW", "S", "SE" } };
            string current = nav.CurrentStep != null ? nav.CurrentStep.Direction : "";
            int cell = Math.Min(area.Width / 3, area.Height / 3);
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    string dir = dirs[r, c];
                    var rect = new Rectangle(area.X + c * cell + 1, area.Y + r * cell + 1, cell - 2, cell - 2);
                    _compass[r, c] = rect;
                    if (dir.Length == 0)
                    {
                        TextRenderer.DrawText(g, "•", _chip, rect, Th.Sub,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        continue;
                    }
                    if (current == dir) Th.FillRound(g, rect, 5, Th.Accent);
                    else
                    {
                        using (var br = new SolidBrush(Th.Card)) g.FillRectangle(br, rect);
                    }
                    TextRenderer.DrawText(g, dir, _chip, rect,
                        current == dir ? Color.FromArgb(26, 21, 10) : Th.Sub,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int i = 0; i < _candidateRects.Count; i++)
            {
                if (_candidateRects[i].Contains(e.Location))
                {
                    if (RouteRequested != null) RouteRequested(_candidateData[i]);
                    return;
                }
            }
            if (NavigationState.Shared.IsActive)
            {
                if (_nextRect.Contains(e.Location)) NavigationState.Shared.Next();
                else if (_prevRect.Contains(e.Location)) NavigationState.Shared.Prev();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _kicker.Dispose();
                _title.Dispose();
                _small.Dispose();
                _head.Dispose();
                _rowFont.Dispose();
                _chip.Dispose();
                _big.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
