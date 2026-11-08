using System;
using System.Drawing;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>配装成本核算：各部位单价合计 + 回本估算。</summary>
    public class BuildTab : TabBase
    {
        private readonly TextBox[] _costs = new TextBox[8];
        private readonly Label _totalValue;
        private readonly Label _totalHint;
        private readonly Label _result;
        private readonly TextBox _loot;
        private bool _syncing;

        private readonly string[] _slotNames =
            { "主手武器", "副手", "头部", "护甲", "鞋子", "披风", "坐骑", "背包" };

        public BuildTab(Runtime runtime)
            : base(runtime)
        {
            Card(0, 0, 496, 190);
            CardText("装备成本（银币）", 14, 8, 240, 20, Th.Text, 10.5f, true);

            var clear = Ui.Secondary("清空", 400, 8, 82, 26);
            clear.Click += delegate
            {
                for (int i = 0; i < _costs.Length; i++)
                {
                    _costs[i].Text = "";
                    Rt.Slots[i].Cost = 0;
                }
                RefreshData();
            };
            Controls.Add(clear);

            for (int i = 0; i < _slotNames.Length; i++)
            {
                int col = i / 4;
                int row = i % 4;
                int x = 14 + col * 244;
                int y = 40 + row * 38;

                var caption = Ui.Caption(_slotNames[i], x, y + 5);
                caption.Size = new Size(72, 18);
                caption.AutoSize = false;
                Controls.Add(caption);

                var box = Ui.Input(x + 76, y, 152, 26);
                int index = i;
                box.TextChanged += delegate(object sender, EventArgs e) { SyncSlot(index, sender as TextBox); };
                Controls.Add(box);
                _costs[i] = box;
            }

            Card(0, 200, 496, 110);
            CardText("配装总成本", 14, 208, 200, 18, Th.Sub, 8.5f, false);
            _totalValue = CardText("0", 14, 226, 260, 42, Th.Accent, 18f, true);
            _totalHint = CardText("把各部位单价填进来即可合计", 190, 240, 292, 20, Th.Sub, 8f, false);

            Card(0, 320, 496, 110);
            CardText("每次击杀平均掉落（银币）", 14, 330, 240, 18, Th.Sub, 8.5f, false);
            _loot = Ui.Input(14, 350, 130, 28);
            _loot.Text = "7000";
            Controls.Add(_loot);
            _result = CardText("—", 156, 352, 320, 26, Th.Good, 11f, true);

            CardText("用于估算这套装备刷怪多少次可以回本", 14, 386, 400, 18, Th.Sub, 8f, false);

            Card(0, 440, 496, 150);
            CardText("说明", 14, 450, 100, 20, Th.Text, 10.5f, true);
            CardText("1. 单价请填写你所在服务器的实际市价，可先在「市场」页查询再填入。\r\n" +
                     "2. 总成本 = 各部位单价之和，含坐骑与背包时更适合估算刷怪套装。\r\n" +
                     "3. 回本次数 = 总成本 ÷ 每次击杀平均掉落（向上取整），仅供参考。\r\n" +
                     "4. 本页只做本地计算，不会上传任何数据。",
                     14, 474, 466, 110, Th.Sub, 8.5f, false);

            _loot.TextChanged += delegate { UpdateNumbers(); };
        }

        private void SyncSlot(int index, TextBox box)
        {
            if (_syncing) return;
            long cost;
            if (!long.TryParse(box.Text.Trim(), out cost) || cost < 0) cost = 0;
            Rt.Slots[index].Cost = cost;
            UpdateNumbers();
        }

        private void UpdateNumbers()
        {
            long total = Rt.LoadoutTotal;
            _totalValue.Text = Th.Num(total);
            _totalHint.Text = total > 0
                ? "折合约 " + Th.Money(total) + " 银币"
                : "把各部位单价填进来即可合计";

            long loot;
            if (!long.TryParse(_loot.Text.Trim(), out loot) || loot < 0) loot = 0;
            Rt.LootPerKill = loot;
            int kills = BuildCost.BreakEvenKills(total, loot);
            _result.Text = kills > 0 ? "约 " + kills + " 次回本" : "填写成本与掉落后自动估算";
        }

        public override void RefreshData()
        {
            _syncing = true;
            for (int i = 0; i < _costs.Length; i++)
            {
                if (_costs[i].Focused) continue;
                long cost = Rt.Slots[i].Cost;
                _costs[i].Text = cost > 0 ? cost.ToString() : "";
            }
            _syncing = false;
            UpdateNumbers();
        }
    }
}
