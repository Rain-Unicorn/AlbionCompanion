using System;
using System.Collections.Generic;

namespace AlbionCompanion.Core.Income
{
    /// <summary>收益来源（设计 §8.3.1）。C3 仅用到 SilverPickup；其余枚举位为 C7 预留。</summary>
    public enum IncomeSource { SilverPickup, PartySilver, GatheringSale, Unknown }

    /// <summary>一条收益记录（金额均为显示单位：原始 FixPoint ÷ 10000）。</summary>
    public sealed class IncomeRecord
    {
        public DateTime TimeUtc;
        public IncomeSource Source;
        public string SourceLabel = "";    // 展示用："拾取银币" / "队伍分成" / "采集出售"
        public string Detail = "";         // 如物品名、目标名
        public long PickerId;             // 拾取者 ObjectId（pending 按 pickerId 分流用；正式入账后仍保留备查）
        public long SilverAfterTax;
        public long SilverBeforeTax;      // 仅 SilverPickup 有值，其余等于 AfterTax
        public long TaxPaid;              // cluster + guild + alliance 合计
        public bool Pending;              // true = 身份未确认期间挂起
        public string AttributionNote = ""; // 如 "p8=..."（倍率原值，不参与计算）
    }

    /// <summary>收益聚合结果（合计只含已确认条目；待确认单独计数）。</summary>
    public sealed class IncomeSummary
    {
        public long ConfirmedTotal;       // 已确认合计（银币）
        public long SelfTotal;            // 本地玩家已确认合计（C3 阶段 == ConfirmedTotal，C7 起细分）
        public int ConfirmedCount;
        public int UnconfirmedRecords;    // pending 条数
        public long PendingSilver;        // pending 合计（显示单位）
        public int RecordCount;
    }

    /// <summary>
    /// 收益唯一权威源（设计 §8.3.1），并承担 R1 的 pending 桶：
    /// 候选期间挂起的拾取记 Pending=true；身份确认后按 pickerId 分流（是我→转正，不是我→丢弃）。
    /// C3 先落地 pending/确认/聚合；JSONL 持久化与 CSV 导出在 C7 补齐。
    /// </summary>
    public static class IncomeTracker
    {
        private const int MaxConfirmed = 2000;
        private const int MaxPending = 1000;

        private static readonly object _lock = new object();
        private static readonly List<IncomeRecord> _confirmed = new List<IncomeRecord>();
        private static readonly List<IncomeRecord> _pending = new List<IncomeRecord>();

        public static long TotalSilver
        {
            get { lock (_lock) { return SumLocked(_confirmed); } }
        }

        /// <summary>正式入账（已确认是我的收益）。</summary>
        public static void AddConfirmed(IncomeRecord r)
        {
            if (r == null) return;
            lock (_lock)
            {
                r.Pending = false;
                _confirmed.Add(r);
                if (_confirmed.Count > MaxConfirmed) _confirmed.RemoveRange(0, _confirmed.Count - MaxConfirmed);
            }
            MirrorToGameEvents(r);
        }

        /// <summary>挂起入账（候选期间，金额未计入任何合计）。</summary>
        public static void AddPending(IncomeRecord r)
        {
            if (r == null) return;
            lock (_lock)
            {
                r.Pending = true;
                _pending.Add(r);
                if (_pending.Count > MaxPending) _pending.RemoveRange(0, _pending.Count - MaxPending);
            }
        }

        /// <summary>
        /// 身份已确认：pending 按 pickerId 分流——
        /// pickerId == selfObjectId → 转为正式入账（rename 而非丢弃，T-03）；
        /// 其余（含旧候选的误挂起）→ 整体丢弃。candidateId 仅用于日志。
        /// </summary>
        public static void ResolvePending(long selfObjectId, long? candidateId)
        {
            List<IncomeRecord> promoted = new List<IncomeRecord>();
            lock (_lock)
            {
                for (int i = 0; i < _pending.Count; i++)
                {
                    var r = _pending[i];
                    if (r.PickerId == selfObjectId) promoted.Add(r);
                }
                int dropped = _pending.Count - promoted.Count;
                _pending.Clear();
                foreach (var r in promoted)
                {
                    r.Pending = false;
                    _confirmed.Add(r);
                }
                if (_confirmed.Count > MaxConfirmed) _confirmed.RemoveRange(0, _confirmed.Count - MaxConfirmed);
                if (dropped > 0 && candidateId != null && candidateId != selfObjectId)
                    System.Diagnostics.Debug.WriteLine("[IncomeTracker] 身份确认：候选 " + candidateId + " 的 "
                        + dropped + " 笔挂起收益非本人，已丢弃");
            }
            // 锁外镜像，避免在 IncomeTracker 锁内触发 UI 回调链
            foreach (var r in promoted) MirrorToGameEvents(r);
        }

        /// <summary>候选被推翻：挂起条目整桶丢弃（设计 §3.1 原则 3）。</summary>
        public static void DiscardPending()
        {
            lock (_lock) { _pending.Clear(); }
        }

        public static IncomeSummary GetSummary()
        {
            lock (_lock)
            {
                var s = new IncomeSummary();
                s.ConfirmedTotal = SumLocked(_confirmed);
                s.SelfTotal = s.ConfirmedTotal; // C3：确认入口只有「是我」一条路径，二者恒等；C7 按来源细分
                s.ConfirmedCount = _confirmed.Count;
                s.UnconfirmedRecords = _pending.Count;
                s.PendingSilver = SumLocked(_pending);
                s.RecordCount = _confirmed.Count + _pending.Count;
                return s;
            }
        }

        /// <summary>快照：新在前，已确认与挂起条目按时间混排，最多 max 条。</summary>
        public static IncomeRecord[] Snapshot(int max)
        {
            if (max <= 0) return new IncomeRecord[0];
            lock (_lock)
            {
                var all = new List<IncomeRecord>(_confirmed.Count + _pending.Count);
                all.AddRange(_confirmed);
                all.AddRange(_pending);
                all.Sort(delegate (IncomeRecord a, IncomeRecord b) { return b.TimeUtc.CompareTo(a.TimeUtc); });
                int n = Math.Min(max, all.Count);
                return all.GetRange(0, n).ToArray();
            }
        }

        public static void Reset()
        {
            lock (_lock) { _confirmed.Clear(); _pending.Clear(); }
        }

        private static long SumLocked(List<IncomeRecord> list)
        {
            long sum = 0;
            for (int i = 0; i < list.Count; i++) sum += list[i].SilverAfterTax;
            return sum;
        }

        /// <summary>
        /// C3 过渡期镜像：IncomeTab/OverviewTab/悬浮条仍读 GameEvents.TotalIncome / SnapshotIncome，
        /// 在 UI 切到 IncomeTracker（C7）之前，正式入账同步镜像一份到旧通道，保证界面不空白。
        /// 挂起条目不镜像（未确认的钱不应出现在任何合计里）。
        /// </summary>
        private static void MirrorToGameEvents(IncomeRecord r)
        {
            GameEvents.AddIncome(new GameEvents.IncomeEntry
            {
                Time = r.TimeUtc.ToLocalTime(),
                Silver = r.SilverAfterTax,
                Source = string.IsNullOrEmpty(r.SourceLabel) ? "拾取银币" : r.SourceLabel
            });
        }
    }
}
