using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>本地行情库浏览：查看 MarketDb 所有记录。</summary>
    public class LocalDbTab : TabBase
    {
        private readonly TextBox _filter;
        private readonly SimpleTable _table;
        private readonly Label _status;

        private readonly float[] _weights = new float[] { 1.5f, 1.2f, 0.6f, 0.8f, 0.8f, 1.2f };
        private readonly bool[] _right = new bool[] { false, false, false, true, true, true };

        public LocalDbTab(Runtime runtime)
            : base(runtime)
        {
            Card(0, 0, 580, 56);
            CardText("本地行情库", 14, 8, 200, 20, Th.Text, 11f, true);
            _status = CardText("共 0 条记录", 14, 30, 466, 18, Th.Sub, 8.5f, false);

            Card(0, 64, 580, 56);
            CardText("筛选（物品 ID 或中文名片段）", 14, 70, 300, 18, Th.Sub, 8.5f, false);
            _filter = Ui.Input(14, 90, 466, 26);
            _filter.TextChanged += delegate { Render(); };

            Heads(new string[] { "物品", "城市", "品质", "最低卖价", "最高买价", "更新" },
                  _weights, _right, 0, 130, 580, 36);
            _table = Table(_weights, _right, 0, 154, 580, 470);
            _table.EmptyText = "本地库暂无数据，先去市场页查询";
            // 图标异步下载完成后自动重绘
            var iconTimer = new Timer();
            iconTimer.Interval = 800;
            iconTimer.Tick += delegate { try { iconTimer.Stop(); Render(); } catch { } };
            ItemIcons.IconsReady += delegate
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(new MethodInvoker(delegate { try { if (!iconTimer.Enabled) iconTimer.Start(); } catch { } })); } catch { }
            };
        }

        private void Render()
        {
            var db = Rt.Db;
            var rows = new List<string[]>();
            var images = new List<Image>();
            string q = _filter.Text.Trim();
            int shown = 0;

            for (int i = 0; i < db.Rows.Count; i++)
            {
                var r = db.Rows[i];
                string baseId = MarketDb.BaseOf(r.ItemId);
                string display = baseId;

                if (q.Length > 0)
                {
                    bool match = baseId.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match) continue;
                }

                string quality = r.Quality <= 0 ? "普通" : new string[] { "", "良好", "杰出", "精良", "史诗", "传说" }[r.Quality];
                rows.Add(new string[]
                {
                    display,
                    r.City,
                    quality,
                    Th.Price(r.SellMin),
                    Th.Price(r.BuyMax),
                    Th.DateText(r.SellMinDate)
                });
                images.Add(ItemIcons.Get(baseId, r.Quality));
                shown++;
            }
            _table.SetRows(rows, images);
            _status.Text = "共 " + db.Rows.Count + " 条记录，当前显示 " + shown + " 条";
        }

        public override void RefreshData()
        {
            Render();
        }
    }
}
