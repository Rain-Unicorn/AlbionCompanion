using System;
using System.Collections.Generic;

namespace AlbionCompanion.Core.Gathering
{
    /// <summary>一条采集记录（evt=61 HarvestFinished）。</summary>
    public sealed class GatheringRecord
    {
        public DateTime TimeUtc;
        public int ItemId;
        public string Resource = "";   // 展示名（ItemDatabase.GetDisplayName）
        public int Amount;
        public int Fame;
    }

    /// <summary>采集聚合结果（设计 §8.6）：总数量、总声望、次数、按资源类别小计。</summary>
    public sealed class GatheringSummary
    {
        public long TotalAmount;
        public long TotalFame;
        public int Count;
        public Dictionary<string, long> ByCategory = new Dictionary<string, long>();
    }

    /// <summary>
    /// 采集唯一权威源（设计 §8.6，与 IncomeTracker 同构）。
    /// 类别归类复用 GatherCategories——§8.6 明令禁止重写 HarvestTab 的 Contains("矿石") 字符串匹配。
    /// evt=72 声望回流走 <see cref="UpdateLastFame"/>（与 GameEvents.TryUpdateLastGatheringFame 同口径：覆盖最近一条的 Fame）。
    /// </summary>
    public static class GatheringTracker
    {
        private const int MaxRecords = 2000;

        private static readonly object _lock = new object();
        private static readonly List<GatheringRecord> _records = new List<GatheringRecord>();

        public static void Add(GatheringRecord r)
        {
            if (r == null) return;
            lock (_lock)
            {
                _records.Add(r);
                if (_records.Count > MaxRecords) _records.RemoveRange(0, _records.Count - MaxRecords);
            }
        }

        /// <summary>evt=72 声望回流：覆盖最近一条采集记录的 Fame（与 GameEvents 侧同语义）。</summary>
        public static bool UpdateLastFame(int fame)
        {
            lock (_lock)
            {
                if (_records.Count == 0) return false;
                _records[_records.Count - 1].Fame = fame;
                return true;
            }
        }

        public static GatheringSummary GetSummary()
        {
            var s = new GatheringSummary();
            lock (_lock)
            {
                s.Count = _records.Count;
                for (int i = 0; i < _records.Count; i++)
                {
                    s.TotalAmount += _records[i].Amount;
                    s.TotalFame += _records[i].Fame;
                    string cat = CategoryOf(_records[i].ItemId);
                    s.ByCategory[cat] = (s.ByCategory.ContainsKey(cat) ? s.ByCategory[cat] : 0L) + _records[i].Amount;
                }
            }
            return s;
        }

        /// <summary>快照：新在前，最多 max 条。</summary>
        public static GatheringRecord[] Snapshot(int max)
        {
            if (max <= 0) return new GatheringRecord[0];
            lock (_lock)
            {
                int n = Math.Min(max, _records.Count);
                var arr = new GatheringRecord[n];
                for (int i = 0; i < n; i++) arr[i] = _records[_records.Count - 1 - i];
                return arr;
            }
        }

        public static void Reset()
        {
            lock (_lock) { _records.Clear(); }
        }

        /// <summary>类别归类复用 GatherCategories（§8.6）；未知资源归「其他」。</summary>
        private static string CategoryOf(int itemId)
        {
            switch (GatherCategories.GetResourceType(itemId))
            {
                case GatherCategories.ResourceType.Ore: return "矿石";
                case GatherCategories.ResourceType.Fiber: return "纤维";
                case GatherCategories.ResourceType.Wood: return "木材";
                case GatherCategories.ResourceType.Hide: return "皮革";
                case GatherCategories.ResourceType.Stone: return "石头";
                default: return "其他";
            }
        }
    }
}
