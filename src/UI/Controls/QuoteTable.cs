using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Controls
{
    /// <summary>
    /// 报价卡（设计 rev27 §3.4 / §5 / §六）：城市 / ↓ 买入 / ↑ 卖出 / 数据时间 四列。
    ///
    /// 三条硬约束都在这里落地：
    /// ① **逐城时间**：每个城市格子用它自己那一路的 <c>sell_price_min_date</c> / <c>buy_price_max_date</c>
    ///    算相对时间（各城可差十几小时），页头不给「总时间」；
    /// ② **0 值不画 0**：价格 0 一律「暂无挂单」/「无数据」，且不参与底注极值比较；
    /// ③ **&gt;24h 整行弱化**：本城数据超过 24 小时，整行降到 70% 亮度（用预计算实色，GDI+ 没有 alpha）。
    ///
    /// 市场页只查价：本控件没有任何差价 / 税后 / 利润字段（用户原话「跑商相关不要放到市场模块」）。
    /// </summary>
    public class QuoteTable : Control
    {
        // ── 布局（设计 §3.4：卡头 40 + 表头 24 + N×26 + 底注 28；卡宽 620 减两侧 16 = 588）
        public const int CardWidth = 620;
        /// <summary>空态/未选态的基础卡高（按六城行位预留）。有数据时用 <see cref="PreferredHeight"/> 动态加高。</summary>
        public const int CardHeight = 248;
        private const int HeadHeight = 40;
        private const int ColumnHeadHeight = 24;
        private const int RowHeight = 26;
        private const int FooterHeight = 28;
        private const int PadX = 16;

        /// <summary>城市行数变化时卡片应有的高度（7 城 = 274，六城 = 248）。</summary>
        public static int PreferredHeight(int cityCount)
        {
            int rows = cityCount <= 0 ? 6 : cityCount;
            return HeadHeight + ColumnHeadHeight + rows * RowHeight + FooterHeight;
        }

        // 四列权重（设计规范 §三）：城市 120 / 买入 180 / 卖出 180 / 数据时间 108 = 588
        private static readonly int[] ColumnWidths = { 120, 180, 180, 108 };

        // GDI+ 没有透明度：所有 12% 底纹预计算成实色（设计 §8）
        private static readonly Color HoverBg = Color.FromArgb(0x15, 0x1B, 0x21);   // ink-3
        private static readonly Color ZebraBg = Color.FromArgb(0x17, 0x1A, 0x20);   // ink-2（Card）
        private static readonly Color ZebraBg2 = Color.FromArgb(0x1F, 0x23, 0x2B);  // ink-3（CardHi）
        private static readonly Color WarnBarBg = Color.FromArgb(0x2A, 0x24, 0x19);  // warn 12% 压成实色
        private static readonly Color HealCapsuleBg = Color.FromArgb(0x16, 0x28, 0x27);
        private static readonly Color StaleTextColor = Color.FromArgb(0x6D, 0x73, 0x7C); // 70% 亮度

        private readonly Font _cardFont = Th.FB(10.5f);
        private readonly Font _kickerFont = Th.F(7.5f);
        private readonly Font _headFont = Th.F(8.5f);
        private readonly Font _cityFont = Th.F(9.5f);
        private readonly Font _priceFont = new Font("Consolas", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _timeFont = Th.F(8f);
        private readonly Font _footFont = Th.F(8f);

        private List<QuoteViewRow> _rows = new List<QuoteViewRow>();
        private QuoteTableModel.CardState _state = QuoteTableModel.CardState.NotSelected;
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private string? _headRight = "6 城 · 亚服";
        private string? _emptyTitle = "取不到亚服数据";
        private string? _emptyDetail = "检查网络或稍后重试";
        private bool _showRetry;
        private string? _warnBar;     // M7：刷新失败但有旧数据
        private int _hoverRow = -1;

        public event EventHandler? RetryRequested;
        public event EventHandler? AdjacentTierRequested;
        private bool _wantAdjacent;

        public QuoteTable()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Th.Bg;
            Size = new Size(CardWidth, CardHeight);
            Font = _cityFont;
        }

        /// <summary>
        /// 设置数据。now 由调用方传入（不用 DateTime.Now 直接算，保证同一帧内三处时间同源同精度）。
        /// 卡片高度随城市行数自适应（7 城含黑市时比六城高一行）。
        /// </summary>
        public void SetData(List<QuoteViewRow> rows, DateTimeOffset now)
        {
            _rows = rows ?? new List<QuoteViewRow>();
            _now = now;
            _state = QuoteTableModel.StateOf(_rows);
            _hoverRow = -1;
            _warnBar = null;
            Height = PreferredHeight(_rows.Count);
            Invalidate();
        }

        /// <summary>M5：三级未选满，不渲染行，只显示「请先选择物品」。</summary>
        public void SetNotSelected()
        {
            _rows = new List<QuoteViewRow>();
            _state = QuoteTableModel.CardState.NotSelected;
            _emptyTitle = "↑ 请先选择物品";
            _emptyDetail = "在左侧选满一级 / 二级 / 三级与阶级后显示各城报价";
            _showRetry = false;
            Height = CardHeight;
            Invalidate();
        }

        /// <summary>
        /// 筛选组合已选满、但还没查过（缓存里没有该组合）：明确写「尚未查询」，不冒充「无挂单」。
        /// 渲染路径仍然零 HTTP，点「查询行情」才回源。
        /// </summary>
        public void SetNotQueried()
        {
            _rows = new List<QuoteViewRow>();
            _state = QuoteTableModel.CardState.NoDataAtAll;
            _emptyTitle = "尚未查询该组合";
            _emptyDetail = "点击「查询行情」回源各城报价";
            _showRetry = true;
            Height = CardHeight;
            Invalidate();
        }

        /// <summary>M8：取不到数据且无旧数据（明确说取不到，不与「旧数据」混写）。</summary>
        public void SetNoData()
        {
            _rows = new List<QuoteViewRow>();
            _state = QuoteTableModel.CardState.NoDataAtAll;
            _emptyTitle = "取不到亚服数据";
            _emptyDetail = "检查网络或稍后重试";
            _showRetry = true;
            Height = CardHeight;
            Invalidate();
        }

        /// <summary>
        /// 通用空态：搜索无结果 / 组合不存在等「不是网络失败」的提示。
        /// 明确写清原因 + 不显示无关缓存（搜索无结果时旧实现会回落渲染全量缓存，这里由调用方给提示）。
        /// </summary>
        public void SetMessage(string title, string detail, bool showRetry)
        {
            _rows = new List<QuoteViewRow>();
            _state = QuoteTableModel.CardState.NoDataAtAll;
            _emptyTitle = title ?? "";
            _emptyDetail = detail ?? "";
            _showRetry = showRetry;
            Height = CardHeight;
            Invalidate();
        }

        /// <summary>M7：刷新失败但有旧数据 —— 顶部 32px warn 条写明「以下为几点几分」的旧数据。</summary>
        public void SetFailedWithOldData(string timeText)
        {
            _warnBar = "刷新失败，以下为 " + timeText + " 的旧数据";
            Invalidate();
        }

        public void ClearWarnBar()
        {
            _warnBar = null;
            Invalidate();
        }

        public void SetHeadRight(string text)
        {
            _headRight = text;
            Invalidate();
        }

        public int RowCount { get { return _rows.Count; } }
        public QuoteTableModel.CardState State { get { return _state; } }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int row = RowAt(e.Y);
            if (row != _hoverRow) { _hoverRow = row; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverRow >= 0) { _hoverRow = -1; Invalidate(); }
        }

        private int RowsTop { get { return HeadHeight + ColumnHeadHeight; } }

        /// <summary>当前帧卡片实际高度：有数据行时按城市数自适应，空态用基础卡高。</summary>
        private int CardHeightNow
        {
            get
            {
                if (_state == QuoteTableModel.CardState.Rows
                    || _state == QuoteTableModel.CardState.AllSellEmpty)
                    return PreferredHeight(_rows.Count);
                return CardHeight;
            }
        }

        private int RowAt(int y)
        {
            if (_state != QuoteTableModel.CardState.Rows && _state != QuoteTableModel.CardState.AllSellEmpty)
                return -1;
            int idx = (y - RowsTop) / RowHeight;
            if (idx < 0 || idx >= _rows.Count) return -1;
            return idx;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Th.Bg);
            int w = ClientSize.Width > 0 ? ClientSize.Width : CardWidth;

            Th.FillRound(g, new Rectangle(0, 0, w, CardHeightNow), 6, Th.Card);
            DrawHead(g, w);

            if (_warnBar != null)
                DrawWarnBar(g, w);

            switch (_state)
            {
                case QuoteTableModel.CardState.NotSelected:
                case QuoteTableModel.CardState.NoDataAtAll:
                case QuoteTableModel.CardState.NoListingAtAll:
                    DrawEmptyState(g, w);
                    break;
                default:
                    DrawTable(g, w);
                    break;
            }
        }

        private void DrawHead(Graphics g, int w)
        {
            TextRenderer.DrawText(g, "城市报价", _cardFont, new Point(PadX, 10), Th.Text,
                TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "CITY QUOTES", _kickerFont, new Point(PadX + 68, 16), Th.Sub,
                TextFormatFlags.NoPadding);
            // 右端「6 城 · 亚服」——注意这里不写数据时间：时间必须逐城各自算
            TextRenderer.DrawText(g, _headRight, _headFont,
                new Rectangle(w - PadX - 120, 14, 120, 16), Th.Sub,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        private void DrawWarnBar(Graphics g, int w)
        {
            var r = new Rectangle(PadX, HeadHeight, w - PadX * 2, 32);
            using (var br = new SolidBrush(WarnBarBg)) g.FillRectangle(br, r);
            TextRenderer.DrawText(g, _warnBar, _footFont, new Rectangle(r.X + 8, r.Y, r.Width - 16, 32),
                Th.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawTable(Graphics g, int w)
        {
            // 表头 24
            var headRect = new Rectangle(PadX, HeadHeight, w - PadX * 2, ColumnHeadHeight);
            int x = PadX;
            string[] titles = { "城市", "↓ 买入", "↑ 卖出", "数据时间" };
            bool[] right = { false, true, true, true };
            for (int c = 0; c < titles.Length; c++)
            {
                var cell = new Rectangle(x, headRect.Y, ColumnWidths[c], ColumnHeadHeight);
                TextRenderer.DrawText(g, titles[c], _headFont, cell, Th.Sub,
                    (right[c] ? TextFormatFlags.Right : TextFormatFlags.Left)
                    | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += ColumnWidths[c];
            }
            using (var pen = new Pen(Th.Line))
                g.DrawLine(pen, PadX, headRect.Bottom, w - PadX, headRect.Bottom);

            int top = RowsTop;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var rect = new Rectangle(PadX, top + i * RowHeight, w - PadX * 2, RowHeight);
                using (var br = new SolidBrush(i == _hoverRow ? HoverBg : (i % 2 == 0 ? ZebraBg : ZebraBg2)))
                    g.FillRectangle(br, rect);

                // >24h 整行弱化（预计算实色，不用 alpha）
                Color textColor = row.Stale ? StaleTextColor : Th.Text;
                Color subColor = row.Stale ? StaleTextColor : Th.Sub;

                int cx = rect.X;
                TextRenderer.DrawText(g, MarketCities.ShortName(row.City), _cityFont,
                    new Rectangle(cx, rect.Y, ColumnWidths[0], RowHeight), textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                // 买入（左）——按买入侧自己的时间戳判时效，不用行级最新时间背书
                cx += ColumnWidths[0];
                DrawPriceCell(g, cx, rect.Y, ColumnWidths[1], row, true, textColor, subColor);

                // 卖出（右）——按卖出侧自己的时间戳判时效
                cx += ColumnWidths[1];
                DrawPriceCell(g, cx, rect.Y, ColumnWidths[2], row, false, textColor, subColor);

                // 数据时间（逐城、逐路：↓ 买入侧 / ↑ 卖出侧 各用自己的时间戳）
                cx += ColumnWidths[2];
                string timeText = QuoteTableModel.SideTimeSummary(row, _now);
                var worst = QuoteTableModel.WorstSideFreshness(row);
                Color timeColor = row.Stale ? StaleTextColor : Th.Sub;
                if (worst != QuoteFreshness.Fresh) timeColor = Th.Accent;
                TextRenderer.DrawText(g, timeText, _timeFont,
                    new Rectangle(cx, rect.Y, ColumnWidths[3], RowHeight), timeColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                    | TextFormatFlags.EndEllipsis);
            }

            DrawFooter(g, w, top + _rows.Count * RowHeight);
        }

        /// <summary>
        /// 价格格：有价画数字（Consolas）；0 值画「暂无挂单」/「无数据」（雅黑，非数字字体）。
        /// 0 绝不画成 0（会被读成白送），也不画成 -（会被读成解析失败）。
        /// 单侧时效过期时该侧数字弱化、偏旧时用 accent 标出——买卖两路各判各的。
        /// </summary>
        private void DrawPriceCell(Graphics g, int x, int y, int width, QuoteViewRow row,
            bool isBuy, Color priceColor, Color subColor)
        {
            var cell = new Rectangle(x, y, width, RowHeight);
            var kind = isBuy ? row.BuyKind : row.SellKind;
            long price = isBuy ? row.BuyPrice : row.SellPrice;
            var freshness = isBuy ? row.BuyFreshness : row.SellFreshness;

            if (kind == QuoteCellKind.Value)
            {
                Color color = priceColor;
                if (freshness == QuoteFreshness.Expired) color = subColor;
                else if (freshness == QuoteFreshness.Aging) color = Th.Accent;
                TextRenderer.DrawText(g, price.ToString("N0"), _priceFont, cell, color,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }

            // 卖出列全 0（M3）时整列灰化，文案陈述结论而不是含糊词
            string label = kind == QuoteCellKind.NoOrders ? "暂无挂单" : "无数据";
            TextRenderer.DrawText(g, label, _headFont, cell,
                kind == QuoteCellKind.NoOrders ? subColor : Th.Sub,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawFooter(Graphics g, int w, int y)
        {
            int excluded;
            var ex = QuoteTableModel.Extremes(_rows, out excluded);
            var r = new Rectangle(PadX, y, w - PadX * 2, FooterHeight);
            using (var pen = new Pen(Th.Line))
                g.DrawLine(pen, r.X, r.Y, r.Right, r.Y);

            string left;
            if (ex.MinBuy > 0 || ex.MaxSell > 0)
            {
                var sb = new System.Text.StringBuilder();
                if (ex.MinBuy > 0) sb.Append("最低买入 " + ex.MinBuy.ToString("N0") + "（" + ShortCity(ex.MinBuyCity) + "）");
                if (ex.MaxSell > 0)
                {
                    if (sb.Length > 0) sb.Append(" · ");
                    sb.Append("最高卖出 " + ex.MaxSell.ToString("N0") + "（" + ShortCity(ex.MaxSellCity) + "）");
                }
                // 极值只说事实，不给差额：差额是算账结论，归跑商页
                left = sb.ToString();
            }
            else
            {
                left = "六城均无可比价格";
            }

            TextRenderer.DrawText(g, left, _footFont,
                new Rectangle(r.X, r.Y, 360, FooterHeight), Th.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // 硬性披露：只要有一格因 0 值/过期被排除，就必须写清排除了几格
            if (excluded > 0)
            {
                TextRenderer.DrawText(g, "含 0 值 " + excluded + " 格，未参与比较", _footFont,
                    new Rectangle(r.Right - 200, r.Y, 200, FooterHeight), Th.Accent,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private static string ShortCity(string city)
        {
            // 城市短名统一由 MarketCities 提供（含 Black Market → 黑市）
            return MarketCities.ShortName(city);
        }

        private void DrawEmptyState(Graphics g, int w)
        {
            int top = RowsTop;
            int bottom = CardHeightNow - FooterHeight;
            int cy = (top + bottom) / 2 - 20;

            string title = _emptyTitle ?? "";
            if (_state == QuoteTableModel.CardState.NoListingAtAll)
            {
                title = "该物品在亚服暂无任何挂单";
            }
            else if (_state == QuoteTableModel.CardState.AllSellEmpty)
            {
                title = "该物品在亚服暂无挂单，卖不出去";
            }

            // 空态不用红色（M4/M9 是正常业务状态），用中性文案 + accent 动作按钮
            TextRenderer.DrawText(g, title, Th.FB(10f),
                new Rectangle(PadX, cy, w - PadX * 2, 22), Th.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _emptyDetail ?? "", _footFont,
                new Rectangle(PadX, cy + 26, w - PadX * 2, 18), Th.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

            if (_showRetry)
            {
                var btn = new Rectangle(w / 2 - 48, cy + 54, 96, 24);
                Th.StrokeRound(g, btn, 4, Th.Accent);
                TextRenderer.DrawText(g, "重试", _footFont, btn, Th.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else if (_state == QuoteTableModel.CardState.NoListingAtAll)
            {
                var b1 = new Rectangle(w / 2 - 82, cy + 54, 76, 22);
                var b2 = new Rectangle(w / 2 + 6, cy + 54, 76, 22);
                Th.StrokeRound(g, b1, 4, Th.Accent);
                Th.StrokeRound(g, b2, 4, Th.Accent);
                TextRenderer.DrawText(g, "看 T4", _footFont, b1, Th.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "看 T6", _footFont, b2, Th.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int w = ClientSize.Width > 0 ? ClientSize.Width : CardWidth;
            int top = RowsTop;
            int bottom = CardHeightNow - FooterHeight;
            int cy = (top + bottom) / 2 - 20;
            if (_showRetry)
            {
                var btn = new Rectangle(w / 2 - 48, cy + 54, 96, 24);
                if (btn.Contains(e.Location) && RetryRequested != null) RetryRequested(this, EventArgs.Empty);
            }
            else if (_state == QuoteTableModel.CardState.NoListingAtAll)
            {
                var b1 = new Rectangle(w / 2 - 82, cy + 54, 76, 22);
                var b2 = new Rectangle(w / 2 + 6, cy + 54, 76, 22);
                if ((b1.Contains(e.Location) || b2.Contains(e.Location)) && AdjacentTierRequested != null)
                {
                    _wantAdjacent = b1.Contains(e.Location);
                    AdjacentTierRequested(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>点了「看 T4」= true，点了「看 T6」= false（相邻阶级跳转）。</summary>
        public bool LastAdjacentWasLower { get { return _wantAdjacent; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cardFont.Dispose();
                _kickerFont.Dispose();
                _headFont.Dispose();
                _cityFont.Dispose();
                _priceFont.Dispose();
                _timeFont.Dispose();
                _footFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
