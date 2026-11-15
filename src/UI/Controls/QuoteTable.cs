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
        // 几何常量与 PreferredCardHeight 的权威定义在 QuoteTableModel（Core，可用离线测试验），
        // 这里只做别名，避免两处各写一份数字后走样。
        public const int CardWidth = 620;
        /// <summary>空态/未选态的基础卡高（按六城行位预留）。有数据时用 <see cref="PreferredHeight"/> 动态加高。</summary>
        public const int CardHeight = 248;
        private const int HeadHeight = QuoteTableModel.CardHeadHeight;
        private const int ColumnHeadHeight = QuoteTableModel.ColumnHeadHeight;
        private const int RowHeight = QuoteTableModel.RowHeight;
        private const int FooterHeight = QuoteTableModel.FooterHeight;
        private const int PadX = 16;

        /// <summary>城市行数变化时卡片应有的高度（7 城 = 274，六城 = 248）。</summary>
        public static int PreferredHeight(int cityCount)
        {
            return QuoteTableModel.PreferredCardHeight(cityCount);
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
        private bool _fetchFailed;    // M8：本次回源失败且无旧数据（区分「取不到」与「组合不存在 / 搜索无结果」）
        private int _hoverRow = -1;
        private int _loadingCities = 6;  // M6：骨架按本次查询的城市数给占位行位

        public event EventHandler? RetryRequested;
        public event EventHandler? AdjacentTierRequested;
        private bool _wantAdjacent;
        // M3 相邻阶级（设计 §5.3 的「看 T4 / 看 T6」）：0 = 该方向无可跳阶级，按钮不画
        private int _adjLower;
        private int _adjUpper;

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
            _fetchFailed = false;
            // M3 用 36px 提示行（比底注高 8），高度必须与 CardHeightNow 同源
            Height = _state == QuoteTableModel.CardState.AllSellEmpty
                ? QuoteTableModel.HintCardHeight(_rows.Count)
                : PreferredHeight(_rows.Count);
            Invalidate();
        }

        /// <summary>M5：三级未选满，不渲染行，只显示「请先选择物品」。</summary>
        public void SetNotSelected()
        {
            _rows = new List<QuoteViewRow>();
            _state = QuoteTableModel.CardState.NotSelected;
            _fetchFailed = false;
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
            _fetchFailed = false;     // 「尚未查询」不是失败态，联动位仍显示
            _emptyTitle = "尚未查询该组合";
            _emptyDetail = "点击「查询行情」回源各城报价";
            _showRetry = true;
            Height = CardHeight;
            Invalidate();
        }

        /// <summary>
        /// M6：查询在途。骨架占位（6 行灰条，不转圈），**不保留上一次价格的残影**——
        /// 留着旧值会让人以为刷新没生效（设计 §六 M6 硬规则「加载中不显示旧值的残影」）。
        /// 卡片高度按本次查询的城市数给出占位行位，查询结束后无论成败都由调用方退出本态。
        /// </summary>
        public void SetLoading(int cityCount)
        {
            _rows = new List<QuoteViewRow>();
            _state = QuoteTableModel.CardState.Loading;
            _fetchFailed = false;
            _emptyTitle = "查询中";
            _emptyDetail = "";
            _showRetry = false;
            _warnBar = null;
            _loadingCities = cityCount <= 0 ? 6 : cityCount;
            Height = PreferredHeight(_loadingCities);
            Invalidate();
        }

        /// <summary>M8：取不到数据且无旧数据（明确说取不到，不与「旧数据」混写）。</summary>
        public void SetNoData()
        {
            _rows = new List<QuoteViewRow>();
            _state = QuoteTableModel.CardState.NoDataAtAll;
            _fetchFailed = true;      // 真·M8：联动位按设计 §六 灰化
            _emptyTitle = "取不到亚服数据";
            _emptyDetail = "检查网络或稍后重试";
            _showRetry = true;
            Height = CardHeight;
            Invalidate();
        }

        /// <summary>
        /// 通用空态：搜索无结果 / 组合不存在等「不是网络失败」的提示。
        /// 明确写清原因 + 不显示无关缓存（搜索无结果时旧实现会回落渲染全量缓存，这里由调用方给提示）。
        /// 这类空态**不是** M8：设计 §六 里 M9「组合无匹配」的联动位是「显示」，
        /// 所以这里不打 _fetchFailed，联动位保持亮色。
        /// </summary>
        public void SetMessage(string title, string detail, bool showRetry)
        {
            _rows = new List<QuoteViewRow>();
            _state = QuoteTableModel.CardState.NoDataAtAll;
            _fetchFailed = false;     // M9 等业务空态：联动位按设计仍显示
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

        /// <summary>
        /// 联动位是否该灰化（设计 §六 联动位列）：只有 M6 加载中、以及真·M8（回源失败且无旧数据）灰化。
        /// 不能直接用 State==NoDataAtAll —— M9 组合无匹配 / 搜索无结果 / 「尚未查询」共用这个枚举值，
        /// 但设计里这三态联动位都是「显示」。
        /// </summary>
        public bool LinkRowDimmed
        {
            get { return !QuoteTableModel.LinkRowEnabled(_state, _fetchFailed); }
        }

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
                if (_state == QuoteTableModel.CardState.Rows)
                    return PreferredHeight(_rows.Count);
                // M3：底注换成 36px 提示行，卡高比常规多 8（高度由 Core 函数给，不在此手算）
                if (_state == QuoteTableModel.CardState.AllSellEmpty)
                    return QuoteTableModel.HintCardHeight(_rows.Count);
                if (_state == QuoteTableModel.CardState.Loading)
                    return PreferredHeight(_loadingCities);
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
                case QuoteTableModel.CardState.Loading:
                    DrawSkeleton(g, w);
                    break;
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
            // 右端「6 城 · 亚服」——注意这里不写数据时间：时间必须逐城各自算。
            // M6 期间改写「查询中…」：刷新在途是唯一可说的状态，报旧城市数没有意义。
            string headRight = _state == QuoteTableModel.CardState.Loading
                ? "查询中…" : (_headRight ?? "6 城 · 亚服");
            TextRenderer.DrawText(g, headRight, _headFont,
                new Rectangle(w - PadX - 120, 14, 120, 16),
                _state == QuoteTableModel.CardState.Loading ? Th.Accent : Th.Sub,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        /// <summary>
        /// 表头四列（M6 骨架与有数据时同构）：列头保持可读，用户才知道这卡还是报价卡，
        /// 只是数据在来的路上——整卡一起灰掉会让人以为控件坏了。
        /// M3 时卖列头追加「（亚服暂无收购单）」：整列 0 值必须在列头就说清原因，
        /// 否则六行「暂无挂单」会被读成六次独立故障。
        /// </summary>
        private void DrawTableHead(Graphics g, int w)
        {
            var headRect = new Rectangle(PadX, HeadHeight, w - PadX * 2, ColumnHeadHeight);
            bool sellEmpty = _state == QuoteTableModel.CardState.AllSellEmpty;
            int x = PadX;
            string[] titles = { "城市", "↓ 买入", sellEmpty ? "↑ 卖出（亚服暂无收购单）" : "↑ 卖出", "数据时间" };
            bool[] right = { false, true, true, true };
            for (int c = 0; c < titles.Length; c++)
            {
                // M3 卖列头要写备注，180 宽装不下「↑ 卖出（亚服暂无收购单）」——
                // 卖列与时间列合并区右对齐，视觉上仍归在卖列尾
                var cell = new Rectangle(x, headRect.Y, ColumnWidths[c], ColumnHeadHeight);
                if (sellEmpty && c == 2) cell.Width = ColumnWidths[2] + ColumnWidths[3];
                TextRenderer.DrawText(g, titles[c], _headFont, cell,
                    sellEmpty && c == 2 ? StaleTextColor : Th.Sub,
                    (right[c] ? TextFormatFlags.Right : TextFormatFlags.Left)
                    | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                    | TextFormatFlags.EndEllipsis);
                x += ColumnWidths[c];
            }
            using (var pen = new Pen(Th.Line))
                g.DrawLine(pen, PadX, headRect.Bottom, w - PadX, headRect.Bottom);
        }

        /// <summary>
        /// M6 骨架：按本次查询的城市数铺灰条，**不转圈、不画假城市名、不画假价格**。
        /// 假名字/假数字会被读成真行情——这正是「加载中不显示旧值的残影」要防的另一半。
        /// 底注区在骨架期间留空：没有任何可比价格，写「六城均无可比价格」会是错的结论。
        /// </summary>
        private void DrawSkeleton(Graphics g, int w)
        {
            DrawTableHead(g, w);

            int top = RowsTop;
            // 灰条宽度按各列内容性质预估（城市窄、买卖价中等、时间最短），只用几何关系
            int[] barWidths = { 60, 64, 64, 44 };
            for (int i = 0; i < _loadingCities; i++)
            {
                var rect = new Rectangle(PadX, top + i * RowHeight, w - PadX * 2, RowHeight);
                using (var br = new SolidBrush(i % 2 == 0 ? ZebraBg : ZebraBg2))
                    g.FillRectangle(br, rect);

                int cx = rect.X;
                for (int c = 0; c < 4; c++)
                {
                    int bw = barWidths[c];
                    int bx = c == 0 ? cx + 2 : cx + ColumnWidths[c] - bw - 4;
                    var bar = new Rectangle(bx, rect.Y + (RowHeight - 9) / 2, bw, 9);
                    using (var br = new SolidBrush(Th.Line)) g.FillRectangle(br, bar);
                    cx += ColumnWidths[c];
                }
            }
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
            DrawTableHead(g, w);

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

            DrawFooterOrHint(g, w, top + _rows.Count * RowHeight);
        }

        /// <summary>底注 vs M3 提示行的分派：一个位置只放一种，避免 28px 底注把 36px 提示行挤掉。</summary>
        private void DrawFooterOrHint(Graphics g, int w, int y)
        {
            if (_state == QuoteTableModel.CardState.AllSellEmpty) DrawSellEmptyHint(g, w, y);
            else DrawFooter(g, w, y);
        }

        /// <summary>
        /// M3 提示行（设计 §5.3）：六城卖出价全 0 = 亚服没人在挂收购单，卖不出去。
        /// 版式：warn 12% 实色底 + 一句陈述结论的文案 + 右侧两个 76×22 动作按钮（看 T4 / 看 T6）。
        ///
        /// 为什么是「陈述结论」而不是「暂无数据显示」：用户要的是能不能卖，答案就是不能，
        /// 含糊词会让人再去找别的页面确认。为什么必须给动作按钮：只说「卖不出去」是死路，
        /// 相邻阶级可能有收购单，下一步必须是可点的。
        /// </summary>
        private void DrawSellEmptyHint(Graphics g, int w, int y)
        {
            var r = new Rectangle(PadX, y, w - PadX * 2, QuoteTableModel.HintHeight);
            using (var pen = new Pen(Th.Line))
                g.DrawLine(pen, r.X, r.Y, r.Right, r.Y);
            using (var br = new SolidBrush(WarnBarBg)) g.FillRectangle(br, r);

            // 只画真有目的地的按钮：T4 已在最低阶时画「看 T4」等于给一个点了没反应的假按钮。
            var b1 = _adjLower > 0 ? AdjacentLowerRect(w, y) : Rectangle.Empty;
            var b2 = _adjUpper > 0 ? AdjacentUpperRect(w, y) : Rectangle.Empty;
            int buttonsLeft = b2.IsEmpty ? (b1.IsEmpty ? r.Right : b1.X) : b2.X;
            // 文案区到按钮左侧留 8px，避免长文案压到按钮
            var textRect = new Rectangle(r.X + 10, r.Y, buttonsLeft - r.X - 18, QuoteTableModel.HintHeight);
            TextRenderer.DrawText(g,
                "该物品在亚服暂无挂单，卖不出去。可看同系其他阶级是否有收购单。",
                _footFont, textRect, Th.Accent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.EndEllipsis);

            if (!b1.IsEmpty)
            {
                Th.StrokeRound(g, b1, 4, Th.Accent);
                TextRenderer.DrawText(g, "看 T" + _adjLower, _footFont, b1, Th.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            if (!b2.IsEmpty)
            {
                Th.StrokeRound(g, b2, 4, Th.Accent);
                TextRenderer.DrawText(g, "看 T" + _adjUpper, _footFont, b2, Th.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>「看 T4」按钮矩形（低阶方向）：与绘制、命中检测共用一份几何。</summary>
        private Rectangle AdjacentLowerRect(int w, int y)
        {
            return new Rectangle(w - PadX - 76 - 8 - 76, y + (QuoteTableModel.HintHeight - 22) / 2, 76, 22);
        }

        /// <summary>「看 T6」按钮矩形（高阶方向）。</summary>
        private Rectangle AdjacentUpperRect(int w, int y)
        {
            return new Rectangle(w - PadX - 76, y + (QuoteTableModel.HintHeight - 22) / 2, 76, 22);
        }

        /// <summary>
        /// 相邻阶级由外层（MarketTab）按当前家族的可用阶级下发：0 = 该方向没有相邻阶级。
        /// 为什么不在这里写死 4 和 6：T4 已经是家族最低阶时，「看 T4」是一个点了没有目的地的按钮。
        /// </summary>
        public void SetAdjacentTiers(int lower, int upper)
        {
            if (_adjLower == lower && _adjUpper == upper) return;
            _adjLower = lower;
            _adjUpper = upper;
            Invalidate();
        }

        /// <summary>M3 提示行顶部 y（= 表头下 + 行数 × 行高）。绘制与命中共用。</summary>
        private int HintTop
        {
            get { return RowsTop + _rows.Count * RowHeight; }
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

            // M3：卖列整列灰化（设计 §5.3）——六城都没收购单时，让卖列统一退成次要色，
            // 视觉上「这一列今天没有内容」；买列保持正常亮度，两类信息不混。
            bool sellDimmed = !isBuy && _state == QuoteTableModel.CardState.AllSellEmpty;

            // 卖出列全 0（M3）时整列灰化，文案陈述结论而不是含糊词
            string label = kind == QuoteCellKind.NoOrders ? "暂无挂单" : "无数据";
            Color labelColor = sellDimmed ? StaleTextColor
                : (kind == QuoteCellKind.NoOrders ? subColor : Th.Sub);
            TextRenderer.DrawText(g, label, _headFont, cell, labelColor,
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
            else if (_state == QuoteTableModel.CardState.AllSellEmpty)
            {
                // M3 提示行的「看 T4 / 看 T6」必须真能点：只画不响应等于假按钮。
                // 命中检测与绘制同源：只认当前真的存在的相邻阶级。
                bool hasLower = _adjLower > 0, hasUpper = _adjUpper > 0;
                var b1 = hasLower ? AdjacentLowerRect(w, HintTop) : Rectangle.Empty;
                var b2 = hasUpper ? AdjacentUpperRect(w, HintTop) : Rectangle.Empty;
                bool hitLower = hasLower && b1.Contains(e.Location);
                bool hitUpper = hasUpper && b2.Contains(e.Location);
                if ((hitLower || hitUpper) && AdjacentTierRequested != null)
                {
                    _wantAdjacent = hitLower;
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

    /// <summary>
    /// 联动位（设计 rev27 §3.5 / §七 / §六 状态矩阵「联动位」列）：620 × 28，落在报价卡下方。
    ///
    /// 它是**标注位，不是功能位**——规范原文「点击只弹一句 `该入口规划中，将与收益明细的物品行互链`，
    /// 不跳转、不展开」。所以这里不接任何跳转逻辑，只画一条入口行 + 一个外链图标。
    ///
    /// 模块边界（用户原话「跑商相关不要放到市场模块，市场只是查价的」）：
    /// 本控件只承载「入口 / 后续扩展」这一层，不承载任何算账内容——那些归跑商页。
    /// 文案由 QuoteTableModel 的常量给出，避免 UI 里散落字符串。
    ///
    /// 状态：M6 加载中 / M8 无旧数据灰化（规范 §六），其余状态亮色。
    /// </summary>
    public class MarketLinkRow : Control
    {
        private readonly Font _titleFont = Th.F(8.5f);
        private readonly Font _badgeFont = Th.F(7.5f);

        private bool _enabled = true;
        private bool _hover;
        private string _hint = QuoteTableModel.LinkRowClickHint;

        /// <summary>点击提示最近一次的文本（供调用方弹提示；也让「点了有反应」可被离线断言）。</summary>
        public string LastHint { get { return _hint; } }

        public MarketLinkRow()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Th.Bg;
            Size = new Size(QuoteTableModel.LinkRowWidth, QuoteTableModel.LinkRowHeight);
            Cursor = Cursors.Hand;
        }

        /// <summary>按报价卡状态灰化/点亮（规范 §六：M6 / M8 灰化）。</summary>
        public void SetEnabled(QuoteTableModel.CardState cardState)
        {
            ApplyEnabled(QuoteTableModel.LinkRowEnabled(cardState));
        }

        /// <summary>
        /// 按卡片给出的精确灰化判定设置（M6 / 真·M8 灰，M9 组合无匹配等业务空态仍亮）。
        /// 与 SetEnabled(CardState) 并存：后者是纯枚举映射，前者补上「同为 NoDataAtAll 但语义不同」的区分。
        /// </summary>
        public void SetDimmed(bool dimmed)
        {
            ApplyEnabled(!dimmed);
        }

        private void ApplyEnabled(bool enabled)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            Cursor = _enabled ? Cursors.Hand : Cursors.Default;
            if (!_enabled) _hover = false;
            Invalidate();
        }

        /// <summary>当前是否亮色（供状态断言）。</summary>
        public bool EnabledByState { get { return _enabled; } }

        protected override void OnMouseEnter(EventArgs e)
        {
            if (_enabled) { _hover = true; Invalidate(); }
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover) { _hover = false; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Th.Bg);
            int w = ClientSize.Width > 0 ? ClientSize.Width : QuoteTableModel.LinkRowWidth;

            // 设计 §3.5：左段 L5 取 text-3、右段 L6 取 text-4，这一段是标注位不是主信息，
            // 所以亮色态也不用主文字色；悬停转 accent 只给轻反馈，不改变「标注位」的性质。
            Color titleColor = _enabled ? (_hover ? Th.Accent : Th.Sub) : Th.Line;
            Color subColor = _enabled ? Th.Sub : Th.Line;

            var titleRect = new Rectangle(0, 0, 300, QuoteTableModel.LinkRowHeight);
            TextRenderer.DrawText(g, QuoteTableModel.LinkRowTitle, _titleFont, titleRect, titleColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.EndEllipsis);

            // 右端：16×16 外链图标 + 「后续扩展」标注（规范 §3.5 右端构成）
            int iconSize = 16;
            var iconRect = new Rectangle(w - 16 - iconSize, (QuoteTableModel.LinkRowHeight - iconSize) / 2,
                iconSize, iconSize);
            DrawExternalIcon(g, iconRect, subColor);
            TextRenderer.DrawText(g, QuoteTableModel.LinkRowBadge, _badgeFont,
                new Rectangle(iconRect.X - 8 - 64, 0, 64, QuoteTableModel.LinkRowHeight), subColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        /// <summary>16×16 外链图标：方框 + 右上出角箭头（不用字体图标，避免缺字形画出豆腐块）。</summary>
        private static void DrawExternalIcon(Graphics g, Rectangle r, Color color)
        {
            using (var pen = new Pen(color))
            {
                int box = 11;
                var boxRect = new Rectangle(r.X, r.Y + r.Height - box, box, box);
                g.DrawRectangle(pen, boxRect);

                // 出角箭头：从方框右上引出到图标右上角
                int ax = r.Right - 1, ay = r.Y + 1;
                g.DrawLine(pen, boxRect.Right - 1, boxRect.Y, ax, ay);
                g.DrawLine(pen, ax - 5, ay, ax, ay);
                g.DrawLine(pen, ax, ay, ax, ay + 5);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            // 规范 §3.5：点击只弹一句规划中文案，不跳转、不展开。
            if (_enabled) _hint = QuoteTableModel.LinkRowClickHint;
            base.OnClick(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _titleFont.Dispose();
                _badgeFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
