using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AlbionCompanion.Core;
using AlbionCompanion.Core.DamageMeter;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>双缓冲 Panel，防止闪烁。</summary>
    public class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            UpdateStyles();
        }
    }

    /// <summary>战斗统计：按伤害输出者汇总，带进度条和实时DPS。</summary>
    public class CombatTab : TabBase
    {
        private readonly Button _clearBtn;
        private readonly PillButton _btnAll, _btnPve, _btnPvp;
        private readonly DoubleBufferedPanel _panel;
        private readonly Timer _refresh;

        public CombatTab(Runtime runtime)
            : base(runtime)
        {
            // 清空统计按钮
            _clearBtn = new Button();
            _clearBtn.Text = "清空";
            _clearBtn.Location = new Point(510, 12);
            _clearBtn.Size = new Size(58, 24);
            _clearBtn.FlatStyle = FlatStyle.Flat;
            _clearBtn.FlatAppearance.BorderColor = Th.Line;
            _clearBtn.FlatAppearance.BorderSize = 1;
            _clearBtn.BackColor = Th.Card;
            _clearBtn.ForeColor = Th.Sub;
            _clearBtn.Font = Th.F(8.5f);
            _clearBtn.Cursor = Cursors.Hand;
            _clearBtn.FlatAppearance.MouseOverBackColor = Th.CardHi;
            _clearBtn.Click += delegate { GameEvents.DamageTracker.Clear(); GameEvents.FireUpdate(); Render(); };
            Controls.Add(_clearBtn);

            // 筛选 pill 按钮
            _btnAll = new PillButton("全部", 14, 56);
            _btnPve = new PillButton("PVE", 76, 56);
            _btnPvp = new PillButton("PVP", 128, 56);
            _btnAll.Clicked += () => SetFilter(DamageStatsTracker.DamageFilter.All);
            _btnPve.Clicked += () => SetFilter(DamageStatsTracker.DamageFilter.Pve);
            _btnPvp.Clicked += () => SetFilter(DamageStatsTracker.DamageFilter.Pvp);
            Controls.Add(_btnAll); Controls.Add(_btnPve); Controls.Add(_btnPvp);

            // 主面板
            _panel = new DoubleBufferedPanel();
            _panel.Location = new Point(0, 90);
            _panel.Size = new Size(580, 478);
            _panel.BackColor = Th.Bg;
            _panel.Paint += Panel_Paint;
            Controls.Add(_panel);

            GameEvents.OnUpdate += delegate { try { if (IsHandleCreated) BeginInvoke(new MethodInvoker(Render)); } catch { } };

            _refresh = new Timer();
            _refresh.Interval = 1000;
            _refresh.Tick += delegate { Render(); };
            _refresh.Start();
            Render();
        }

        private void Panel_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Th.Bg);

            int w = _panel.Width;
            int pad = 14;

            // ===== 顶部大数字 =====
            long dmg = GameEvents.TotalDamage;
            long heal = GameEvents.TotalHealing;
            int kills = GameEvents.KillCount;

            // 小标签
            TextRenderer.DrawText(g, "— COMBAT TELEMETRY", Th.F(7.5f),
                new Rectangle(pad, 4, 300, 14), Th.Accent, TextFormatFlags.Left);
            TextRenderer.DrawText(g, "战斗统计", Th.FB(16f),
                new Rectangle(pad, 18, 300, 26), Th.Text, TextFormatFlags.Left);

            // 大数字行
            int statX = pad;
            DrawBigStat(g, ref statX, "伤害", Th.Money(dmg), Th.Bad, 160);
            DrawBigStat(g, ref statX, "治疗", Th.Money(heal), Th.Good, 130);
            DrawBigStat(g, ref statX, "击杀", kills.ToString(), Th.Text, 90);

            // ===== 伤害输出卡片 =====
            int cardY = 82;
            Card(g, pad, cardY, w - pad * 2, 190);

            // 卡片标题
            DrawIconBadge(g, pad + 12, cardY + 10, Th.Bad);
            TextRenderer.DrawText(g, "伤害输出", Th.FB(10f),
                new Rectangle(pad + 32, cardY + 8, 200, 18), Th.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "DAMAGE OUT", Th.F(7f),
                new Rectangle(pad + 110, cardY + 10, 200, 14), Th.Sub, TextFormatFlags.Left);

            var allDamage = GameEvents.DamageTracker.GetTopDamage();
            var topDamage = GameEvents.IsInParty
                ? allDamage.FindAll(d => GameEvents.IsPartyMember(d.PlayerName))
                : allDamage;

            int listY = cardY + 36;
            if (topDamage.Count == 0)
            {
                TextRenderer.DrawText(g, "暂无伤害数据", Th.F(9f),
                    new Rectangle(pad + 12, listY + 8, w - pad * 2 - 24, 16), Th.Sub, TextFormatFlags.Left);
            }
            else
            {
                long totalDmg = 0;
                foreach (var d in topDamage) totalDmg += d.TotalDamage;
                if (totalDmg <= 0) totalDmg = 1;

                int rh = 38;
                for (int i = 0; i < topDamage.Count && i < 5; i++)
                {
                    var entry = topDamage[i];
                    int y = listY + i * rh;
                    float pct = (float)entry.TotalDamage / totalDmg;

                    // 左侧红色竖条
                    var vbar = new Rectangle(pad + 12, y + 2, 3, rh - 10);
                    Th.FillRound(g, vbar, 1, Th.Bad);

                    // 名字
                    TextRenderer.DrawText(g, entry.PlayerName, Th.FB(9f),
                        new Rectangle(pad + 22, y, 140, 14), Th.Text, TextFormatFlags.Left);

                    // 伤害数字+百分比 右对齐
                    string dmgText = Th.Money(entry.TotalDamage) + "  " + (pct * 100).ToString("0") + "%";
                    TextRenderer.DrawText(g, dmgText, Th.FB(9f),
                        new Rectangle(w - pad - 130, y, 116, 14), Th.Text, TextFormatFlags.Right);

                    // 进度条
                    var barBg = new Rectangle(pad + 22, y + 17, w - pad * 2 - 36, 4);
                    Th.FillRound(g, barBg, 2, Th.Line);
                    var bar = new Rectangle(pad + 22, y + 17, (int)((w - pad * 2 - 36) * pct), 4);
                    if (bar.Width > 0) Th.FillRound(g, bar, 2, Th.Bad);

                    // 子标题
                    string sub = string.Format("DPS {0}   峰值 {1}   最大 {2}",
                        Th.Money(entry.CurrentDps5s), Th.Money(entry.PeakBurst5s), Th.Money(entry.BiggestHit));
                    TextRenderer.DrawText(g, sub, Th.F(7.5f),
                        new Rectangle(pad + 22, y + 24, w - pad * 2 - 36, 12), Th.Sub, TextFormatFlags.Left);
                }
            }

            // ===== 治疗输出卡片 =====
            int healY = cardY + 200;
            int healCardH = 100;
            Card(g, pad, healY, w - pad * 2, healCardH);

            DrawIconBadge(g, pad + 12, healY + 10, Th.Good);
            TextRenderer.DrawText(g, "治疗输出", Th.FB(10f),
                new Rectangle(pad + 32, healY + 8, 200, 18), Th.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "HEALING OUT", Th.F(7f),
                new Rectangle(pad + 110, healY + 10, 200, 14), Th.Sub, TextFormatFlags.Left);

            var allHeal = GameEvents.DamageTracker.GetTopHealing();
            var topHeal = GameEvents.IsInParty
                ? allHeal.FindAll(h => GameEvents.IsPartyMember(h.PlayerName))
                : allHeal;

            if (topHeal.Count == 0)
            {
                TextRenderer.DrawText(g, "暂无治疗数据 — 本场战斗未产生治疗量", Th.F(8.5f),
                    new Rectangle(pad + 12, healY + 40, w - pad * 2 - 24, 16), Th.Sub, TextFormatFlags.Left);
            }
            else
            {
                long totalHeal = 0;
                foreach (var h in topHeal) totalHeal += h.TotalHeal;
                if (totalHeal <= 0) totalHeal = 1;

                int rh = 30;
                for (int i = 0; i < topHeal.Count && i < 3; i++)
                {
                    var entry = topHeal[i];
                    int y = healY + 36 + i * rh;
                    float pct = (float)entry.TotalHeal / totalHeal;

                    var vbar = new Rectangle(pad + 12, y + 2, 3, rh - 8);
                    Th.FillRound(g, vbar, 1, Th.Good);

                    TextRenderer.DrawText(g, entry.PlayerName, Th.FB(9f),
                        new Rectangle(pad + 22, y, 140, 14), Th.Text, TextFormatFlags.Left);

                    string healText = Th.Money(entry.TotalHeal) + "  " + (pct * 100).ToString("0") + "%";
                    TextRenderer.DrawText(g, healText, Th.FB(9f),
                        new Rectangle(w - pad - 130, y, 116, 14), Th.Text, TextFormatFlags.Right);

                    var barBg = new Rectangle(pad + 22, y + 16, w - pad * 2 - 36, 3);
                    Th.FillRound(g, barBg, 2, Th.Line);
                    var bar = new Rectangle(pad + 22, y + 16, (int)((w - pad * 2 - 36) * pct), 3);
                    if (bar.Width > 0) Th.FillRound(g, bar, 2, Th.Good);
                }
            }
        }

        private static void DrawBigStat(Graphics g, ref int x, string label, string value, Color color, int width)
        {
            TextRenderer.DrawText(g, value, Th.FB(18f),
                new Rectangle(x, 44, width - 10, 26), color, TextFormatFlags.Left);
            TextRenderer.DrawText(g, label, Th.F(8f),
                new Rectangle(x + 70, 52, 50, 14), Th.Sub, TextFormatFlags.Left);
            x += width;
        }

        private static void Card(Graphics g, int x, int y, int w, int h)
        {
            var r = new Rectangle(x, y, w, h);
            Th.FillRound(g, r, 8, Th.Card);
            Th.StrokeRound(g, r, 8, Th.Line);
        }

        private static void DrawIconBadge(Graphics g, int x, int y, Color color)
        {
            var r = new Rectangle(x, y, 16, 16);
            Th.FillRound(g, r, 4, Color.FromArgb(40, color));
            TextRenderer.DrawText(g, "✕", Th.FB(8f),
                new Rectangle(x, y - 1, 16, 16), color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void Render()
        {
            UpdateFilterButtons();
            _panel.Invalidate();
        }

        private void SetFilter(DamageStatsTracker.DamageFilter f)
        {
            GameEvents.DamageTracker.FilterMode = f;
            Render();
        }

        private void UpdateFilterButtons()
        {
            var cur = GameEvents.DamageTracker.FilterMode;
            _btnAll.Active = cur == DamageStatsTracker.DamageFilter.All;
            _btnPve.Active = cur == DamageStatsTracker.DamageFilter.Pve;
            _btnPvp.Active = cur == DamageStatsTracker.DamageFilter.Pvp;
        }

        public override void RefreshData() { Render(); }
    }

    /// <summary>Pill 样式筛选按钮。</summary>
    public class PillButton : Control
    {
        public string Text { get; set; }
        public bool Active { get; set; }
        public Action Clicked { get; set; }

        public PillButton(string text, int x, int y)
        {
            Text = text;
            Location = new Point(x, y);
            Size = new Size(58, 26);
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = Th.F(8.5f);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (Active)
            {
                Th.FillRound(g, r, 13, Th.Accent);
                TextRenderer.DrawText(g, Text, Th.FB(8.5f), r, Th.Bg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                Th.FillRound(g, r, 13, Th.Card);
                Th.StrokeRound(g, r, 13, Th.Line);
                TextRenderer.DrawText(g, Text, Th.F(8.5f), r, Th.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void OnClick(EventArgs e) { Clicked?.Invoke(); }
    }
}
