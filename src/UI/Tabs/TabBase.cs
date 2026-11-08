using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Tabs
{
    public interface ITab
    {
        void RefreshData();
    }

    /// <summary>功能页基类：自绘卡片 + 表格表头助手。</summary>
    public abstract class TabBase : UserControl, ITab
    {
        protected readonly Runtime Rt;
        private readonly List<Rectangle> _cards = new List<Rectangle>();
        private readonly Font _headFont;

        protected TabBase(Runtime runtime)
        {
            Rt = runtime;
            DoubleBuffered = true;
            BackColor = Th.Bg;
            _headFont = Th.F(8.25f);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw, true);
        }

        protected void Card(int x, int y, int w, int h)
        {
            _cards.Add(new Rectangle(x, y, w, h));
        }

        protected Label CardText(string text, int x, int y, int w, int h, Color color, float size, bool bold)
        {
            var label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.BackColor = Color.Transparent;
            label.ForeColor = color;
            label.Font = bold ? Th.FB(size) : Th.F(size);
            label.Location = new Point(x, y);
            label.Size = new Size(w, h);
            label.TextAlign = ContentAlignment.TopLeft;
            Controls.Add(label);
            return label;
        }

        protected void Heads(string[] titles, float[] weights, bool[] right, int x, int y, int w)
        {
            Heads(titles, weights, right, x, y, w, 0);
        }

        protected void Heads(string[] titles, float[] weights, bool[] right, int x, int y, int w, int offset)
        {
            float usable = w - offset;
            float total = 0f;
            for (int i = 0; i < weights.Length; i++) total += Math.Max(0.01f, weights[i]);
            float cursor = x + offset;
            for (int i = 0; i < titles.Length; i++)
            {
                float cw = usable * (Math.Max(0.01f, weights[i]) / total);
                var label = new Label();
                label.Text = titles[i];
                label.AutoSize = false;
                label.BackColor = Th.Bg;
                label.ForeColor = Th.Sub;
                label.Font = _headFont;
                label.Location = new Point((int)cursor, y);
                label.Size = new Size((int)Math.Max(20, cw - 6), 18);
                bool isRight = right != null && i < right.Length && right[i];
                label.TextAlign = isRight ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
                Controls.Add(label);
                cursor += cw;
            }
        }

        protected SimpleTable Table(float[] weights, bool[] right, int x, int y, int w, int h)
        {
            var table = new SimpleTable();
            table.Weights = weights;
            table.RightAlign = right;
            table.Location = new Point(x, y);
            table.Size = new Size(w, h);
            Controls.Add(table);
            return table;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Th.Bg);
            foreach (var r in _cards)
            {
                Th.FillRound(g, r, 10, Th.Card);
                Th.StrokeRound(g, r, 10, Th.Line);
            }
            base.OnPaint(e);
        }

        public virtual void RefreshData()
        {
        }
    }
}
