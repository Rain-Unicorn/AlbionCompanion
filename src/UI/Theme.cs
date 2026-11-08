using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace AlbionCompanion.UI
{
    /// <summary>统一暗色主题、绘图工具与格式化工具。</summary>
    public static class Th
    {
        public static readonly Color Bg = Color.FromArgb(15, 17, 21);
        public static readonly Color Card = Color.FromArgb(23, 26, 32);
        public static readonly Color CardHi = Color.FromArgb(31, 35, 43);
        public static readonly Color Line = Color.FromArgb(43, 49, 59);
        public static readonly Color Text = Color.FromArgb(233, 237, 243);
        public static readonly Color Sub = Color.FromArgb(137, 145, 158);
        public static readonly Color Accent = Color.FromArgb(216, 169, 74);
        public static readonly Color AccentDim = Color.FromArgb(122, 216, 169, 74);
        public static readonly Color Good = Color.FromArgb(94, 200, 148);
        public static readonly Color Bad = Color.FromArgb(226, 108, 108);
        public static readonly Color Info = Color.FromArgb(110, 168, 255);

        public static Font F(float size)
        {
            return new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Point);
        }

        public static Font FB(float size)
        {
            return new Font("Microsoft YaHei UI", size, FontStyle.Bold, GraphicsUnit.Point);
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || r.Width <= 0 || r.Height <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color color)
        {
            using (var path = Round(r, radius))
            using (var brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
            }
        }

        public static void StrokeRound(Graphics g, Rectangle r, int radius, Color color)
        {
            using (var path = Round(r, radius))
            using (var pen = new Pen(color))
            {
                g.DrawPath(pen, path);
            }
        }

        public static string Money(long value)
        {
            long abs = Math.Abs(value);
            if (abs >= 1000000000L) return string.Format("{0:0.00}B", value / 1000000000.0);
            if (abs >= 1000000L) return string.Format("{0:0.00}M", value / 1000000.0);
            if (abs >= 1000L) return string.Format("{0:0.0}K", value / 1000.0);
            return value.ToString("N0");
        }

        public static string Num(long value)
        {
            return value.ToString("N0");
        }

        public static string Price(long value)
        {
            return value <= 0 ? "—" : value.ToString("N0");
        }

        public static string DateText(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "—";
            DateTime parsed;
            if (!DateTime.TryParse(iso, out parsed)) return "—";
            if (parsed.Year < 2000) return "—";
            return parsed.ToString("MM-dd HH:mm");
        }

        public static string Cape(string text, int max)
        {
            if (text == null) return "";
            if (text.Length <= max) return text;
            return text.Substring(0, max - 1) + "…";
        }
    }
}
