using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>本地行情库浏览：查看 MarketDb 所有记录（快照读，适配后台 worker 并发写）。</summary>
    public class LocalDbTab : TabBase
    {
        private readonly TextBox _filter;
        private readonly SimpleTable _table;
        private readonly Label _status;

        private readonly float[] _weights = new float[] { 1.5f, 1.2f, 0.9f, 0.8f, 0.8f, 1.2f };
        private readonly bool[] _right = new bool[] { false, false, false, true, true, true };

        public LocalDbTab(Runtime runtime)
            : base(runtime)
        {
            Card(0, 0, 580, 56);
            CardText("本地行情库", 14, 8, 200, 20, Th.Text, 11f, true);
            _status = CardText("共 0 条记录", 14, 30, 550, 18, Th.Sub, 8.5f, false);

            Card(0, 64, 580, 56);
            CardText("筛选（物品 ID / 中文名 / 城市片段）", 14, 70, 300, 18, Th.Sub, 8.5f, false);
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
            // MarketDb.Rows 现在是快照：后台 worker 写库时也不再出现 List 扩容撕裂。
            // 0 价（无上报）行在数据层已被过滤，这里不再靠显示层遮盖 0。
            var rows = db.Rows;
            var cells = new List<string[]>();
            var images = new List<Image>();
            string q = _filter.Text.Trim();
            int shown = 0;

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                string baseId = MarketDb.BaseOf(r.ItemId);
                string display = baseId;
                ItemIndex index = ItemIndex.Default;
                if (index != null)
                {
                    string name = index.DisplayNameOf(r.ItemId);
                    if (!string.IsNullOrEmpty(name)) display = name;
                }
                int enchant = MarketItemKey.EnchantOf(r.ItemId);
                if (enchant > 0) display = display + " +" + enchant;

                if (q.Length > 0)
                {
                    bool match = r.ItemId.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                 || baseId.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                 || display.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                 || r.City.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match) continue;
                }

                // 品质统一走 MarketQuality（1..5 = 普通/良好/杰出/精良/大师；非法值显示 —，不假装普通）
                string quality = MarketQuality.IsValid(r.Quality) ? MarketQuality.DisplayOf(r.Quality) : "—";
                cells.Add(new string[]
                {
                    display,
                    MarketCities.Normalize(r.City),
                    quality,
                    r.SellMin > 0 ? Th.Price(r.SellMin) : "—",
                    r.BuyMax > 0 ? Th.Price(r.BuyMax) : "—",
                    r.SellMin > 0 ? Th.DateText(r.SellMinDate) : "暂无上报"
                });
                images.Add(ItemIcons.Get(baseId, r.Quality));
                shown++;
            }

            _table.SetRows(cells, images);
            _status.Text = "共 " + rows.Count + " 条记录，当前显示 " + shown + " 条"
                           + (db.SkippedNoDataCount > 0
                               ? " · 数据层已过滤 " + db.SkippedNoDataCount + " 条两侧均无上报的记录"
                               : "");
        }

        public override void RefreshData()
        {
            Render();
        }
    }
}
