using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>收益台账：实时显示银币收入。</summary>
    public class IncomeTab : TabBase
    {
        private readonly Label _summary;
        private readonly SimpleTable _table;
        private readonly Timer _refresh;

        public IncomeTab(Runtime runtime)
            : base(runtime)
        {
            Card(0, 0, 580, 56);
            CardText("收益统计（实时抓包）", 14, 8, 300, 20, Th.Text, 11f, true);
            _summary = CardText("总收益：0 银币", 14, 30, 552, 18, Th.Accent, 10f, true);

            Card(0, 64, 580, 0);

            Heads(new string[] { "时间", "来源", "银币" },
                  new float[] { 0.8f, 1.2f, 1.0f },
                  new bool[] { false, false, true },
                  0, 80, 580, 36);
            _table = Table(new float[] { 0.8f, 1.2f, 1.0f },
                           new bool[] { false, false, true },
                           0, 104, 580, 520);
            _table.EmptyText = "等待收益数据...";

            GameEvents.OnUpdate += delegate { try { if (IsHandleCreated) BeginInvoke(new MethodInvoker(Render)); } catch { } };

            _refresh = new Timer();
            _refresh.Interval = 1000;
            _refresh.Tick += delegate { Render(); };
            _refresh.Start();
            Render();
        }

        private void Render()
        {
            _summary.Text = "总收益：" + Th.Money(GameEvents.TotalIncome) + " 银币";

            var rows = new List<string[]>();
            var list = GameEvents.SnapshotIncome();
            for (int i = list.Length - 1; i >= 0 && rows.Count < 100; i--)
            {
                var e = list[i];
                rows.Add(new string[]
                {
                    e.Time.ToString("HH:mm:ss"),
                    e.Source,
                    "+" + Th.Money(e.Silver)
                });
            }
            _table.SetRows(rows, new List<Image>());
        }

        public override void RefreshData() { Render(); }
    }
}
