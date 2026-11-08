using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AlbionCompanion.Core;
using AlbionCompanion.UI.Tabs;

namespace AlbionCompanion.UI
{
    /// <summary>展开后的详细面板：左侧导航 + 右侧功能区。</summary>
    public class PanelForm : Form
    {
        private const int RailWidth = 130;
        private const int HeaderHeight = 52;
        private const int FooterHeight = 38;
        private const int ContentLeft = 124;
        private const int ContentMargin = 16;

        private readonly Runtime _rt;
        private readonly NavItem[] _navs = new NavItem[12];
        private readonly Control[] _views = new Control[12];
        private readonly Panel _host;
        private int _current;

        private readonly string[] _titles = { "总览", "收益", "战斗", "附近玩家", "市场", "跑商", "采集", "本地库", "自动更新", "实时", "配装", "设置" };

        private bool _dragging;
        private Point _originCursor;
        private Point _originForm;

        public event EventHandler CollapseRequested;

        /// <summary>仅供离线预览使用：显示窗口但不抢焦点。</summary>
        public static bool PreviewMode = false;

        protected override bool ShowWithoutActivation
        {
            get { return PreviewMode; }
        }

        public int CurrentIndex
        {
            get { return _current; }
        }

        public PanelForm(Runtime runtime)
        {
            _rt = runtime;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(760, 830);
            BackColor = Th.Bg;
            ShowInTaskbar = false;
            TopMost = _rt.Settings.AlwaysOnTop;
            DoubleBuffered = true;
            Text = "Albion Companion";

            BuildHeader();
            BuildRail();
            _host = new Panel();
            _host.Location = new Point(ContentLeft, HeaderHeight + 4);
            _host.Size = new Size(Width - ContentLeft - ContentMargin, Height - HeaderHeight - FooterHeight);
            _host.BackColor = Th.Bg;
            Controls.Add(_host);

            BuildViews();
            BuildFooter();
            UpdateRegion();

            _rt.Changed += delegate
            {
                if (Visible) RefreshActive();
            };
        }

        private void UpdateRegion()
        {
            Region = new Region(Th.Round(new Rectangle(0, 0, Width, Height), 14));
        }

        private void BuildHeader()
        {
            var collapse = new IconBtn();
            collapse.Text = "收起";
            collapse.Location = new Point(Width - 104, 14);
            collapse.Size = new Size(62, 26);
            collapse.Click += delegate
            {
                var handler = CollapseRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            Controls.Add(collapse);

            var close = new IconBtn();
            close.Text = "✕";
            close.Location = new Point(Width - 40, 14);
            close.Size = new Size(26, 26);
            close.Click += delegate
            {
                var handler = CollapseRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            Controls.Add(close);
        }

        private void BuildRail()
        {
            for (int i = 0; i < _titles.Length; i++)
            {
                var nav = new NavItem();
                nav.Text = _titles[i];
                nav.Location = new Point(10, HeaderHeight + 14 + i * 52);
                nav.Size = new Size(RailWidth - 24, 46);
                int index = i;
                nav.Click += delegate { SelectTab(index); };
                Controls.Add(nav);
                _navs[i] = nav;
            }
        }

        private void BuildViews()
        {
            _views[0] = new OverviewTab(_rt);
            _views[1] = new IncomeTab(_rt);
            _views[2] = new CombatTab(_rt);
            _views[3] = new NearbyPlayersTab(_rt);
            _views[4] = new MarketTab(_rt);
            _views[5] = new TradeTab(_rt);
            _views[6] = new HarvestTab(_rt);
            _views[7] = new LocalDbTab(_rt);
            _views[8] = new AutoUpdateTab(_rt);
            _views[9] = new LiveTab(_rt);
            _views[10] = new BuildTab(_rt);
            _views[11] = new SettingsTab(_rt);

            for (int i = 0; i < _views.Length; i++)
            {
                var view = _views[i];
                view.Dock = DockStyle.Fill;
                view.Visible = false;
                _host.Controls.Add(view);
            }
        }

        private void BuildFooter()
        {
            var note = new Label();
            note.Text = "数据源：Albion Data Project 公共行情接口　·　实时抓包统计战斗/收益/采集　·　安全无注入";
            note.AutoSize = false;
            note.BackColor = Th.Bg;
            note.ForeColor = Th.Sub;
            note.Font = Th.F(8f);
            note.TextAlign = ContentAlignment.MiddleLeft;
            note.Location = new Point(ContentLeft, Height - FooterHeight + 6);
            note.Size = new Size(Width - ContentLeft - ContentMargin, 20);
            Controls.Add(note);
        }

        public void SelectTab(int index)
        {
            if (index < 0 || index >= _views.Length) return;
            _current = index;
            for (int i = 0; i < _views.Length; i++)
            {
                _views[i].Visible = i == index;
                _navs[i].Selected = i == index;
                _navs[i].Invalidate();
            }
            RefreshActive();
        }

        private void RefreshActive()
        {
            var tab = _views[_current] as ITab;
            if (tab != null) tab.RefreshData();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            SelectTab(_current);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Th.Bg);

            Th.StrokeRound(g, new Rectangle(0, 0, Width - 1, Height - 1), 14, Th.Line);

            Th.FillRound(g, new Rectangle(20, 16, 5, 22), 2, Th.Accent);
            TextRenderer.DrawText(g, "ALBION COMPANION", Th.FB(11.5f), new Point(34, 12), Th.Text,
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "阿尔比恩助手 · 本地收益 / 战斗 / 市场", Th.F(8.25f),
                new Point(34, 31), Th.Sub, TextFormatFlags.NoPrefix);

            using (var pen = new Pen(Th.Line))
            {
                g.DrawLine(pen, 0, HeaderHeight, Width, HeaderHeight);
                g.DrawLine(pen, RailWidth - 6, HeaderHeight, RailWidth - 6,
                    Height - FooterHeight);
            }

            TextRenderer.DrawText(g, "热键 " + _rt.Settings.Hotkey, Th.F(8f),
                new Point(16, Height - FooterHeight + 20), Th.Sub, TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.Y < HeaderHeight)
            {
                _dragging = true;
                _originCursor = Cursor.Position;
                _originForm = Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            var now = Cursor.Position;
            Location = new Point(_originForm.X + now.X - _originCursor.X,
                                 _originForm.Y + now.Y - _originCursor.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                var handler = CollapseRequested;
                if (handler != null) handler(this, EventArgs.Empty);
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
