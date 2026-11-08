using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>跑商套利：读本地行情库，自动找跨城差价路线。</summary>
    public class TradeTab : TabBase
    {
        private readonly Label _status;
        private readonly SimpleTable _table;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly NameCache _cache;

        private readonly float[] _weights = new float[] { 1.5f, 1.3f, 1.3f, 0.95f, 0.75f };
        private readonly bool[] _right = new bool[] { false, false, false, true, true };

        private static string ShortCity(string city)
        {
            switch (city)
            {
                case "Caerleon": return "凯城";
                case "Bridgewatch": return "桥望";
                case "Martlock": return "马丁";
                case "Thetford": return "塞福";
                case "Fort Sterling": return "斯特";
                case "Lymhurst": return "林堡";
                default: return city;
            }
        }

        public TradeTab(Runtime runtime)
            : base(runtime)
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                         + @"\AlbionCompanion";
            _cache = NameCache.Load(System.IO.Path.Combine(dir, "itemnames.json"));

            Card(0, 0, 496, 60);
            CardText("跑商套利", 14, 8, 200, 20, Th.Text, 11f, true);
            _status = CardText("卖单扣 8% 交易税 + 2.5% 创建费；挂收购单另扣 2.5%",
                               14, 32, 466, 18, Th.Sub, 8f, false);

            Heads(new string[] { "物品", "收（城·价）", "卖（城·价）", "单件净利", "回报率" },
                  _weights, _right, 0, 72, 496, 36);
            _table = Table(_weights, _right, 0, 108, 496, 612);
            _table.EmptyText = "本地库还没有数据：先去「市场」页搜几个常用物品，\n这里会自动算出最赚的跨城路线";

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 60 * 1000;
            _timer.Tick += delegate { Render(); };
            _timer.Start();

            Render();
        }

        private void Render()
        {
            var all = Rt.Db.Rows;
            var routes = Arbitrage.Compute(all, 0.08, 0.025, 100);

            var rows = new List<string[]>();
            int shown = Math.Min(routes.Count, 50);
            for (int i = 0; i < shown; i++)
            {
                var r = routes[i];
                string display;
                if (!_cache.Map.TryGetValue(r.ItemId, out display) || string.IsNullOrEmpty(display))
                    display = r.ItemId;

                rows.Add(new string[]
                {
                    display,
                    ShortCity(r.BuyCity) + " " + Th.Price(r.BuyPrice),
                    ShortCity(r.SellCity) + " " + Th.Price(r.SellPrice),
                    "+" + Th.Price(r.NetProfit),
                    r.ReturnPct.ToString("0.0") + "%"
                });
            }
            _table.SetRows(rows);
            _status.Text = routes.Count + " 条路线 · 本地库 " + all.Count + " 行 · 60 秒自动刷新";
        }

        public override void RefreshData()
        {
            Render();
        }
    }
}
