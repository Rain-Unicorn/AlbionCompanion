using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI
{
    /// <summary>暗色右键菜单渲染器。</summary>
    public class DarkMenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Th.Card);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var pen = new Pen(Th.Line))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected && e.Item.Enabled)
            {
                var r = new Rectangle(4, 0, e.Item.Width - 8, e.Item.Height);
                Th.FillRound(e.Graphics, r, 4, Th.CardHi);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Th.Text : Th.Sub;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (var pen = new Pen(Th.Line))
            {
                e.Graphics.DrawLine(pen, 10, e.Item.Height / 2, e.Item.Width - 10, e.Item.Height / 2);
            }
        }
    }

    /// <summary>左侧导航项。</summary>
    public class NavItem : Control
    {
        public bool Selected;
        private bool _hover;

        public NavItem()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Th.Bg;
            Cursor = Cursors.Hand;
            Font = Th.F(10f);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Th.Bg);

            var r = new Rectangle(2, 2, Width - 4, Height - 4);
            if (Selected)
            {
                Th.FillRound(g, r, 8, Th.CardHi);
                Th.FillRound(g, new Rectangle(r.X + 1, r.Y + 12, 3, r.Height - 24), 2, Th.Accent);
            }
            else if (_hover)
            {
                Th.FillRound(g, r, 8, Color.FromArgb(27, 31, 38));
            }

            var textColor = Selected ? Th.Text : (_hover ? Th.Text : Th.Sub);
            TextRenderer.DrawText(g, Text, Selected ? Th.FB(10f) : Th.F(10f),
                new Rectangle(r.X + 12, r.Y, r.Width - 18, r.Height), textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>右上角小按钮（收起 / 关闭）。</summary>
    public class IconBtn : Control
    {
        private bool _hover;

        public IconBtn()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Th.Bg;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Th.Bg);
            if (_hover) Th.FillRound(g, new Rectangle(0, 0, Width, Height), 5, Th.CardHi);
            TextRenderer.DrawText(g, Text, Th.F(9f),
                new Rectangle(0, 0, Width, Height), _hover ? Th.Text : Th.Sub,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>可滚动的自绘表格。</summary>
    public class SimpleTable : UserControl
    {
        public int RowHeight = 32;
        public float[] Weights = new float[] { 1f };
        public bool[] RightAlign = new bool[] { false };
        public List<string[]> Rows = new List<string[]>();
        public List<Image> RowImages;
        public string EmptyText = "暂无数据";

        private readonly Font _rowFont;
        private readonly Font _emptyFont;
        private int _scrollY;
        private int _maxScroll;
        private bool _thumbDrag;
        private int _thumbDragOffset;

        public SimpleTable()
        {
            DoubleBuffered = true;
            BackColor = Th.Bg;
            _rowFont = Th.F(9.5f);
            _emptyFont = Th.F(9f);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw, true);
        }

        public int ScrollY { get { return _scrollY; } }

        private int ScrollWidth { get { return 8; } }

        public void SetRows(List<string[]> rows)
        {
            SetRows(rows, null);
        }

        public void SetRows(List<string[]> rows, List<Image> images)
        {
            Rows = rows == null ? new List<string[]>() : rows;
            RowImages = images;
            int total = Rows.Count * RowHeight;
            int visible = ClientSize.Height;
            _maxScroll = Math.Max(0, total - visible);
            if (_scrollY > _maxScroll) _scrollY = _maxScroll;
            Invalidate();
        }

        private bool HasScroll { get { return _maxScroll > 0; } }

        private Rectangle ThumbRect()
        {
            int w = ScrollWidth;
            int h = ClientSize.Height;
            if (!HasScroll) return Rectangle.Empty;
            int trackH = h - 8;
            int thumbH = Math.Max(24, (int)((float)h / (Rows.Count * RowHeight) * trackH));
            int y = 4 + (int)((float)_scrollY / _maxScroll * (trackH - thumbH));
            return new Rectangle(ClientSize.Width - w, y, w - 2, thumbH);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scrollY = Math.Max(0, Math.Min(_maxScroll, _scrollY - e.Delta / 2));
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var tr = ThumbRect();
            if (HasScroll && tr.Contains(e.Location))
            {
                _thumbDrag = true;
                _thumbDragOffset = e.Y - tr.Y;
            }
            else if (HasScroll && e.X >= ClientSize.Width - ScrollWidth)
            {
                // click track: page up/down
                if (e.Y < tr.Y) _scrollY = Math.Max(0, _scrollY - ClientSize.Height);
                else _scrollY = Math.Min(_maxScroll, _scrollY + ClientSize.Height);
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_thumbDrag) return;
            int trackH = ClientSize.Height - 8;
            var tr = ThumbRect();
            int y = e.Y - _thumbDragOffset - 4;
            float ratio = trackH > tr.Height ? (float)y / (trackH - tr.Height) : 0;
            _scrollY = Math.Max(0, Math.Min(_maxScroll, (int)(ratio * _maxScroll)));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _thumbDrag = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Th.Bg);

            if (Rows.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, _emptyFont,
                    new Rectangle(0, 44, Math.Max(0, ClientSize.Width), 24),
                    Th.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
                return;
            }

            int pad = 6;
            int offset = _scrollY;
            bool hasIcons = RowImages != null && RowImages.Count > 0;
            int iconSpace = hasIcons ? 36 : 0;
            int scrollW = HasScroll ? ScrollWidth : 0;
            int usable = Math.Max(10, ClientSize.Width - pad * 2 - iconSpace - scrollW);
            float totalWeight = 0f;
            for (int i = 0; i < Weights.Length; i++) totalWeight += Math.Max(0.01f, Weights[i]);

            for (int i = 0; i < Rows.Count; i++)
            {
                int y = i * RowHeight - offset;
                if (y + RowHeight < 0 || y > ClientSize.Height) continue;

                if (hasIcons && i < RowImages.Count && RowImages[i] != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(RowImages[i], new Rectangle(pad, y + 3, 26, 26));
                }

                float x = pad + iconSpace;
                string[] cells = Rows[i];
                for (int c = 0; c < Weights.Length; c++)
                {
                    float w = usable * (Math.Max(0.01f, Weights[c]) / totalWeight);
                    string text = cells != null && c < cells.Length ? cells[c] : "";
                    bool right = RightAlign != null && c < RightAlign.Length && RightAlign[c];
                    var rect = new Rectangle((int)x, y, (int)Math.Max(0, w - 6), RowHeight);
                    var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                                | TextFormatFlags.EndEllipsis
                                | (right ? TextFormatFlags.Right : TextFormatFlags.Left);
                    var color = c == 0 ? Th.Text : Th.Sub;
                    TextRenderer.DrawText(g, text, _rowFont, rect, color, flags);
                    x += w;
                }

                using (var pen = new Pen(Th.Line))
                {
                    g.DrawLine(pen, pad, y + RowHeight - 1, ClientSize.Width - pad - scrollW, y + RowHeight - 1);
                }
            }

            // 自绘深色滚动条
            if (HasScroll)
            {
                int sx = ClientSize.Width - ScrollWidth;
                using (var track = new SolidBrush(Th.Card))
                    g.FillRectangle(track, sx, 4, ScrollWidth - 2, ClientSize.Height - 8);
                var tr = ThumbRect();
                using (var thumb = new SolidBrush(Th.CardHi))
                    g.FillRectangle(thumb, tr.X, tr.Y, tr.Width, tr.Height);
            }
        }
    }

    /// <summary>横向条形排行图。</summary>
    public class Bars : Control
    {
        public class BarItem
        {
            public string Label;
            public string ValueText;
            public double Value;
            public Color Color;
        }

        public List<BarItem> Items = new List<BarItem>();
        public string EmptyText = "暂无数据";
        public int ValueWidth = 74;
        private readonly Font _font;
        private readonly Font _valueFont;

        public Bars()
        {
            DoubleBuffered = true;
            BackColor = Th.Bg;
            _font = Th.F(9f);
            _valueFont = Th.FB(9f);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw, true);
        }

        public void SetItems(List<BarItem> items)
        {
            Items = items == null ? new List<BarItem>() : items;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Th.Bg);

            if (Items.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, _font,
                    new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Th.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            double max = 0;
            for (int i = 0; i < Items.Count; i++) if (Items[i].Value > max) max = Items[i].Value;
            if (max <= 0) max = 1;

            int labelW = 84;
            int valueW = ValueWidth;
            int barAreaW = Math.Max(20, ClientSize.Width - labelW - valueW - 12);

            for (int i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                int y = 4 + i * 32;
                if (y + 26 > ClientSize.Height) break;

                TextRenderer.DrawText(g, Th.Cape(item.Label, 8), _font,
                    new Rectangle(0, y, labelW - 8, 20), Th.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);

                var track = new Rectangle(labelW, y + 6, barAreaW, 8);
                Th.FillRound(g, track, 4, Color.FromArgb(34, 38, 46));

                double ratio = item.Value / max;
                if (ratio < 0) ratio = 0;
                if (ratio > 1) ratio = 1;
                int fillW = (int)(barAreaW * ratio);
                if (fillW > 0)
                {
                    var fill = new Rectangle(labelW, y + 6, Math.Max(3, fillW), 8);
                    Th.FillRound(g, fill, 4, item.Color);
                }

                TextRenderer.DrawText(g, item.ValueText, _valueFont,
                    new Rectangle(labelW + barAreaW + 8, y, valueW - 8, 20), item.Color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>数据卡片。</summary>
    public class StatCard : Control
    {
        public string Caption = "";
        public string Value = "";
        public string Hint = "";
        public Color ValueColor = Th.Accent;
        private readonly Font _capFont;
        private readonly Font _valFont;
        private readonly Font _hintFont;

        public StatCard()
        {
            DoubleBuffered = true;
            BackColor = Th.Bg;
            _capFont = Th.F(8.5f);
            _valFont = Th.FB(16f);
            _hintFont = Th.F(8f);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw, true);
        }

        public void SetData(string caption, string value, string hint)
        {
            Caption = caption;
            Value = value;
            Hint = hint;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Th.Bg);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Th.FillRound(g, r, 10, Th.Card);
            Th.StrokeRound(g, r, 10, Th.Line);

            TextRenderer.DrawText(g, Caption, _capFont, new Rectangle(14, 10, Width - 28, 16), Th.Sub,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Value, _valFont, new Rectangle(14, 28, Width - 92, 28), ValueColor,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            if (!string.IsNullOrEmpty(Hint))
            {
                TextRenderer.DrawText(g, Hint, _hintFont, new Rectangle(14, Height - 24, Width - 20, 16), Th.Sub,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }
    }

    /// <summary>统一的控件构造助手，保证暗色风格一致。</summary>
    public static class Ui
    {
        public static TextBox Input(int x, int y, int w, int h)
        {
            var box = new TextBox();
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Th.CardHi;
            box.ForeColor = Th.Text;
            box.Font = Th.F(9.5f);
            box.Location = new Point(x, y);
            box.Size = new Size(w, h);
            return box;
        }

        public static Button Primary(string text, int x, int y, int w, int h)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 186, 96);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(196, 152, 62);
            b.BackColor = Th.Accent;
            b.ForeColor = Color.FromArgb(26, 21, 10);
            b.Font = Th.FB(9.5f);
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            b.Cursor = Cursors.Hand;
            return b;
        }

        public static Button Secondary(string text, int x, int y, int w, int h)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 47, 57);
            b.BackColor = Th.CardHi;
            b.ForeColor = Th.Text;
            b.Font = Th.F(9.5f);
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            b.Cursor = Cursors.Hand;
            return b;
        }

        public static Label Caption(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.BackColor = Th.Bg;
            l.ForeColor = Th.Sub;
            l.Font = Th.F(8.5f);
            l.Location = new Point(x, y);
            return l;
        }

        public static Label Title(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.BackColor = Th.Bg;
            l.ForeColor = Th.Text;
            l.Font = Th.FB(11f);
            l.Location = new Point(x, y);
            return l;
        }

        public static Label Value(string text, int x, int y, Color color)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.BackColor = Th.Bg;
            l.ForeColor = color;
            l.Font = Th.FB(10f);
            l.Location = new Point(x, y);
            return l;
        }

        public static ComboBox Dropdown(string[] items, int x, int y, int w, int h)
        {
            var cb = new ComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.FlatStyle = FlatStyle.Flat;
            cb.BackColor = Th.CardHi;
            cb.ForeColor = Th.Text;
            cb.Font = Th.F(9.5f);
            cb.Items.AddRange(items);
            cb.SelectedIndex = 0;
            cb.Location = new Point(x, y);
            cb.Size = new Size(w, h);
            return cb;
        }

        public static CheckBox Check(string text, int x, int y, int w, bool isChecked)
        {
            var c = new CheckBox();
            c.Text = text;
            c.AutoSize = false;
            c.BackColor = Th.Bg;
            c.ForeColor = Th.Sub;
            c.Font = Th.F(9f);
            c.Checked = isChecked;
            c.Location = new Point(x, y);
            c.Size = new Size(w, 22);
            return c;
        }

        public static Panel Divider(int x, int y, int w)
        {
            var p = new Panel();
            p.BackColor = Th.Line;
            p.Location = new Point(x, y);
            p.Size = new Size(w, 1);
            return p;
        }
    }

    /// <summary>物品图标：从 render.albiononline.com 拉取，按 baseId+品质缓存到本地。</summary>
    public static class ItemIcons
    {
        private static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();
        private static readonly object _lock = new object();
        private static string _dir;
        /// <summary>有新图标下载完成时触发（UI 线程外）。</summary>
        public static event Action IconsReady;

        public static string CacheDir
        {
            get
            {
                if (_dir == null)
                {
                    _dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "AlbionCompanion", "icons");
                    try { Directory.CreateDirectory(_dir); } catch { }
                }
                return _dir;
            }
        }

        private static string KeyOf(string baseId, int quality)
        {
            return baseId.ToLowerInvariant() + "_" + Math.Max(1, quality);
        }

        /// <summary>同步返回已缓存的图标；缺失时返回 null 并在后台线程下载。</summary>
        public static Bitmap Get(string baseId, int quality)
        {
            if (string.IsNullOrEmpty(baseId)) return null;
            string key = KeyOf(baseId, quality);
            lock (_lock)
            {
                Bitmap cached;
                if (_cache.TryGetValue(key, out cached)) return cached;
            }

            string file = Path.Combine(CacheDir, key + ".png");
            if (File.Exists(file))
            {
                try
                {
                    using (var raw = new Bitmap(file))
                    {
                        var bmp = new Bitmap(raw);
                        lock (_lock) _cache[key] = bmp;
                        return bmp;
                    }
                }
                catch { }
            }

            DownloadAsync(baseId, quality);
            return null;
        }

        private static void DownloadAsync(string baseId, int quality)
        {
            string key = KeyOf(baseId, quality);
            string file = Path.Combine(CacheDir, key + ".png");
            var thread = new Thread(delegate()
            {
                try
                {
                    string url = ItemCatalog.BuildIconUrl(baseId, quality);
                    var client = new WebClient();
                    client.Headers.Add("User-Agent", "AlbionCompanion/1.0");
                    byte[] bytes = client.DownloadData(url);
                    File.WriteAllBytes(file, bytes);
                    using (var ms = new MemoryStream(bytes))
                    using (var raw = new Bitmap(ms))
                    {
                        lock (_lock) _cache[key] = new Bitmap(raw);
                    }
                    if (IconsReady != null) try { IconsReady(); } catch { }
                }
                catch { /* 图标缺失不影响表格 */ }
            });
            thread.IsBackground = true;
            thread.Start();
        }
    }
}
