using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AlbionCompanion.Core;
using AlbionCompanion.Core.Income;
using AlbionCompanion.Core.Trade;

namespace AlbionCompanion.UI
{
    /// <summary>
    /// 常驻置顶的紧凑悬浮条（352×92），单击展开详细面板。
    /// 双模式（跑商规范 §5/§8.9）：
    /// ① 默认「收益」模式 —— 与既有实现完全一致的本次会话收益展示；
    /// ② 跑商导航激活时切换「路线」模式 —— 与主窗导航卡**共用同一个 NavigationState**
    ///    （静态单例），下一步只改状态，悬浮条与主窗各自订阅 Changed 重绘，不推不拉。
    /// </summary>
    public class OverlayForm : Form
    {
        private readonly Runtime _rt;
        private readonly Timer _timer;
        private bool _dragging;
        private bool _moved;
        private Point _originCursor;
        private Point _originForm;

        public event EventHandler ExpandRequested;
        public event EventHandler ExitRequested;

        private readonly Font _capFont;
        private readonly Font _bigFont;
        private readonly Font _lineFont;
        private readonly Font _hintFont;

        private const int OverlayWidth = 352;
        private const int OverlayHeight = 92;

        public OverlayForm(Runtime runtime)
        {
            _rt = runtime;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(OverlayWidth, OverlayHeight);
            BackColor = Th.Bg;
            Text = "Albion Companion";
            TopMost = _rt.Settings.AlwaysOnTop;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Opacity = Math.Max(0.45f, Math.Min(1f, _rt.Settings.OpacityPercent / 100f));

            _capFont = Th.F(8.25f);
            _bigFont = Th.FB(17f);
            _lineFont = Th.F(9f);
            _hintFont = Th.F(8f);

            ApplyRegion();

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate { Invalidate(); };
            _timer.Start();

            _rt.Changed += delegate
            {
                ApplySettings();
                Invalidate();
            };

            // 单一状态源：导航状态变化时悬浮条自己重绘（不靠主窗推送）
            NavigationState.Shared.Changed += OnNavigationChanged;

            BuildMenu();
        }

        public void ApplySettings()
        {
            TopMost = _rt.Settings.AlwaysOnTop;
            Opacity = Math.Max(0.45f, Math.Min(1f, _rt.Settings.OpacityPercent / 100f));
            Invalidate();
        }

        private void ApplyRegion()
        {
            Region = new Region(Th.Round(new Rectangle(0, 0, Width, Height), 18));
        }

        private void BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = Th.Card;
            menu.ForeColor = Th.Text;
            menu.Font = Th.F(9.5f);
            menu.ShowImageMargin = false;

            menu.Items.Add("展开详细面板", null, delegate { RaiseExpand(); });
            menu.Items.Add("重置本次会话", null, delegate { _rt.ResetSession(); });
            menu.Items.Add(new ToolStripSeparator());
            var topItem = new ToolStripMenuItem("窗口置顶");
            topItem.Checked = _rt.Settings.AlwaysOnTop;
            topItem.Click += delegate
            {
                _rt.Settings.AlwaysOnTop = !_rt.Settings.AlwaysOnTop;
                _rt.ApplySettings();
                topItem.Checked = _rt.Settings.AlwaysOnTop;
                ApplySettings();
            };
            menu.Items.Add(topItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate
            {
                var handler = ExitRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            });

            ContextMenuStrip = menu;
        }

        private void RaiseExpand()
        {
            var handler = ExpandRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void OnNavigationChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _moved = false;
                _originCursor = Cursor.Position;
                _originForm = Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            var now = Cursor.Position;
            int dx = now.X - _originCursor.X;
            int dy = now.Y - _originCursor.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 4) _moved = true;
            Location = new Point(_originForm.X + dx, _originForm.Y + dy);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            _dragging = false;
            if (!_moved) RaiseExpand();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Th.Bg);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Th.FillRound(g, r, 18, Color.FromArgb(21, 24, 30));
            Th.StrokeRound(g, new Rectangle(1, 1, Width - 3, Height - 3), 17, Th.Line);

            // 双模式：导航激活时显示路线，否则保持原「本次会话收益」展示不变
            var nav = NavigationState.Shared;
            if (nav.IsActive)
            {
                PaintNavigation(g, nav);
            }
            else
            {
                PaintIncome(g);
            }
        }

        /// <summary>原收益展示（保持不变，避免破坏既有收益口径）。</summary>
        private void PaintIncome(Graphics g)
        {
            Th.FillRound(g, new Rectangle(12, 22, 3, Height - 44), 1, Th.Accent);

            TextRenderer.DrawText(g, "收益", _capFont, new Point(26, 16), Th.Sub,
                TextFormatFlags.NoPrefix);

            // R4/C5：总收益改读 IncomeTracker（只含已确认条目），不再用 500 条截断列表求和
            // 复核修复：悬浮条是「本次会话」口径 —— 必须用 SessionTotal（排除启动时从 JSONL 恢复的历史，
            // 否则新会话一启动就显示历史累计，时薪= 历史收益/会话时长，数字每秒剧烈变化）
            var inc = IncomeTracker.GetSummary();
            long total = inc.SessionTotal;
            TextRenderer.DrawText(g, Th.Money(total), _bigFont, new Point(24, 32), Th.Accent,
                TextFormatFlags.NoPrefix);

            string startText = "开始于 " + _rt.SessionStart.ToString("HH:mm");
            TextRenderer.DrawText(g, startText, _hintFont, new Point(26, Height - 22), Th.Sub,
                TextFormatFlags.NoPrefix);

            using (var pen = new Pen(Th.Line))
            {
                g.DrawLine(pen, 188, 20, 188, Height - 20);
            }

            TextRenderer.DrawText(g, "时长 " + SessionClock.Format(_rt.Elapsed), _lineFont,
                new Point(204, 14), Th.Text, TextFormatFlags.NoPrefix);

            // R4/C5：时薪入参与大数字同源（IncomeTracker 本次会话合计），避免两处口径分叉
            double perHour = SessionClock.PerHour(inc.SessionTotal, _rt.Elapsed);
            string rateText = perHour > 0 ? "时薪 " + Th.Money((long)perHour) + "/时" : "等待收益...";
            TextRenderer.DrawText(g, rateText, _lineFont, new Point(204, 38), Th.Good,
                TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, "点击展开 · 右键菜单", _hintFont, new Point(204, Height - 22),
                Th.Sub, TextFormatFlags.NoPrefix);
        }

        /// <summary>路线模式（与主窗导航卡共用 NavigationState.Shared）。</summary>
        private void PaintNavigation(Graphics g, NavigationState nav)
        {
            bool red = nav.IsCurrentRed;
            Color accent = red ? Th.Bad : Th.Accent;

            // 三处告警之一：左侧竖条
            Th.FillRound(g, new Rectangle(12, 22, 3, Height - 44), 1, accent);

            string kicker = "路线 · " + (string.IsNullOrEmpty(nav.FormLabel) ? "导航" : nav.FormLabel);
            TextRenderer.DrawText(g, kicker, _capFont, new Point(26, 16),
                red ? Th.Bad : Th.Sub, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            TextRenderer.DrawText(g, nav.Title, _bigFont, new Rectangle(24, 30, 156, 28), Th.Text,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            TextRenderer.DrawText(g, nav.CurrentText, _lineFont,
                new Rectangle(26, 60, 158, 18), red ? Th.Bad : Th.Sub,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, nav.IsCurrentRed ? "⚠ 红区" : nav.ProgressText, _hintFont,
                new Rectangle(26, Height - 22, 156, 16), red ? Th.Bad : Th.Sub,
                TextFormatFlags.NoPrefix);

            using (var pen = new Pen(Th.Line))
            {
                g.DrawLine(pen, 188, 20, 188, Height - 20);
            }

            TextRenderer.DrawText(g, nav.ProgressText, _lineFont, new Point(204, 14), Th.Text,
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, nav.NextHint, _hintFont,
                new Rectangle(204, 38, 140, 16), red ? Th.Bad : Th.Sub,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g,
                nav.Arrived ? "导航完成" : "剩余预估 " + Th.Money(nav.EstimatedProfit) + " 银",
                _hintFont, new Point(204, Height - 22), red ? Th.Bad : Th.Accent,
                TextFormatFlags.NoPrefix);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ApplyRegion();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { NavigationState.Shared.Changed -= OnNavigationChanged; } catch (ObjectDisposedException) { }
                _timer.Stop();
                _timer.Dispose();
                _capFont.Dispose();
                _bigFont.Dispose();
                _lineFont.Dispose();
                _hintFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
