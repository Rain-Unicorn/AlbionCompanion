using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>总览：实时战斗/采集/收益汇总。</summary>
    public class OverviewTab : TabBase
    {
        private readonly StatCard _online;
        private readonly StatCard _combat;
        private readonly StatCard _gather;
        private readonly StatCard _income;
        private readonly SimpleTable _table;

        private readonly float[] _weights = new float[] { 0.7f, 0.7f, 1.6f, 0.9f };
        private readonly bool[] _right = new bool[] { false, false, false, true };

        public OverviewTab(Runtime runtime)
            : base(runtime)
        {
            _online = AddCard(0, 0, Th.Info);
            _combat = AddCard(252, 0, Th.Bad);
            _gather = AddCard(0, 108, Th.Good);
            _income = AddCard(252, 108, Th.Accent);

            Card(0, 216, 496, 36);
            CardText("实时动态（最近战斗/采集/收益事件）", 14, 224, 300, 18, Th.Text, 10f, true);

            Heads(new string[] { "时间", "类型", "详情", "数值" }, _weights, _right, 0, 256, 496);
            _table = Table(_weights, _right, 0, 278, 496, 450);
            _table.EmptyText = "等待游戏数据...（需开启抓包）";

            GameEvents.OnUpdate += delegate { try { if (IsHandleCreated) BeginInvoke(new MethodInvoker(RefreshData)); } catch { } };
        }

        private StatCard AddCard(int x, int y, Color color)
        {
            var card = new StatCard();
            card.Location = new Point(x, y);
            card.Size = new Size(236, 96);
            card.ValueColor = color;
            Controls.Add(card);
            return card;
        }

        public override void RefreshData()
        {
            // 在线时间
            _online.SetData("本次在线", SessionClock.Format(Rt.Elapsed),
                            "始于 " + Rt.SessionStart.ToString("HH:mm"));

            // 战斗
            long dmg = GameEvents.TotalDamage;
            long heal = GameEvents.TotalHealing;
            int kills = GameEvents.KillCount;
            int deaths = GameEvents.DeathCount;
            _combat.SetData("战斗统计", "伤害 " + Th.Money(dmg),
                            "治疗 " + Th.Money(heal) + "  ·  击杀 " + kills + "  ·  死亡 " + deaths);

            // 采集
            long totalAmt = 0;
            long totalFame = 0;
            // 快照在这里取一次，供下方三路归并复用；禁止在 Paint 内快照。
            var gList = GameEvents.SnapshotGathering();
            for (int i = 0; i < gList.Length; i++)
            {
                totalAmt += gList[i].Amount;
                totalFame += gList[i].Fame;
            }
            _gather.SetData("采集统计", Th.Money(totalAmt) + " 资源",
                            "声望 " + Th.Money(totalFame) + "  ·  " + gList.Length + " 次");

            // 收益
            var income = GameEvents.SnapshotIncome();
            long inc = GameEvents.TotalIncome;
            _income.SetData("银币收益", Th.Money(inc),
                            "明细 " + income.Length + " 条");

            // 合并最近动态：三份快照在锁外归并，UI 不再持有活集合
            var rows = new List<string[]>();
            int maxRows = 40;
            var combat = GameEvents.SnapshotCombat();
            int ci = combat.Length - 1;
            int gi = gList.Length - 1;
            int ii = income.Length - 1;

            while (rows.Count < maxRows && (ci >= 0 || gi >= 0 || ii >= 0))
            {
                DateTime ct = ci >= 0 ? combat[ci].Time : DateTime.MinValue;
                DateTime gt = gi >= 0 ? gList[gi].Time : DateTime.MinValue;
                DateTime it = ii >= 0 ? income[ii].Time : DateTime.MinValue;
                DateTime latest = ct;
                string type = "";
                string detail = "";
                string val = "";

                if (gt > latest) { latest = gt; }
                if (it > latest) { latest = it; }

                if (latest == ct && ci >= 0)
                {
                    var e = combat[ci]; ci--;
                    type = TypeText(e.Type);
                    detail = e.Target ?? "-";
                    val = e.Damage > 0 ? Th.Money(e.Damage) : "-";
                }
                else if (latest == gt && gi >= 0)
                {
                    var e = gList[gi]; gi--;
                    type = "采集";
                    detail = e.Resource ?? "-";
                    val = "+" + e.Amount;
                }
                else if (latest == it && ii >= 0)
                {
                    var e = income[ii]; ii--;
                    type = "收益";
                    detail = e.Source ?? "-";
                    val = "+" + Th.Money(e.Silver);
                }
                else break;

                rows.Add(new string[] { latest.ToString("HH:mm:ss"), type, detail, val });
            }
            _table.SetRows(rows, new List<System.Drawing.Image>());
        }

        private static string TypeText(string t)
        {
            switch (t)
            {
                case "damage": return "伤害";
                case "heal": return "治疗";
                case "kill": return "击杀";
                case "death": return "死亡";
                default: return t;
            }
        }
    }
}
