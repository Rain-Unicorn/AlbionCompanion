using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>采集统计：实时显示采集到的资源、数量和声望。</summary>
    public class HarvestTab : TabBase
    {
        private readonly Label _summary;
        private readonly SimpleTable _table;
        private readonly Timer _refresh;
        private readonly Label _breakdown;

        public HarvestTab(Runtime runtime)
            : base(runtime)
        {
            Card(0, 0, 580, 56);
            CardText("采集统计（实时抓包）", 14, 8, 300, 20, Th.Text, 11f, true);
            _summary = CardText("总计：0 个资源 · 0 声望", 14, 30, 552, 18, Th.Accent, 10f, true);

            Card(0, 64, 580, 36);
            _breakdown = CardText("矿石 0  ·  纤维 0  ·  木材 0  ·  皮革 0  ·  石头 0",
                14, 74, 552, 18, Th.Sub, 9f, false);

            Heads(new string[] { "时间", "资源", "数量", "声望" },
                  new float[] { 0.8f, 1.2f, 0.6f, 0.8f },
                  new bool[] { false, false, true, true },
                  0, 108, 580, 36);
            _table = Table(new float[] { 0.8f, 1.2f, 0.6f, 0.8f },
                           new bool[] { false, false, true, true },
                           0, 132, 580, 490);
            _table.EmptyText = "等待采集数据...";

            GameEvents.OnUpdate += delegate { try { if (IsHandleCreated) BeginInvoke(new MethodInvoker(Render)); } catch { } };

            _refresh = new Timer();
            _refresh.Interval = 1000;
            _refresh.Tick += delegate { Render(); };
            _refresh.Start();
            Render();
        }

        private void Render()
        {
            long totalAmount = 0;
            long totalFame = 0;
            long ore = 0, fiber = 0, wood = 0, hide = 0, stone = 0;

            var list = GameEvents.Gathering;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                totalAmount += e.Amount;
                totalFame += e.Fame;
                string r = (e.Resource ?? "");
                if (r.Contains("矿石") || r.Contains("ore")) ore += e.Amount;
                else if (r.Contains("纤维") || r.Contains("fiber")) fiber += e.Amount;
                else if (r.Contains("木材") || r.Contains("wood")) wood += e.Amount;
                else if (r.Contains("皮革") || r.Contains("hide") || r.Contains("leather")) hide += e.Amount;
                else if (r.Contains("石头") || r.Contains("stone")) stone += e.Amount;
            }

            _summary.Text = "总计：" + Th.Money(totalAmount) + " 个资源 · " + Th.Money(totalFame) + " 声望";
            _breakdown.Text = "矿石 " + ore + "  ·  纤维 " + fiber + "  ·  木材 " + wood + "  ·  皮革 " + hide + "  ·  石头 " + stone;

            var rows = new List<string[]>();
            for (int i = list.Count - 1; i >= 0 && rows.Count < 100; i--)
            {
                var e = list[i];
                rows.Add(new string[]
                {
                    e.Time.ToString("HH:mm:ss"),
                    e.Resource ?? "-",
                    "+" + e.Amount,
                    e.Fame > 0 ? "+" + e.Fame : "-"
                });
            }
            _table.SetRows(rows, new List<Image>());
        }

        public override void RefreshData() { Render(); }
    }
}
