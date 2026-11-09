using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AlbionCompanion.Core;
using AlbionCompanion.Core.Gathering;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>
    /// 采集统计。
    /// 唯一数据源 = <see cref="GatheringTracker"/>（与总览采集卡片同源）：
    /// 旧实现这里读 GameEvents.SnapshotGathering，总览读 GatheringTracker，而 evt=61（采集）
    /// 与 evt=72（声望回流）跨线程落账，两处各自求和在时序上会分裂（一边有记录一边没声望）。
    /// 现在大数字/类别小计只来自 GetSummary()，明细只来自 Snapshot()，绝不自己遍历求和。
    /// </summary>
    public class HarvestTab : TabBase
    {
        private static readonly string[] HeadTitles = new string[] { "时间", "资源", "类别", "数量", "声望" };
        private static readonly float[] HeadWeights = new float[] { 0.7f, 1.3f, 0.8f, 0.5f, 0.6f };
        private static readonly bool[] HeadRight = new bool[] { false, false, false, true, true };

        private readonly Label _summary;
        private readonly Label _breakdown;
        private readonly Label _diagnostics;
        private readonly SimpleTable _table;
        private readonly Timer _refresh;

        public HarvestTab(Runtime runtime)
            : base(runtime)
        {
            Card(0, 0, 580, 56);
            CardText("采集统计（实时抓包）", 14, 8, 300, 20, Th.Text, 11f, true);
            _summary = CardText("总计：0 个资源 · 0 声望 · 0 次", 14, 30, 552, 18, Th.Accent, 10f, true);

            Card(0, 64, 580, 48);
            _breakdown = CardText(
                "矿石 0  ·  纤维 0  ·  木材 0  ·  皮革 0  ·  石头 0  ·  未分类 0",
                14, 72, 552, 18, Th.Sub, 9f, false);
            _diagnostics = CardText("", 14, 92, 552, 16, Th.Sub, 8f, false);

            Heads(HeadTitles, HeadWeights, HeadRight, 0, 116, 580, 0);
            _table = Table(HeadWeights, HeadRight, 0, 140, 580, 482);
            _table.EmptyText = "暂无采集记录：开启抓包后完成一次采集（evt=61）即出现统计";

            // 刷新只用于触发重绘：数据永远从 GatheringTracker 现取，避免 UI 持有第二份口径
            GameEvents.OnUpdate += delegate
            {
                try { if (IsHandleCreated) BeginInvoke(new MethodInvoker(Render)); } catch { }
            };

            _refresh = new Timer();
            _refresh.Interval = 1000;
            _refresh.Tick += delegate { Render(); };
            _refresh.Start();
            Render();
        }

        private void Render()
        {
            GatheringSummary summary = GatheringTracker.GetSummary();

            if (summary.Count == 0)
            {
                _summary.Text = "暂无采集数据 · 等待 evt=61（HarvestFinished）";
                _breakdown.Text = "矿石 —  ·  纤维 —  ·  木材 —  ·  皮革 —  ·  石头 —  ·  未分类 —";
            }
            else
            {
                _summary.Text = "总计：" + Th.Money(summary.TotalAmount) + " 个资源 · "
                                + Th.Money(summary.TotalFame) + " 声望 · " + summary.Count + " 次";
                _breakdown.Text =
                    GatherCategories.CategoryOre + " " + AmountOf(summary, GatherCategories.CategoryOre)
                    + "  ·  " + GatherCategories.CategoryFiber + " " + AmountOf(summary, GatherCategories.CategoryFiber)
                    + "  ·  " + GatherCategories.CategoryWood + " " + AmountOf(summary, GatherCategories.CategoryWood)
                    + "  ·  " + GatherCategories.CategoryHide + " " + AmountOf(summary, GatherCategories.CategoryHide)
                    + "  ·  " + GatherCategories.CategoryStone + " " + AmountOf(summary, GatherCategories.CategoryStone)
                    + "  ·  未分类 " + summary.UnclassifiedAmount
                    + (summary.UnclassifiedCount > 0 ? "（" + summary.UnclassifiedCount + " 次）" : "");
            }

            _diagnostics.Text = BuildDiagnostics(summary);

            // 明细与汇总同源：Snapshot 已是「新在前」，不再自己遍历求和
            GatheringRecord[] list = GatheringTracker.Snapshot(100);
            var rows = new List<string[]>();
            for (int i = 0; i < list.Length; i++)
            {
                GatheringRecord record = list[i];
                rows.Add(new string[]
                {
                    ToLocalTime(record.TimeUtc).ToString("HH:mm:ss"),
                    string.IsNullOrEmpty(record.Resource) ? "Item " + record.ItemId : record.Resource,
                    CategoryLabel(record.ItemId),
                    "+" + record.Amount,
                    record.Fame > 0 ? "+" + record.Fame : "-"
                });
            }
            _table.SetRows(rows, new List<Image>());
        }

        /// <summary>防护/数据诚实性提示：任何被丢弃/未归属的数据都要在 UI 上可见，绝不静默。</summary>
        private static string BuildDiagnostics(GatheringSummary summary)
        {
            var notes = new List<string>();
            if (summary.UnclassifiedCount > 0)
                notes.Add("未分类 " + summary.UnclassifiedCount + " 条（物品元数据缺失，不臆造类别）");
            if (summary.PendingFameCount > 0)
                notes.Add("待归属声望 " + summary.PendingFameCount + " 条（等下一次 evt=61）");
            if (summary.UnattributedFameCount > 0)
                notes.Add("未归属声望 " + summary.UnattributedFameCount + " 次");
            if (summary.DuplicateAdds > 0)
                notes.Add("已去重采集 " + summary.DuplicateAdds + " 条");
            if (summary.OutOfOrderAdds > 0)
                notes.Add("乱序记录 " + summary.OutOfOrderAdds + " 条");
            if (summary.DuplicateFameUpdates > 0)
                notes.Add("重复声望更新 " + summary.DuplicateFameUpdates + " 次");
            if (summary.TrimmedRecords > 0)
                notes.Add("超出上限已裁剪 " + summary.TrimmedRecords + " 条");
            return notes.Count == 0 ? "" : string.Join("  ·  ", notes.ToArray());
        }

        private static long AmountOf(GatheringSummary summary, string category)
        {
            long amount;
            return summary.ByCategory.TryGetValue(category, out amount) ? amount : 0L;
        }

        private static string CategoryLabel(int itemId)
        {
            GatherCategories.ResourceType type = GatherCategories.GetResourceType(itemId);
            if (type == GatherCategories.ResourceType.Unknown) return "未分类";
            return GatherCategories.CategoryName(type);
        }

        private static DateTime ToLocalTime(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Utc) return utc.ToLocalTime();
            return utc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime()
                : utc;
        }

        public override void RefreshData() { Render(); }
    }
}
